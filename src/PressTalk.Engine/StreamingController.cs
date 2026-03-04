using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Audio;
using PressTalk.Contracts.Pipeline;
using PressTalk.Contracts.Session;

namespace PressTalk.Engine;

public sealed class StreamingController
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(300);

    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IStreamingPipeline _pipeline;
    private readonly SessionStateMachine _stateMachine;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _streamLoopCts;
    private Task? _streamLoopTask;
    private string? _sessionId;
    private string _languageHint = "auto";
    private IReadOnlyList<string> _hotwords = [];
    private bool _enableSpeakerDiarization;
    private int _consumedSamples;

    public StreamingController(
        IAudioCaptureService audioCaptureService,
        IStreamingPipeline pipeline,
        SessionStateMachine? stateMachine = null,
        Action<string>? log = null)
    {
        _audioCaptureService = audioCaptureService;
        _pipeline = pipeline;
        _log = log;
        _stateMachine = stateMachine ?? new SessionStateMachine(log);
    }

    public event Action<StreamingAsrResult>? ResultUpdated;

    public SessionState CurrentState => _stateMachine.CurrentState;

    public string? CurrentSessionId => _sessionId;

    public async Task<string> StartStreamingSessionAsync(
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken)
    {
        _stateMachine.Transition(SessionTrigger.StartStreaming);

        _sessionId = Guid.NewGuid().ToString("N");
        _languageHint = string.IsNullOrWhiteSpace(languageHint) ? "auto" : languageHint.Trim();
        _hotwords = hotwords;
        _enableSpeakerDiarization = enableSpeakerDiarization;
        _consumedSamples = 0;

        await _audioCaptureService.StartCaptureAsync(_sessionId, cancellationToken);
        await _pipeline.StartSessionAsync(
            _sessionId,
            _languageHint,
            _hotwords,
            _enableSpeakerDiarization,
            cancellationToken);

        _streamLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _streamLoopTask = RunStreamingLoopAsync(_sessionId, _streamLoopCts.Token);

        _log?.Invoke(
            $"[Engine.StreamingController] start session={_sessionId}, lang={_languageHint}, hotwords={_hotwords.Count}, speaker={_enableSpeakerDiarization}");
        return _sessionId;
    }

    public async Task<StreamingAsrResult> StopStreamingSessionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            throw new InvalidOperationException("No active streaming session.");
        }

        _stateMachine.Transition(SessionTrigger.StopStreaming);
        var sessionId = _sessionId;

        try
        {
            if (_streamLoopCts is not null)
            {
                _streamLoopCts.Cancel();
            }

            if (_streamLoopTask is not null)
            {
                try
                {
                    await _streamLoopTask;
                }
                catch (OperationCanceledException)
                {
                    // Ignore normal cancellation.
                }
            }

            var captured = await _audioCaptureService.StopCaptureAsync(sessionId, cancellationToken);
            if (captured.AudioSamples.Length > _consumedSamples)
            {
                var tail = captured.AudioSamples.Slice(_consumedSamples);
                if (tail.Length > 0)
                {
                    var tailResult = await _pipeline.PushChunkAsync(
                        sessionId,
                        tail,
                        captured.SampleRate,
                        cancellationToken);
                    if (HasVisibleUpdate(tailResult))
                    {
                        _stateMachine.Transition(SessionTrigger.StreamingChunk);
                        ResultUpdated?.Invoke(tailResult);
                    }
                }
            }

            var finalResult = await _pipeline.EndSessionAsync(sessionId, cancellationToken);
            _stateMachine.Transition(SessionTrigger.AsrComplete);
            _stateMachine.Transition(SessionTrigger.CommitComplete);
            ResultUpdated?.Invoke(finalResult);

            return finalResult;
        }
        catch
        {
            _stateMachine.Transition(SessionTrigger.Error);
            throw;
        }
        finally
        {
            _stateMachine.Transition(SessionTrigger.Reset);
            _streamLoopCts?.Dispose();
            _streamLoopCts = null;
            _streamLoopTask = null;
            _sessionId = null;
            _languageHint = "auto";
            _hotwords = [];
            _enableSpeakerDiarization = false;
            _consumedSamples = 0;
        }
    }

    private async Task RunStreamingLoopAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PushInterval, cancellationToken);

            if (!_audioCaptureService.TryGetIncrementalChunk(
                    _consumedSamples,
                    minimumSamples: 1,
                    out var chunk,
                    out var totalSamples))
            {
                continue;
            }

            if (chunk.AudioSamples.Length == 0)
            {
                continue;
            }

            _consumedSamples = Math.Max(_consumedSamples, totalSamples);
            var result = await _pipeline.PushChunkAsync(
                sessionId,
                chunk.AudioSamples,
                chunk.SampleRate,
                cancellationToken);

            if (!HasVisibleUpdate(result))
            {
                continue;
            }

            _stateMachine.Transition(SessionTrigger.StreamingChunk);
            ResultUpdated?.Invoke(result);
        }
    }

    private static bool HasVisibleUpdate(StreamingAsrResult result)
    {
        return result.PreviewText.Length > 0
            || result.ConfirmedText.Length > 0
            || result.DeltaText.Length > 0
            || result.SpeakerSegments.Count > 0;
    }
}

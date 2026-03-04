using PressTalk.Contracts.Asr;
using PressTalk.Contracts.Audio;
using PressTalk.Contracts.Pipeline;
using PressTalk.Contracts.Session;

namespace PressTalk.Engine;

public sealed class StreamingController
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IStreamingPipeline _pipeline;
    private readonly SessionStateMachine _stateMachine;
    private readonly Action<string>? _log;
    private readonly TimeSpan _pushInterval;
    private readonly SemaphoreSlim _pipelineCallLock = new(1, 1);

    private CancellationTokenSource? _streamLoopCts;
    private Task? _streamLoopTask;
    private string? _sessionId;
    private string _languageHint = "auto";
    private IReadOnlyList<string> _hotwords = [];
    private bool _enableSpeakerDiarization;
    private int _consumedSamples;
    private Exception? _streamLoopFailure;

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
        _pushInterval = ResolvePushInterval();
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
        _streamLoopFailure = null;

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

            await _pipelineCallLock.WaitAsync(cancellationToken);
            try
            {
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

                if (_streamLoopFailure is not null)
                {
                    _log?.Invoke($"[Engine.StreamingController] stream loop failed before stop: {_streamLoopFailure.Message}");
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
            finally
            {
                _pipelineCallLock.Release();
            }
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
            _streamLoopFailure = null;
        }
    }

    private async Task RunStreamingLoopAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_pushInterval, cancellationToken);

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
            StreamingAsrResult result;
            try
            {
                await _pipelineCallLock.WaitAsync(cancellationToken);
                try
                {
                    result = await _pipeline.PushChunkAsync(
                        sessionId,
                        chunk.AudioSamples,
                        chunk.SampleRate,
                        CancellationToken.None);
                }
                finally
                {
                    _pipelineCallLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _streamLoopFailure = ex;
                _log?.Invoke($"[Engine.StreamingController] stream loop chunk failed: {ex.Message}");
                break;
            }

            if (!HasVisibleUpdate(result))
            {
                continue;
            }

            if (CurrentState != SessionState.Streaming || !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                _log?.Invoke("[Engine.StreamingController] late streaming result ignored after stop");
                break;
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

    private static TimeSpan ResolvePushInterval()
    {
        var raw = Environment.GetEnvironmentVariable("PRESSTALK_STREAM_PUSH_MS");
        if (int.TryParse(raw, out var ms))
        {
            return TimeSpan.FromMilliseconds(Math.Clamp(ms, 200, 1200));
        }

        return TimeSpan.FromMilliseconds(450);
    }
}

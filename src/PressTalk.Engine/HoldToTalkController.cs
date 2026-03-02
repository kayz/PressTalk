using PressTalk.Contracts.Audio;
using PressTalk.Contracts.Pipeline;
using PressTalk.Contracts.Session;

namespace PressTalk.Engine;

public sealed class HoldToTalkController
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IPressTalkPipeline _pipeline;
    private readonly SessionStateMachine _stateMachine;
    private readonly Action<string>? _log;

    private string? _sessionId;
    private string _languageHint = "auto";

    public HoldToTalkController(
        IAudioCaptureService audioCaptureService,
        IPressTalkPipeline pipeline,
        SessionStateMachine? stateMachine = null,
        Action<string>? log = null)
    {
        _audioCaptureService = audioCaptureService;
        _pipeline = pipeline;
        _log = log;
        _stateMachine = stateMachine ?? new SessionStateMachine(log);
    }

    public SessionState CurrentState => _stateMachine.CurrentState;

    public string? CurrentSessionId => _sessionId;

    public async Task<string> OnPressAsync(string languageHint, CancellationToken cancellationToken)
    {
        _log?.Invoke("[Engine.Controller] OnPress received");
        _stateMachine.Transition(SessionTrigger.Press);

        _sessionId = Guid.NewGuid().ToString("N");
        _languageHint = string.IsNullOrWhiteSpace(languageHint) ? "auto" : languageHint.Trim();
        _log?.Invoke($"[Engine.Controller] session created, session={_sessionId}, lang={_languageHint}");

        await _audioCaptureService.StartCaptureAsync(_sessionId, cancellationToken);
        _log?.Invoke($"[Engine.Controller] capture started, session={_sessionId}");

        return _sessionId;
    }

    public async Task<PressTalkResult> OnReleaseAsync(
        CancellationToken cancellationToken,
        bool isStickyDictationMode = false,
        bool enableSemanticEnhancement = false)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            throw new InvalidOperationException("No active session. Call OnPressAsync first.");
        }

        _log?.Invoke($"[Engine.Controller] OnRelease received, session={_sessionId}");
        _stateMachine.Transition(SessionTrigger.Release);

        try
        {
            var captured = await _audioCaptureService.StopCaptureAsync(_sessionId, cancellationToken);
            _log?.Invoke($"[Engine.Controller] capture stopped, samples={captured.AudioSamples.Length}, sampleRate={captured.SampleRate}, durationMs={captured.Duration.TotalMilliseconds:F1}");

            var request = new PressTalkRequest(
                SessionId: _sessionId,
                AudioSamples: captured.AudioSamples,
                SampleRate: captured.SampleRate,
                LanguageHint: _languageHint,
                IsStickyDictationMode: isStickyDictationMode,
                EnableSemanticEnhancement: enableSemanticEnhancement);

            _log?.Invoke($"[Engine.Controller] pipeline start, session={_sessionId}");
            var result = await _pipeline.ProcessAsync(request, cancellationToken);
            _log?.Invoke($"[Engine.Controller] pipeline done, session={_sessionId}");

            _stateMachine.Transition(SessionTrigger.AsrComplete);
            _stateMachine.Transition(SessionTrigger.CommitComplete);

            return result;
        }
        catch
        {
            _stateMachine.Transition(SessionTrigger.Error);
            _log?.Invoke($"[Engine.Controller] error, session={_sessionId}");
            throw;
        }
        finally
        {
            _stateMachine.Transition(SessionTrigger.Reset);
            _log?.Invoke($"[Engine.Controller] reset, session={_sessionId}");
            _sessionId = null;
            _languageHint = "auto";
        }
    }
}

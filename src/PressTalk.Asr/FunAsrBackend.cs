using PressTalk.Contracts.Asr;

namespace PressTalk.Asr;

public sealed class FunAsrBackend : IStreamingAsrBackend
{
    private readonly FunAsrRuntimeClient _runtime;
    private readonly Action<string>? _log;

    public FunAsrBackend(
        FunAsrRuntimeClient runtime,
        Action<string>? log = null)
    {
        _runtime = runtime;
        _log = log;
    }

    public string Name => "funasr-streaming";

    public async Task StartStreamingSessionAsync(
        string sessionId,
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken)
    {
        _log?.Invoke(
            $"[ASR.FunASR] start session={sessionId}, lang={languageHint}, hotwords={hotwords.Count}, speaker={enableSpeakerDiarization}");
        await _runtime.StartStreamingSessionAsync(
            sessionId,
            languageHint,
            hotwords,
            enableSpeakerDiarization,
            cancellationToken);
    }

    public async Task<StreamingAsrResult> PushAudioChunkAsync(
        string sessionId,
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        return await _runtime.PushAudioChunkAsync(sessionId, audioSamples, sampleRate, cancellationToken);
    }

    public async Task<StreamingAsrResult> EndStreamingSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        _log?.Invoke($"[ASR.FunASR] end session={sessionId}");
        return await _runtime.EndStreamingSessionAsync(sessionId, cancellationToken);
    }
}

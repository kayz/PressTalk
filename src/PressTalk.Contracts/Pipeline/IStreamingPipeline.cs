using PressTalk.Contracts.Asr;

namespace PressTalk.Contracts.Pipeline;

public interface IStreamingPipeline
{
    Task StartSessionAsync(
        string sessionId,
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken);

    Task<StreamingAsrResult> PushChunkAsync(
        string sessionId,
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken);

    Task<StreamingAsrResult> EndSessionAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

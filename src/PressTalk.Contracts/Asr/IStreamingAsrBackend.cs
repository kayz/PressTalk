namespace PressTalk.Contracts.Asr;

public interface IStreamingAsrBackend
{
    string Name { get; }

    Task StartStreamingSessionAsync(
        string sessionId,
        string languageHint,
        IReadOnlyList<string> hotwords,
        bool enableSpeakerDiarization,
        CancellationToken cancellationToken);

    Task<StreamingAsrResult> PushAudioChunkAsync(
        string sessionId,
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken);

    Task<StreamingAsrResult> EndStreamingSessionAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

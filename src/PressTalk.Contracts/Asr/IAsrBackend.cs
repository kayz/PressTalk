namespace PressTalk.Contracts.Asr;

public interface IAsrBackend
{
    string Name { get; }

    Task<AsrResult> TranscribeAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken);
}


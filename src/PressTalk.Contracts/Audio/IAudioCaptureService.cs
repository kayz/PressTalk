namespace PressTalk.Contracts.Audio;

public interface IAudioCaptureService
{
    Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken);

    Task<AudioCaptureResult> StopCaptureAsync(string sessionId, CancellationToken cancellationToken);

    bool TryGetIncrementalChunk(
        int fromSampleIndex,
        int minimumSamples,
        out AudioCaptureResult chunk,
        out int totalSamples)
    {
        chunk = new AudioCaptureResult(ReadOnlyMemory<float>.Empty, 0, TimeSpan.Zero);
        totalSamples = 0;
        return false;
    }
}

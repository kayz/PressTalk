namespace PressTalk.Contracts.Audio;

public interface IAudioCaptureService
{
    Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken);

    Task<AudioCaptureResult> StopCaptureAsync(string sessionId, CancellationToken cancellationToken);
}


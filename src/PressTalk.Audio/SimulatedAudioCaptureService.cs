using PressTalk.Contracts.Audio;

namespace PressTalk.Audio;

public sealed class SimulatedAudioCaptureService : IAudioCaptureService
{
    private readonly object _sync = new();
    private DateTimeOffset? _startedAt;
    private string? _activeSessionId;
    private readonly Action<string>? _log;

    public SimulatedAudioCaptureService(Action<string>? log = null)
    {
        _log = log;
    }

    public Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_activeSessionId is not null)
            {
                throw new InvalidOperationException("Capture already started.");
            }

            _activeSessionId = sessionId;
            _startedAt = DateTimeOffset.UtcNow;
        }

        _log?.Invoke($"[Audio.Simulated] start capture, session={sessionId}");

        return Task.CompletedTask;
    }

    public Task<AudioCaptureResult> StopCaptureAsync(string sessionId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_activeSessionId is null || _activeSessionId != sessionId || _startedAt is null)
            {
                throw new InvalidOperationException("Capture session mismatch.");
            }

            var duration = DateTimeOffset.UtcNow - _startedAt.Value;

            _activeSessionId = null;
            _startedAt = null;

            _log?.Invoke($"[Audio.Simulated] stop capture, session={sessionId}, durationMs={duration.TotalMilliseconds:F1}, samples=0");

            return Task.FromResult(new AudioCaptureResult(
                AudioSamples: ReadOnlyMemory<float>.Empty,
                SampleRate: 16000,
                Duration: duration));
        }
    }
}

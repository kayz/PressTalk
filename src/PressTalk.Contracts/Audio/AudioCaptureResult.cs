namespace PressTalk.Contracts.Audio;

public sealed record AudioCaptureResult(
    ReadOnlyMemory<float> AudioSamples,
    int SampleRate,
    TimeSpan Duration);


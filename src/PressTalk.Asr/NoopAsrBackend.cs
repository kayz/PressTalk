using PressTalk.Contracts.Asr;

namespace PressTalk.Asr;

public sealed class NoopAsrBackend : IAsrBackend
{
    private readonly Action<string>? _log;

    public NoopAsrBackend(Action<string>? log = null)
    {
        _log = log;
    }

    public string Name => "noop";

    public Task<AsrResult> TranscribeAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        _log?.Invoke($"[ASR.Noop] transcribe start, samples={audioSamples.Length}, sampleRate={sampleRate}");

        var result = new AsrResult(
            Text: "[M0] ASR backend placeholder output.",
            IsFinal: true,
            Duration: TimeSpan.Zero);

        _log?.Invoke($"[ASR.Noop] transcribe end, textLen={result.Text.Length}");

        return Task.FromResult(result);
    }
}

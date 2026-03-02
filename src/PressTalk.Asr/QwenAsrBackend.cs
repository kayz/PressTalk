using PressTalk.Contracts.Asr;

namespace PressTalk.Asr;

public sealed class QwenAsrBackend : IAsrBackend
{
    private readonly QwenRuntimeClient _runtime;
    private readonly Action<string>? _log;

    public QwenAsrBackend(
        QwenRuntimeClient runtime,
        Action<string>? log = null)
    {
        _runtime = runtime;
        _log = log;
    }

    public string Name => "qwen3-asr";

    public async Task<AsrResult> TranscribeAsync(
        ReadOnlyMemory<float> audioSamples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        _log?.Invoke($"[ASR.Qwen] transcribe start, samples={audioSamples.Length}, sampleRate={sampleRate}");
        var result = await _runtime.TranscribeFinalAsync(audioSamples, sampleRate, "auto", cancellationToken);
        _log?.Invoke($"[ASR.Qwen] transcribe end, textLen={result.Text.Length}, durationMs={result.Duration.TotalMilliseconds:F1}");
        return result;
    }
}

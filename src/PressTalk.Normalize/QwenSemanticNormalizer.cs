using PressTalk.Asr;
using PressTalk.Contracts.Normalize;

namespace PressTalk.Normalize;

public sealed class QwenSemanticNormalizer : ITextNormalizer
{
    private readonly QwenRuntimeClient _runtime;
    private readonly Action<string>? _log;

    public QwenSemanticNormalizer(QwenRuntimeClient runtime, Action<string>? log = null)
    {
        _runtime = runtime;
        _log = log;
    }

    public async Task<string> NormalizeAsync(
        string rawText,
        string languageHint,
        TextNormalizationOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        _log?.Invoke(
            $"[Normalize.Qwen] input, lang={languageHint}, textLen={rawText.Length}, semantic={options.EnableSemantic}, scenario={options.Scenario}, preserveStructured={options.PreserveStructuredItems}");
        var output = await _runtime.NormalizeAsync(rawText, languageHint, options, cancellationToken);
        _log?.Invoke($"[Normalize.Qwen] output, textLen={output.Length}");
        return output;
    }
}

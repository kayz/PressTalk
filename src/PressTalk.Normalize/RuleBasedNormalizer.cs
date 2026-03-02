using PressTalk.Contracts.Normalize;

namespace PressTalk.Normalize;

public sealed class RuleBasedNormalizer : ITextNormalizer
{
    private static readonly string[] FillerTokens = ["um", "uh", "emm", "嗯", "呃", "那个"];
    private readonly Action<string>? _log;

    public RuleBasedNormalizer(Action<string>? log = null)
    {
        _log = log;
    }

    public Task<string> NormalizeAsync(
        string rawText,
        string languageHint,
        TextNormalizationOptions options,
        CancellationToken cancellationToken)
    {
        _log?.Invoke($"[Normalize.Rule] input, lang={languageHint}, text='{rawText}'");

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return Task.FromResult(string.Empty);
        }

        var output = rawText.Trim();

        foreach (var token in FillerTokens)
        {
            output = output.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        output = output.Replace("  ", " ", StringComparison.Ordinal);

        var normalized = output.Trim();
        _log?.Invoke($"[Normalize.Rule] output, text='{normalized}'");

        return Task.FromResult(normalized);
    }
}

namespace PressTalk.App.Configuration;

public sealed class HotwordConfig
{
    public List<string> Terms { get; set; } = [];

    public IReadOnlyList<string> GetNormalizedTerms()
    {
        return Terms
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string ToMultiline()
    {
        return string.Join(Environment.NewLine, GetNormalizedTerms());
    }

    public static HotwordConfig FromMultiline(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HotwordConfig();
        }

        var terms = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HotwordConfig { Terms = terms };
    }
}

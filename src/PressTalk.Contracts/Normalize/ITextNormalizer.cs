namespace PressTalk.Contracts.Normalize;

public interface ITextNormalizer
{
    Task<string> NormalizeAsync(
        string rawText,
        string languageHint,
        TextNormalizationOptions options,
        CancellationToken cancellationToken);
}

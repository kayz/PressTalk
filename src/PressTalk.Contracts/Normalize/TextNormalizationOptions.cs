namespace PressTalk.Contracts.Normalize;

public sealed record TextNormalizationOptions(
    bool EnableSemantic,
    string Scenario,
    bool PreserveStructuredItems)
{
    public static readonly TextNormalizationOptions Default = new(
        EnableSemantic: false,
        Scenario: "default",
        PreserveStructuredItems: false);
}

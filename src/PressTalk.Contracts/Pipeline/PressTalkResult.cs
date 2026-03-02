namespace PressTalk.Contracts.Pipeline;

public sealed record PressTalkResult(
    string SessionId,
    string RawText,
    string NormalizedText,
    TimeSpan Duration);


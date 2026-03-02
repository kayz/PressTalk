namespace PressTalk.Contracts.Asr;

public sealed record AsrResult(
    string Text,
    bool IsFinal,
    TimeSpan Duration);


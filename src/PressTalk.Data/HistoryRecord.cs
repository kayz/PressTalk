namespace PressTalk.Data;

public sealed record HistoryRecord(
    string SessionId,
    DateTimeOffset Timestamp,
    string RawText,
    string NormalizedText,
    string AppName);


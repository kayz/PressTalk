namespace PressTalk.Contracts.Asr;

public sealed record StreamingAsrResult(
    string SessionId,
    string PreviewText,
    string ConfirmedText,
    string DeltaText,
    bool IsFinal,
    TimeSpan Duration,
    IReadOnlyList<SpeakerSegment> SpeakerSegments);

namespace PressTalk.Contracts.Asr;

public sealed record SpeakerSegment(
    string SpeakerId,
    string Text,
    int StartMs,
    int EndMs);

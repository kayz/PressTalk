namespace PressTalk.Contracts.Pipeline;

public sealed record PressTalkRequest(
    string SessionId,
    ReadOnlyMemory<float> AudioSamples,
    int SampleRate,
    string LanguageHint,
    bool IsStickyDictationMode,
    bool EnableSemanticEnhancement);

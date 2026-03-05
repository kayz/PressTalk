namespace PressTalk.App.Configuration;

public sealed class AppUserConfig
{
    public int SchemaVersion { get; set; } = 1;

    public string HoldKeyName { get; set; } = "F8";

    public uint HoldKeyVirtualKey { get; set; } = 0x77;

    public bool EnableLiveCaption { get; set; }

    public bool EnableManualSemanticLlm { get; set; }

    public bool EnableStickyDictationSemantic { get; set; } = true;

    public bool EnableSpeakerDiarization { get; set; }

    public HotwordConfig HotwordConfig { get; set; } = new();

    // fast: prioritize latency; formatted: add punctuation and final formatting.
    public string TranscriptionMode { get; set; } = "fast";

    public bool AlwaysOnTop { get; set; } = true;

    public int? FloatingWindowX { get; set; }

    public int? FloatingWindowY { get; set; }
}

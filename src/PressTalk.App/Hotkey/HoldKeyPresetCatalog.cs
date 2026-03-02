namespace PressTalk.App.Hotkey;

public static class HoldKeyPresetCatalog
{
    private static readonly IReadOnlyList<HoldKeyPreset> Presets =
    [
        new("F8", 0x77),
        new("F9", 0x78),
        new("F10", 0x79),
        new("F11", 0x7A),
        new("F12", 0x7B)
    ];

    public static IReadOnlyList<HoldKeyPreset> All => Presets;

    public static HoldKeyPreset Default => Presets[0];

    public static bool IsSupported(uint virtualKey)
    {
        return Presets.Any(p => p.VirtualKey == virtualKey);
    }

    public static HoldKeyPreset Resolve(uint virtualKey)
    {
        return Presets.FirstOrDefault(p => p.VirtualKey == virtualKey) ?? Default;
    }
}


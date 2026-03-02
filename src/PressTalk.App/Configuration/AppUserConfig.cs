namespace PressTalk.App.Configuration;

public sealed class AppUserConfig
{
    public int SchemaVersion { get; set; } = 1;

    public string HoldKeyName { get; set; } = "F8";

    public uint HoldKeyVirtualKey { get; set; } = 0x77;
}


namespace PressTalk.Contracts.Configuration;

public sealed class RuntimeOptions
{
    public string LanguageHint { get; set; } = "auto";

    public int SampleRate { get; set; } = 16000;

    public string ModelProfile { get; set; } = "sensevoice-small";
}


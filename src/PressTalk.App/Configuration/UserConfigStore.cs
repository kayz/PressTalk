using System.Text.Json;

namespace PressTalk.App.Configuration;

public sealed class UserConfigStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PressTalk");

    public string ConfigPath => Path.Combine(RootDirectory, "config.json");

    public async Task<AppUserConfig?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigPath, cancellationToken);
            return JsonSerializer.Deserialize<AppUserConfig>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(AppUserConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootDirectory);

        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await File.WriteAllTextAsync(ConfigPath, json, cancellationToken);
    }

    public void Delete()
    {
        if (File.Exists(ConfigPath))
        {
            File.Delete(ConfigPath);
        }
    }
}


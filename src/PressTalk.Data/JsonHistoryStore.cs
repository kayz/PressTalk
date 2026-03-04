using System.Text;
using System.Text.Json;

namespace PressTalk.Data;

public sealed class JsonHistoryStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    public JsonHistoryStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PressTalk",
                "history");
    }

    public string RootDirectory { get; }

    public string HistoryPath => Path.Combine(RootDirectory, "history.jsonl");

    public async Task AppendAsync(HistoryRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootDirectory);
        var line = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(HistoryPath, line, Encoding.UTF8, cancellationToken);
    }
}

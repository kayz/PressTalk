namespace PressTalk.Data;

public sealed class InMemoryHistoryStore
{
    private readonly List<HistoryRecord> _records = [];

    public void Add(HistoryRecord record)
    {
        _records.Add(record);
    }

    public IReadOnlyList<HistoryRecord> All()
    {
        return _records;
    }
}


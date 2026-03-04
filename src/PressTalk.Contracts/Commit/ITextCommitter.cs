namespace PressTalk.Contracts.Commit;

public interface ITextCommitter
{
    Task CommitAsync(string text, CancellationToken cancellationToken);

    Task CommitIncrementalAsync(
        string confirmedText,
        bool isFinal,
        CancellationToken cancellationToken);

    void ResetIncrementalState();
}

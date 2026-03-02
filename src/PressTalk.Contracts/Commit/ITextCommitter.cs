namespace PressTalk.Contracts.Commit;

public interface ITextCommitter
{
    Task CommitAsync(string text, CancellationToken cancellationToken);
}


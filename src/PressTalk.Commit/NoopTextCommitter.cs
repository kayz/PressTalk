using PressTalk.Contracts.Commit;

namespace PressTalk.Commit;

public sealed class NoopTextCommitter : ITextCommitter
{
    public Task CommitAsync(string text, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}


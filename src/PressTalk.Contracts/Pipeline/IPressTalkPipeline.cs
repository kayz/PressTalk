namespace PressTalk.Contracts.Pipeline;

public interface IPressTalkPipeline
{
    Task<PressTalkResult> ProcessAsync(PressTalkRequest request, CancellationToken cancellationToken);
}


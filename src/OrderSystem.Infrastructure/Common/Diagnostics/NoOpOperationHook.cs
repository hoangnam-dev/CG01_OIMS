using OrderSystem.Application.Common.Diagnostics;

namespace OrderSystem.Infrastructure.Common.Diagnostics;

internal sealed class NoOpOperationHook : IOperationHook
{
    public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

using OrderSystem.Application.Common.Diagnostics;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class ControllableOperationHook : IOperationHook
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    public async Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
        _reached.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
    }

    public void Release() => _release.TrySetResult();
}

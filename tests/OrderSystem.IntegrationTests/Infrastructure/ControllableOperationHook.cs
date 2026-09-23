using OrderSystem.Application.Common.Diagnostics;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class ControllableOperationHook : IOperationHook
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string targetCheckpoint;
    private readonly int expectedParticipants;
    private int reachedCount;

    public Task Reached => _reached.Task;
    public int ReachedCount => Volatile.Read(ref reachedCount);

    public ControllableOperationHook(string targetCheckpoint, int expectedParticipants = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCheckpoint);
        if (expectedParticipants <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedParticipants), "Expected participant count must be greater than zero");
        }
        this.targetCheckpoint = targetCheckpoint;
        this.expectedParticipants = expectedParticipants;
    }

    public async Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);

        if (!string.Equals(checkpoint, targetCheckpoint, StringComparison.Ordinal))
        {
            return;
        }

        var currentCount = Interlocked.Increment(ref reachedCount);

        if (currentCount > expectedParticipants)
        {
            throw new InvalidOperationException($"Checkpoint '{targetCheckpoint}' was reached more than {expectedParticipants} time(s)");
        }
        if (currentCount == expectedParticipants)
        {
            _reached.TrySetResult();
        }

        await _release.Task.WaitAsync(cancellationToken);
    }

    public void Release() => _release.TrySetResult();
}

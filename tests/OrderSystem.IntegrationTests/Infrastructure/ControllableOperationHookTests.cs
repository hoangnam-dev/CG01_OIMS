using OrderSystem.Application.Orders;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class ControllableOperationHookTests
{
    [Fact]
    public void Constructor_WithNonPositiveExpectedParticipants_Throws()
    {
        // Arrange | Act
        var action = () => new ControllableOperationHook(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          expectedParticipants: 0
        );

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(action);
    }

    [Fact]
    public async Task ReachAsync_WithDifferentCheckpoint_CompletesImmediately()
    {
        // Arrange
        var hook = new ControllableOperationHook(
          OrderOperationCheckpoints.BeforeInventoryReservation
        );

        // Act
        var task = hook.ReachAsync(
          OrderOperationCheckpoints.AfterInventoryReservation,
          CancellationToken.None
        );
        await task.WaitAsync(TimeSpan.FromSeconds(1));

        // Assert
        Assert.False(hook.Reached.IsCompleted);
        Assert.Equal(0, hook.ReachedCount);
    }

    [Fact]
    public async Task ReachAsync_WithMatchingCheckpoint_WaitsUntilReleased()
    {
        // Arrange
        var hook = new ControllableOperationHook(
          OrderOperationCheckpoints.BeforeInventoryReservation
        );

        // Act
        var waitingActor = hook.ReachAsync(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          CancellationToken.None
        );

        // Assert before release
        await hook.Reached.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, hook.ReachedCount);
        Assert.False(waitingActor.IsCompleted);

        // Act: allow the actor to continue
        hook.Release();

        // Assert after release
        await waitingActor.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(waitingActor.IsCompleted);
    }

    [Fact]
    public async Task ReachAsync_WithTwoExpectedParticipants_SignalsReachedOnlyAfterBothArrive()
    {
        // Arrange
        var hook = new ControllableOperationHook(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          expectedParticipants: 2
        );

        // Act: actor A reaches the checkpoint
        var firstActor = hook.ReachAsync(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          CancellationToken.None
        );
        // Assert: one actor is not enough
        Assert.Equal(1, hook.ReachedCount);
        Assert.False(hook.Reached.IsCompleted);
        Assert.False(firstActor.IsCompleted);

        // Act: actor B reaches the same checkpoint
        var secondActor = hook.ReachAsync(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          CancellationToken.None
        );
        // Assert: both actors arrived, but are still paused
        await hook.Reached.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, hook.ReachedCount);
        Assert.False(firstActor.IsCompleted);
        Assert.False(secondActor.IsCompleted);

        // Act: release both actors
        hook.Release();

        // Assert
        await Task.WhenAll(firstActor, secondActor).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReachAsync_WhenMoreActorsArriveThanExpected_Throws()
    {
        // Arrange
        var hook = new ControllableOperationHook(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          expectedParticipants: 1
        );

        var firstActor = hook.ReachAsync(
          OrderOperationCheckpoints.BeforeInventoryReservation,
          CancellationToken.None
        );

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                hook.ReachAsync(
                    OrderOperationCheckpoints.BeforeInventoryReservation,
                    CancellationToken.None)
              );

        // Cleanup: do not leave the first actor waiting.
        hook.Release();
        await firstActor.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReachAsync_WhenCancellationIsRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var hook = new ControllableOperationHook(
            OrderOperationCheckpoints.BeforeInventoryReservation);
        using var cancellation = new CancellationTokenSource();

        var waitingActor = hook.ReachAsync(
            OrderOperationCheckpoints.BeforeInventoryReservation,
            cancellation.Token);

        await hook.Reached.WaitAsync(TimeSpan.FromSeconds(1));

        // Act
        cancellation.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waitingActor);
    }
}
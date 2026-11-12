extern alias worker;

using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Outbox;

namespace OrderSystem.IntegrationTests.Worker;

public sealed class OutboxPublisherWorkerTests
{
    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task ExecuteAsync_TransientPersistenceFailure_RetriesBatchWithoutStopping()
    {
        var batch = new TransientFailureBatch();
        await using var provider = new ServiceCollection()
            .AddScoped<IOutboxPublishBatch>(_ => batch)
            .BuildServiceProvider();
        var options = Options.Create(new OutboxPublisherOptions
        {
            MaxConcurrentPublishes = 2,
            IdlePollInterval = TimeSpan.FromMilliseconds(10)
        });
        using var workerInstance = new worker::OrderSystem.Worker.OutboxPublisherWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), options,
            NullLogger<worker::OrderSystem.Worker.OutboxPublisherWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            var completed = await Task.WhenAny(batch.SecondCall, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(batch.SecondCall, completed);
            Assert.Equal(2, batch.LastCapacity);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T05")]
    public async Task ExecuteAsync_AfterBatchWithWork_RunsNextBatchWithoutIdleDelay()
    {
        var batch = new RecordingBatch();
        await using var provider = new ServiceCollection()
            .AddScoped<IOutboxPublishBatch>(_ => batch)
            .BuildServiceProvider();
        var options = Options.Create(new OutboxPublisherOptions
        {
            MaxConcurrentPublishes = 2,
            IdlePollInterval = TimeSpan.FromHours(1)
        });
        using var workerInstance = new worker::OrderSystem.Worker.OutboxPublisherWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), options,
            NullLogger<worker::OrderSystem.Worker.OutboxPublisherWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);

        try
        {
            var completed = await Task.WhenAny(batch.SecondCall, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(batch.SecondCall, completed);
            Assert.Equal(2, batch.LastCapacity);
        }
        finally
        {
            await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T05")]
    public async Task StopAsync_CancelsActiveBatchAndCompletes()
    {
        var batch = new WaitingBatch();
        await using var provider = new ServiceCollection()
            .AddScoped<IOutboxPublishBatch>(_ => batch)
            .BuildServiceProvider();
        var options = Options.Create(new OutboxPublisherOptions
        {
            MaxConcurrentPublishes = 2,
            IdlePollInterval = TimeSpan.FromHours(1)
        });
        using var workerInstance = new worker::OrderSystem.Worker.OutboxPublisherWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), options,
            NullLogger<worker::OrderSystem.Worker.OutboxPublisherWorker>.Instance);

        await workerInstance.StartAsync(CancellationToken.None);
        await batch.Started.WaitAsync(TimeSpan.FromSeconds(2));

        await workerInstance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(batch.CancellationObserved.IsCompletedSuccessfully);
        Assert.Equal(2, batch.LastCapacity);
    }

    private sealed class TransientFailureBatch : IOutboxPublishBatch
    {
        private readonly TaskCompletionSource secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public Task SecondCall => secondCall.Task;

        public int LastCapacity { get; private set; }

        public async Task<int> RunOnceAsync(int capacity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCapacity = capacity;
            if (Interlocked.Increment(ref callCount) == 1)
            {
                await Task.Yield();
                throw new TransientDatabaseException();
            }

            secondCall.TrySetResult();
            return 0;
        }
    }

    private sealed class TransientDatabaseException : DbException
    {
        public override bool IsTransient => true;
    }

    private sealed class RecordingBatch : IOutboxPublishBatch
    {
        private readonly TaskCompletionSource secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public Task SecondCall => secondCall.Task;

        public int LastCapacity { get; private set; }

        public Task<int> RunOnceAsync(int capacity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCapacity = capacity;
            var call = Interlocked.Increment(ref callCount);
            if (call == 2)
            {
                secondCall.TrySetResult();
            }

            return Task.FromResult(call == 1 ? 1 : 0);
        }
    }

    private sealed class WaitingBatch : IOutboxPublishBatch
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public Task CancellationObserved => cancellationObserved.Task;

        public int LastCapacity { get; private set; }

        public async Task<int> RunOnceAsync(int capacity, CancellationToken cancellationToken)
        {
            LastCapacity = capacity;
            using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OutboxCleanupStoreTests(PostgreSqlFixture postgres) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    [Trait("Requirement", "S12-T07")]
    public void OutboxHealthOptions_BindsConfiguredPendingAgeThreshold()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=oims_options_test",
                ["OutboxHealth:PendingAgeThreshold"] = "00:20:00"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrderSystemPersistence(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var options = provider.GetRequiredService<IOptions<OutboxHealthOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(20), options.PendingAgeThreshold);
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task OutboxHealthCheck_ReportsNeedsAttentionWithoutBlockingReadiness()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = postgres.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrderSystemPersistence(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using (var migrationScope = provider.CreateAsyncScope())
        {
            await migrationScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
        }

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var registration = Assert.Single(registrations, candidate => candidate.Name == "outbox");
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.DoesNotContain("ready", registration.Tags);
        var checks = provider.GetRequiredService<HealthCheckService>();
        var healthy = await checks.CheckHealthAsync(candidate => candidate.Name == "outbox");
        Assert.Equal(HealthStatus.Healthy, healthy.Entries["outbox"].Status);

        var needsAttention = CreatePendingIntent(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        var claimToken = Guid.NewGuid();
        Assert.True(needsAttention.TryClaim(needsAttention.OccurredAt, claimToken, TimeSpan.FromSeconds(60)));
        Assert.True(needsAttention.TryMarkNeedsAttention(claimToken, OutboxFailureCategory.Unroutable));
        try
        {
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var dbContext = seedScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
                dbContext.OutboxMessages.Add(needsAttention);
                await dbContext.SaveChangesAsync();
            }

            var degraded = await checks.CheckHealthAsync(candidate => candidate.Name == "outbox");
            Assert.Equal(HealthStatus.Degraded, degraded.Entries["outbox"].Status);
        }
        finally
        {
            await using var cleanupScope = provider.CreateAsyncScope();
            var dbContext = cleanupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.OutboxMessages.Where(message => message.MessageId == needsAttention.MessageId).ExecuteDeleteAsync();
        }

        var recovered = await checks.CheckHealthAsync(candidate => candidate.Name == "outbox");
        Assert.Equal(HealthStatus.Healthy, recovered.Entries["outbox"].Status);
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task OutboxHealthCheck_PendingAgeOverTenMinutes_DegradesAndRecovers()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = postgres.ConnectionString,
                ["OutboxHealth:PendingAgeThreshold"] = "00:10:00"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrderSystemPersistence(configuration);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var pending = CreatePendingIntent(now.AddMinutes(-10));
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            dbContext.OutboxMessages.Add(pending);
            await dbContext.SaveChangesAsync();
        }

        try
        {
            var checks = provider.GetRequiredService<HealthCheckService>();
            var atBoundary = await checks.CheckHealthAsync(candidate => candidate.Name == "outbox");
            Assert.Equal(HealthStatus.Healthy, atBoundary.Entries["outbox"].Status);

            clock.UtcNow = now.AddMilliseconds(1);
            var overdue = await checks.CheckHealthAsync(candidate => candidate.Name == "outbox");
            Assert.Equal(HealthStatus.Degraded, overdue.Entries["outbox"].Status);
        }
        finally
        {
            await using var cleanupScope = provider.CreateAsyncScope();
            var dbContext = cleanupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.OutboxMessages.Where(message => message.MessageId == pending.MessageId).ExecuteDeleteAsync();
        }

        var recovered = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == "outbox");
        Assert.Equal(HealthStatus.Healthy, recovered.Entries["outbox"].Status);
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task EnabledCleanupWorker_CleansPublishedWhileMetricsWorkerRecordsBacklog()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var published = CreatePublishedIntent(now.AddDays(-31));
        var pending = CreatePendingIntent(now.AddDays(-45));
        var needsAttention = CreatePendingIntent(now.AddDays(-40));
        var claimToken = Guid.NewGuid();
        Assert.True(needsAttention.TryClaim(needsAttention.OccurredAt, claimToken, TimeSpan.FromSeconds(60)));
        Assert.True(needsAttention.TryMarkNeedsAttention(claimToken, OutboxFailureCategory.Unroutable));
        var ids = new[] { published.MessageId, pending.MessageId, needsAttention.MessageId };
        var measurements = new ConcurrentQueue<(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)>();
        var ages = new ConcurrentQueue<(double Seconds, IReadOnlyList<KeyValuePair<string, object?>> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "OrderSystem.Infrastructure.Outbox" &&
                instrument.Name is "oims.outbox.backlog" or "oims.outbox.oldest_pending_age_seconds")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Enqueue((value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            ages.Enqueue((value, tags.ToArray())));
        listener.Start();

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.AddRange(published, pending, needsAttention);
            await seedContext.SaveChangesAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["OutboxCleanup:Enabled"] = bool.TrueString,
            ["OutboxCleanup:InitialDelay"] = "00:00:00.010",
            ["OutboxCleanup:Interval"] = "00:05:00",
            ["OutboxCleanup:BatchSize"] = "10",
            ["OutboxCleanup:PublishedRetention"] = "30.00:00:00",
            ["OutboxMetrics:Interval"] = "00:00:00.050"
        };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services
                    .AddOrderSystemPersistence(context.Configuration)
                    .AddOrderSystemCommonInfrastructure()
                    .AddOrderSystemOutboxPublisherInfrastructure(context.Configuration);
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FakeClock(now));
            })
            .Build();

        try
        {
            await host.StartAsync();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            bool oldPublishedRemains;
            do
            {
                await using var pollContext = new OrderSystemDbContext(options);
                oldPublishedRemains = await pollContext.OutboxMessages.AsNoTracking()
                    .AnyAsync(message => message.MessageId == published.MessageId);
                if (oldPublishedRemains)
                {
                    await Task.Delay(20);
                }
            } while (oldPublishedRemains && DateTimeOffset.UtcNow < deadline);

            Assert.False(oldPublishedRemains);
            await using var assertionContext = new OrderSystemDbContext(options);
            Assert.True(await assertionContext.OutboxMessages.AsNoTracking()
                .AnyAsync(message => message.MessageId == pending.MessageId && message.Status == OutboxMessageStatus.Pending));
            Assert.True(await assertionContext.OutboxMessages.AsNoTracking()
                .AnyAsync(message => message.MessageId == needsAttention.MessageId && message.Status == OutboxMessageStatus.NeedsAttention));
            var metricDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (!measurements.Any(measurement => measurement.Tags.Any(tag => tag is { Key: "status", Value: "needs_attention" })) &&
                DateTimeOffset.UtcNow < metricDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Contains(measurements, measurement =>
                measurement.Value == 1 &&
                measurement.Tags.Any(tag => tag is { Key: "status", Value: "pending" }));
            Assert.Contains(measurements, measurement =>
                measurement.Value == 1 &&
                measurement.Tags.Any(tag => tag is { Key: "status", Value: "needs_attention" }));
            var ageDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (ages.IsEmpty && DateTimeOffset.UtcNow < ageDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Contains(ages, age =>
                age.Seconds == TimeSpan.FromDays(45).TotalSeconds &&
                age.Tags.Any(tag => tag is { Key: "status", Value: "pending" }));
        }
        finally
        {
            await host.StopAsync();
            await using var cleanupContext = new OrderSystemDbContext(options);
            await cleanupContext.OutboxMessages.Where(message => ids.Contains(message.MessageId))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task BacklogMetrics_ContinueSamplingWhenPublishedCleanupIsDisabled()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var first = CreatePendingIntent(now.AddSeconds(-37));
        var second = CreatePendingIntent(now.AddSeconds(-11));
        var pendingCounts = new ConcurrentQueue<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "OrderSystem.Infrastructure.Outbox" && instrument.Name == "oims.outbox.backlog")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag is { Key: "status", Value: "pending" }))
            {
                pendingCounts.Enqueue(value);
            }
        });
        listener.Start();

        await using (var seedContext = new OrderSystemDbContext(options))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.OutboxMessages.Add(first);
            await seedContext.SaveChangesAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["OutboxCleanup:Enabled"] = bool.FalseString,
            ["OutboxMetrics:Interval"] = "00:00:00.050"
        };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services
                    .AddOrderSystemPersistence(context.Configuration)
                    .AddOrderSystemCommonInfrastructure()
                    .AddOrderSystemOutboxPublisherInfrastructure(context.Configuration);
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FakeClock(now));
            })
            .Build();

        try
        {
            await host.StartAsync();
            var firstDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (!pendingCounts.Contains(1) && DateTimeOffset.UtcNow < firstDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Contains(1, pendingCounts);

            await using (var secondContext = new OrderSystemDbContext(options))
            {
                secondContext.OutboxMessages.Add(second);
                await secondContext.SaveChangesAsync();
            }

            var secondDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (!pendingCounts.Contains(2) && DateTimeOffset.UtcNow < secondDeadline)
            {
                await Task.Delay(20);
            }
            Assert.Contains(2, pendingCounts);
        }
        finally
        {
            await host.StopAsync();
            await using var cleanupContext = new OrderSystemDbContext(options);
            await cleanupContext.OutboxMessages.Where(message => message.MessageId == first.MessageId || message.MessageId == second.MessageId)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task DeletePublishedBatchAsync_DeletesAtOrBeforeThirtyDayBoundaryOnly()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var cutoff = now.AddDays(-30);
        var before = CreatePublishedIntent(cutoff.AddMilliseconds(-1));
        var exactlyAt = CreatePublishedIntent(cutoff);
        var after = CreatePublishedIntent(cutoff.AddMilliseconds(1));
        var ids = new[] { before.MessageId, exactlyAt.MessageId, after.MessageId };

        try
        {
            await using (var seedContext = new OrderSystemDbContext(options))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.OutboxMessages.AddRange(before, exactlyAt, after);
                await seedContext.SaveChangesAsync();
            }

            await using (var cleanupContext = new OrderSystemDbContext(options))
            {
                await new EfOutboxCleanupStore(cleanupContext)
                    .DeletePublishedBatchAsync(cutoff, 10, CancellationToken.None);
            }

            await using var assertionContext = new OrderSystemDbContext(options);
            var remaining = await assertionContext.OutboxMessages.AsNoTracking()
                .Where(message => ids.Contains(message.MessageId))
                .Select(message => message.MessageId)
                .ToListAsync();
            Assert.Equal([after.MessageId], remaining);
        }
        finally
        {
            await using var cleanupContext = new OrderSystemDbContext(options);
            await cleanupContext.OutboxMessages.Where(message => ids.Contains(message.MessageId))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task DeletePublishedBatchAsync_LimitsBatchAndPreservesAllActiveStatuses()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var cutoff = now.AddDays(-30);
        var published = Enumerable.Range(0, 3)
            .Select(index => CreatePublishedIntent(cutoff.AddDays(-1).AddMinutes(index)))
            .ToArray();
        var pending = CreatePendingIntent(cutoff.AddDays(-2));
        var publishing = CreatePendingIntent(cutoff.AddDays(-2));
        Assert.True(publishing.TryClaim(publishing.OccurredAt, Guid.NewGuid(), TimeSpan.FromSeconds(60)));
        var needsAttention = CreatePendingIntent(cutoff.AddDays(-2));
        var token = Guid.NewGuid();
        Assert.True(needsAttention.TryClaim(needsAttention.OccurredAt, token, TimeSpan.FromSeconds(60)));
        Assert.True(needsAttention.TryMarkNeedsAttention(token, OutboxFailureCategory.Unroutable));
        var ids = published.Select(message => message.MessageId)
            .Concat([pending.MessageId, publishing.MessageId, needsAttention.MessageId])
            .ToArray();

        try
        {
            await using (var seedContext = new OrderSystemDbContext(options))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.OutboxMessages.AddRange(published);
                seedContext.OutboxMessages.AddRange(pending, publishing, needsAttention);
                await seedContext.SaveChangesAsync();
            }

            await using (var cleanupContext = new OrderSystemDbContext(options))
            {
                var deleted = await new EfOutboxCleanupStore(cleanupContext)
                    .DeletePublishedBatchAsync(cutoff, 2, CancellationToken.None);
                Assert.Equal(2, deleted);
            }

            await using var assertionContext = new OrderSystemDbContext(options);
            var remaining = await assertionContext.OutboxMessages.AsNoTracking()
                .Where(message => ids.Contains(message.MessageId))
                .Select(message => new { message.MessageId, message.Status })
                .ToListAsync();
            Assert.Contains(remaining, message => message.MessageId == published[2].MessageId);
            Assert.DoesNotContain(remaining, message => message.MessageId == published[0].MessageId);
            Assert.DoesNotContain(remaining, message => message.MessageId == published[1].MessageId);
            Assert.Contains(remaining, message => message.MessageId == pending.MessageId && message.Status == OutboxMessageStatus.Pending);
            Assert.Contains(remaining, message => message.MessageId == publishing.MessageId && message.Status == OutboxMessageStatus.Publishing);
            Assert.Contains(remaining, message => message.MessageId == needsAttention.MessageId && message.Status == OutboxMessageStatus.NeedsAttention);
        }
        finally
        {
            await using var cleanupContext = new OrderSystemDbContext(options);
            await cleanupContext.OutboxMessages.Where(message => ids.Contains(message.MessageId))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S12-T07")]
    public async Task DeletePublishedBatchAsync_SkipsLockedRowSoAnotherWorkerCanContinue()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        var cutoff = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var locked = CreatePublishedIntent(cutoff.AddDays(-2));
        var available = CreatePublishedIntent(cutoff.AddDays(-1));
        var ids = new[] { locked.MessageId, available.MessageId };

        try
        {
            await using (var seedContext = new OrderSystemDbContext(options))
            {
                await seedContext.Database.MigrateAsync();
                seedContext.OutboxMessages.AddRange(locked, available);
                await seedContext.SaveChangesAsync();
            }

            await using (var lockingContext = new OrderSystemDbContext(options))
            {
                await using var transaction = await lockingContext.Database.BeginTransactionAsync();
                var selected = await lockingContext.OutboxMessages
                    .FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE message_id = {locked.MessageId} FOR UPDATE")
                    .SingleAsync();
                Assert.Equal(locked.MessageId, selected.MessageId);

                await using var otherContext = new OrderSystemDbContext(options);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var deleted = await new EfOutboxCleanupStore(otherContext)
                    .DeletePublishedBatchAsync(cutoff, 2, timeout.Token);
                Assert.Equal(1, deleted);
                await transaction.RollbackAsync();
            }

            await using var assertionContext = new OrderSystemDbContext(options);
            Assert.True(await assertionContext.OutboxMessages.AsNoTracking()
                .AnyAsync(message => message.MessageId == locked.MessageId));
            Assert.False(await assertionContext.OutboxMessages.AsNoTracking()
                .AnyAsync(message => message.MessageId == available.MessageId));
        }
        finally
        {
            await using var cleanupContext = new OrderSystemDbContext(options);
            await cleanupContext.OutboxMessages.Where(message => ids.Contains(message.MessageId))
                .ExecuteDeleteAsync();
        }
    }

    private static OutboxMessage CreatePublishedIntent(DateTimeOffset publishedAt)
    {
        var intent = CreatePendingIntent(publishedAt.AddMinutes(-1));
        var token = Guid.NewGuid();
        Assert.True(intent.TryClaim(intent.OccurredAt, token, TimeSpan.FromSeconds(60)));
        Assert.True(intent.TryMarkPublished(publishedAt, token));
        return intent;
    }

    private static OutboxMessage CreatePendingIntent(DateTimeOffset occurredAt)
    {
        var eventMessage = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: occurredAt);
        var intent = new OutboxMessage(
            eventMessage.MessageId, eventMessage.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(eventMessage)),
            eventMessage.CorrelationId, eventMessage.OccurredAt, "oims.events", "order.created.v1");
        return intent;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Notifications;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class MessagingCleanupWorkerTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Requirement", "S10-T09")]
    [Trait("TestId", "MSG-CLEANUP-001")]
    public async Task Worker_ProcessedMessageRetention_DeletesAtFourteenDayCutoffAndKeepsYoungerEvidence()
    {
        var expiredId = Guid.NewGuid();
        var boundaryId = Guid.NewGuid();
        var retainedId = Guid.NewGuid();
        var trackedIds = new[] { expiredId, boundaryId, retainedId };
        var oldTerminalFailureId = Guid.NewGuid();
        var boundaryTerminalFailureId = Guid.NewGuid();
        var retainedTerminalFailureId = Guid.NewGuid();
        var pendingFailureId = Guid.NewGuid();
        var redriveRequestedFailureId = Guid.NewGuid();
        var failureIds = new[]
        {
            oldTerminalFailureId,
            boundaryTerminalFailureId,
            retainedTerminalFailureId,
            pendingFailureId,
            redriveRequestedFailureId
        };
        var adminId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var options = CreateDbContextOptions();

        await using (var arrangeContext = new OrderSystemDbContext(options))
        {
            await arrangeContext.Database.MigrateAsync();
            var admin = CreateUser(adminId, UserRole.Admin);
            var customer = CreateUser(customerId, UserRole.Customer);
            var oldTerminalFailure = CreateFailure(oldTerminalFailureId, FailedMessageFailureKind.Permanent);
            var boundaryTerminalFailure = CreateFailure(boundaryTerminalFailureId, FailedMessageFailureKind.Permanent);
            var retainedTerminalFailure = CreateFailure(retainedTerminalFailureId, FailedMessageFailureKind.Permanent);
            var pendingFailure = CreateFailure(pendingFailureId, FailedMessageFailureKind.Permanent);
            var redriveRequestedFailure = CreateFailure(redriveRequestedFailureId, FailedMessageFailureKind.RetryExhausted);

            Assert.True(oldTerminalFailure.TryClose(adminId, "Investigation complete", FixedNow.AddDays(-90).AddMilliseconds(-1)));
            Assert.True(boundaryTerminalFailure.TryClose(adminId, "Investigation complete", FixedNow.AddDays(-90)));
            Assert.True(retainedTerminalFailure.TryClose(adminId, "Investigation complete", FixedNow.AddDays(-90).AddMilliseconds(1)));
            Assert.True(redriveRequestedFailure.TryRequestRedrive(adminId, "Dependency recovery verified", FixedNow.AddDays(-100)));

            arrangeContext.ProcessedMessages.AddRange(
                new ProcessedMessage(expiredId, "cleanup-test", FixedNow.AddDays(-14).AddMilliseconds(-1)),
                new ProcessedMessage(boundaryId, "cleanup-test", FixedNow.AddDays(-14)),
                new ProcessedMessage(retainedId, "cleanup-test", FixedNow.AddDays(-14).AddMilliseconds(1)));
            arrangeContext.AddRange(
                admin,
                customer,
                new Order(orderId, customerId, 0m, FixedNow.AddDays(-179), FixedNow.AddDays(-180)),
                new Notification(notificationId, customerId, orderId, Guid.NewGuid(), FixedNow.AddDays(-180)),
                oldTerminalFailure,
                boundaryTerminalFailure,
                retainedTerminalFailure,
                pendingFailure,
                redriveRequestedFailure);
            await arrangeContext.SaveChangesAsync();
        }

        using var host = CreateHost(cleanupBatchSize: 2);

        try
        {
            await host.StartAsync();
            var remainingIds = await WaitForProcessedMessageCleanupAsync(options, trackedIds, TimeSpan.FromSeconds(2));
            var remainingFailureIds = await WaitForFailedMessageCleanupAsync(options, failureIds, expectedCount: 3, TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(expiredId, remainingIds);
            Assert.DoesNotContain(boundaryId, remainingIds);
            Assert.Contains(retainedId, remainingIds);
            Assert.DoesNotContain(oldTerminalFailureId, remainingFailureIds);
            Assert.DoesNotContain(boundaryTerminalFailureId, remainingFailureIds);
            Assert.Contains(retainedTerminalFailureId, remainingFailureIds);
            Assert.Contains(pendingFailureId, remainingFailureIds);
            Assert.Contains(redriveRequestedFailureId, remainingFailureIds);

            await using var assertionContext = new OrderSystemDbContext(options);
            Assert.True(await assertionContext.Notifications.AnyAsync(notification => notification.Id == notificationId));
        }
        finally
        {
            await host.StopAsync();
            await CleanupAsync(options, trackedIds, failureIds, notificationId, orderId, [adminId, customerId]);
        }
    }

    [Fact]
    [Trait("Requirement", "S10-T09")]
    [Trait("TestId", "MSG-CLEANUP-002")]
    public async Task TwoWorkers_TerminalFailureCleanup_ClaimsDisjointBatchesAndRerunIsSafe()
    {
        var adminId = Guid.NewGuid();
        var failureIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var options = CreateDbContextOptions();

        await using (var arrangeContext = new OrderSystemDbContext(options))
        {
            await arrangeContext.Database.MigrateAsync();
            arrangeContext.Users.Add(CreateUser(adminId, UserRole.Admin));
            foreach (var failureId in failureIds)
            {
                var failure = CreateFailure(failureId, FailedMessageFailureKind.Permanent);
                Assert.True(failure.TryClose(adminId, "Investigation complete", FixedNow.AddDays(-91)));
                arrangeContext.FailedMessages.Add(failure);
            }

            await arrangeContext.SaveChangesAsync();
        }

        using var firstHost = CreateHost(cleanupBatchSize: 2);
        using var secondHost = CreateHost(cleanupBatchSize: 2);

        try
        {
            await Task.WhenAll(firstHost.StartAsync(), secondHost.StartAsync());
            var remainingFailureIds = await WaitForFailedMessageCleanupAsync(options, failureIds, expectedCount: 0, TimeSpan.FromSeconds(2));

            Assert.Empty(remainingFailureIds);

            using var rerunHost = CreateHost(cleanupBatchSize: 2);
            await rerunHost.StartAsync();
            await rerunHost.StopAsync();
        }
        finally
        {
            await Task.WhenAll(firstHost.StopAsync(), secondHost.StopAsync());
            await CleanupAsync(options, [], failureIds, null, null, [adminId]);
        }
    }

    private IHost CreateHost(int cleanupBatchSize = 10)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["MessageCleanup:Enabled"] = bool.TrueString,
            ["MessageCleanup:CleanupInitialDelay"] = "00:00:00.010",
            ["MessageCleanup:CleanupInterval"] = "00:05:00",
            ["MessageCleanup:CleanupBatchSize"] = cleanupBatchSize.ToString(CultureInfo.InvariantCulture),
            ["MessageCleanup:ProcessedMessageRetention"] = "14.00:00:00",
            ["MessageCleanup:TerminalFailureRetention"] = "90.00:00:00"
        };

        return new HostBuilder()
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
                    .AddOrderSystemMessageCleanupInfrastructure(context.Configuration);
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FakeClock(FixedNow));
            })
            .Build();
    }

    private DbContextOptions<OrderSystemDbContext> CreateDbContextOptions() =>
        new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

    private static async Task<HashSet<Guid>> WaitForProcessedMessageCleanupAsync(
        DbContextOptions<OrderSystemDbContext> options,
        Guid[] trackedIds,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        HashSet<Guid> remainingIds;

        do
        {
            await using var assertionContext = new OrderSystemDbContext(options);
            remainingIds = await assertionContext.ProcessedMessages
                .AsNoTracking()
                .Where(message => trackedIds.Contains(message.MessageId))
                .Select(message => message.MessageId)
                .ToHashSetAsync();

            if (remainingIds.Count < trackedIds.Length)
            {
                return remainingIds;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (DateTimeOffset.UtcNow < deadline);

        return remainingIds;
    }

    private static async Task<HashSet<Guid>> WaitForFailedMessageCleanupAsync(
        DbContextOptions<OrderSystemDbContext> options,
        Guid[] trackedIds,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        HashSet<Guid> remainingIds;

        do
        {
            await using var assertionContext = new OrderSystemDbContext(options);
            remainingIds = await assertionContext.FailedMessages
                .AsNoTracking()
                .Where(message => trackedIds.Contains(message.Id))
                .Select(message => message.Id)
                .ToHashSetAsync();

            if (remainingIds.Count <= expectedCount)
            {
                return remainingIds;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (DateTimeOffset.UtcNow < deadline);

        return remainingIds;
    }

    private static FailedMessage CreateFailure(Guid id, FailedMessageFailureKind failureKind)
    {
        var messageId = Guid.NewGuid();
        byte[] body = [1];
        return new FailedMessage(
            id,
            "cleanup-test",
            messageId,
            messageId.ToString("D"),
            Guid.NewGuid(),
            "OrderCreated",
            1,
            FixedNow.AddDays(-120),
            body,
            FailedMessageBodyFingerprint.Compute(body),
            "{}",
            "oims.events",
            "order.created.v1",
            failureKind,
            failureKind == FailedMessageFailureKind.RetryExhausted ? "retry_exhausted" : "permanent_failure",
            failureKind == FailedMessageFailureKind.RetryExhausted ? 3 : 1,
            FixedNow.AddDays(-120));
    }

    private static User CreateUser(Guid id, UserRole role)
    {
        var email = $"message-cleanup-{id:N}@example.com";
        return new User(id, email, email, TestCredentials.CreateHashPlaceholder(), role, FixedNow.AddDays(-200));
    }

    private static async Task CleanupAsync(
        DbContextOptions<OrderSystemDbContext> options,
        Guid[] processedMessageIds,
        Guid[] failedMessageIds,
        Guid? notificationId,
        Guid? orderId,
        Guid[] userIds)
    {
        await using var cleanupContext = new OrderSystemDbContext(options);
        await cleanupContext.ProcessedMessages
            .Where(message => processedMessageIds.Contains(message.MessageId))
            .ExecuteDeleteAsync();
        await cleanupContext.FailedMessages
            .Where(message => failedMessageIds.Contains(message.Id))
            .ExecuteDeleteAsync();

        if (notificationId is { } retainedNotificationId)
        {
            await cleanupContext.Notifications
                .Where(notification => notification.Id == retainedNotificationId)
                .ExecuteDeleteAsync();
        }

        if (orderId is { } retainedOrderId)
        {
            await cleanupContext.Orders
                .Where(order => order.Id == retainedOrderId)
                .ExecuteDeleteAsync();
        }

        await cleanupContext.Users
            .Where(user => userIds.Contains(user.Id))
            .ExecuteDeleteAsync();
    }
}

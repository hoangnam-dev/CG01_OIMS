using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure;
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
        var options = CreateDbContextOptions();

        await using (var arrangeContext = new OrderSystemDbContext(options))
        {
            await arrangeContext.Database.MigrateAsync();
            arrangeContext.ProcessedMessages.AddRange(
                new ProcessedMessage(expiredId, "cleanup-test", FixedNow.AddDays(-14).AddMilliseconds(-1)),
                new ProcessedMessage(boundaryId, "cleanup-test", FixedNow.AddDays(-14)),
                new ProcessedMessage(retainedId, "cleanup-test", FixedNow.AddDays(-14).AddMilliseconds(1)));
            await arrangeContext.SaveChangesAsync();
        }

        using var host = CreateHost();

        try
        {
            await host.StartAsync();
            var remainingIds = await WaitForProcessedMessageCleanupAsync(options, trackedIds, TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(expiredId, remainingIds);
            Assert.DoesNotContain(boundaryId, remainingIds);
            Assert.Contains(retainedId, remainingIds);
        }
        finally
        {
            await host.StopAsync();

            await using var cleanupContext = new OrderSystemDbContext(options);
            await cleanupContext.ProcessedMessages
                .Where(message => trackedIds.Contains(message.MessageId))
                .ExecuteDeleteAsync();
        }
    }

    private IHost CreateHost()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["MessageCleanup:Enabled"] = bool.TrueString,
            ["MessageCleanup:CleanupInitialDelay"] = "00:00:00.010",
            ["MessageCleanup:CleanupInterval"] = "00:05:00",
            ["MessageCleanup:CleanupBatchSize"] = 10.ToString(CultureInfo.InvariantCulture),
            ["MessageCleanup:ProcessedMessageRetention"] = "14.00:00:00"
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
}

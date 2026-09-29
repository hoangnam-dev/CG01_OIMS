using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Orders;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ReservationExpirationCandidateTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task ListCandidatesAsync_SelectsOnlyPendingPaymentAtOrBeforeNow()
    {
        var now = new DateTimeOffset(1200, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await using var factory = CreateFactory();
        var pastDueId = Guid.Parse("73000000-0000-0000-0000-000000000001");
        var dueNowId = Guid.Parse("73000000-0000-0000-0000-000000000002");
        var futureId = Guid.Parse("73000000-0000-0000-0000-000000000003");
        var confirmedId = Guid.Parse("73000000-0000-0000-0000-000000000004");
        var cancelledId = Guid.Parse("73000000-0000-0000-0000-000000000005");
        var expiredId = Guid.Parse("73000000-0000-0000-0000-000000000006");

        await SeedOrdersAsync(
            factory,
            new OrderSeed(pastDueId, now.AddSeconds(-1), OrderStatus.PendingPayment),
            new OrderSeed(dueNowId, now, OrderStatus.PendingPayment),
            new OrderSeed(futureId, now.AddSeconds(1), OrderStatus.PendingPayment),
            new OrderSeed(confirmedId, now.AddMinutes(-1), OrderStatus.Confirmed),
            new OrderSeed(cancelledId, now.AddMinutes(-1), OrderStatus.Cancelled),
            new OrderSeed(expiredId, now.AddMinutes(-1), OrderStatus.Expired));

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

        var candidates = await store.ListCandidatesAsync(
            now,
            batchSize: 10_000,
            CancellationToken.None);

        Assert.Contains(pastDueId, candidates);
        Assert.Contains(dueNowId, candidates);
        Assert.DoesNotContain(futureId, candidates);
        Assert.DoesNotContain(confirmedId, candidates);
        Assert.DoesNotContain(cancelledId, candidates);
        Assert.DoesNotContain(expiredId, candidates);
    }

    [Fact]
    public async Task ListCandidatesAsync_OrdersByExpirationThenId()
    {
        var now = new DateTimeOffset(1300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var firstId = Guid.Parse("72000000-0000-0000-0000-000000000003");
        var secondId = Guid.Parse("72000000-0000-0000-0000-000000000001");
        var thirdId = Guid.Parse("72000000-0000-0000-0000-000000000002");
        await using var factory = CreateFactory();

        await SeedOrdersAsync(
            factory,
            new OrderSeed(thirdId, now.AddMinutes(-1), OrderStatus.PendingPayment),
            new OrderSeed(firstId, now.AddMinutes(-2), OrderStatus.PendingPayment),
            new OrderSeed(secondId, now.AddMinutes(-1), OrderStatus.PendingPayment));

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

        var candidates = await store.ListCandidatesAsync(
            now,
            batchSize: 10_000,
            CancellationToken.None);

        var seededIds = new HashSet<Guid> { firstId, secondId, thirdId };
        var seededCandidates = candidates
            .Where(seededIds.Contains)
            .ToArray();

        Assert.Equal([firstId, secondId, thirdId], seededCandidates);
    }

    [Fact]
    public async Task ListCandidatesAsync_LimitsResultsToFirstDeterministicBatch()
    {
        var now = new DateTimeOffset(1000, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var firstId = Guid.Parse("71000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("71000000-0000-0000-0000-000000000002");
        var thirdId = Guid.Parse("71000000-0000-0000-0000-000000000003");
        await using var factory = CreateFactory();

        await SeedOrdersAsync(
            factory,
            new OrderSeed(thirdId, now.AddMinutes(-1), OrderStatus.PendingPayment),
            new OrderSeed(firstId, now.AddMinutes(-3), OrderStatus.PendingPayment),
            new OrderSeed(secondId, now.AddMinutes(-2), OrderStatus.PendingPayment));

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();

        var candidates = await store.ListCandidatesAsync(
            now,
            batchSize: 2,
            CancellationToken.None);

        Assert.Equal([firstId, secondId], candidates);
    }

    [Fact]
    public async Task ListCandidatesAsync_WhenCancelled_PropagatesCancellation()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReservationExpirationStore>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ListCandidatesAsync(
                DateTimeOffset.UtcNow,
                batchSize: 10,
                cancellation.Token));
    }

    private static async Task SeedOrdersAsync(
        WebApplicationFactory<Program> factory,
        params OrderSeed[] seeds)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var ownerId = Guid.NewGuid();
        var earliestCreatedAt = seeds.Min(seed => seed.ReservationExpiresAt).AddDays(-1);
        dbContext.Users.Add(new User(
            ownerId,
            $"{ownerId:N}@example.com",
            $"{ownerId:N}@example.com",
            "test-password-hash",
            UserRole.Customer,
            earliestCreatedAt));

        foreach (var seed in seeds)
        {
            var createdAt = seed.ReservationExpiresAt.AddHours(-1);
            var order = new Order(
                seed.Id,
                ownerId,
                totalAmount: 0m,
                seed.ReservationExpiresAt,
                createdAt);

            switch (seed.Status)
            {
                case OrderStatus.PendingPayment:
                    break;
                case OrderStatus.Confirmed:
                    order.Confirm(createdAt.AddMinutes(1));
                    break;
                case OrderStatus.Cancelled:
                    order.Cancel(createdAt.AddMinutes(1));
                    break;
                case OrderStatus.Expired:
                    order.Expire(seed.ReservationExpiresAt);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(seeds),
                        seed.Status,
                        "The candidate-query fixture does not support this Order status.");
            }

            dbContext.Orders.Add(order);
        }

        await dbContext.SaveChangesAsync();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));

    private sealed record OrderSeed(
        Guid Id,
        DateTimeOffset ReservationExpiresAt,
        OrderStatus Status);
}

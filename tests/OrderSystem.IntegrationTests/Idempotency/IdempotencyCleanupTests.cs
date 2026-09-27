using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Idempotency;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Idempotency;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class IdempotencyCleanupTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset Cutoff =
        new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeleteCompletedBatch_DeletesOnlyCompletedRowsWhoseDeleteAfterHasElapsed()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var eligible = CreateRequest(userId, Cutoff.AddMinutes(-1), completed: true, hashSeed: 1);
        var exactBoundary = CreateRequest(userId, Cutoff, completed: true, hashSeed: 4);
        var future = CreateRequest(userId, Cutoff.AddMinutes(1), completed: true, hashSeed: 2);
        var processing = CreateRequest(userId, Cutoff.AddDays(-7), completed: false, hashSeed: 3);
        await SeedRequestsAsync(factory, eligible, exactBoundary, future, processing);

        int deletedCount;
        using (var cleanupScope = factory.Services.CreateScope())
        {
            var cleanup = cleanupScope.ServiceProvider.GetRequiredService<IIdempotencyCleanupStore>();
            deletedCount = await cleanup.DeleteCompletedBatchAsync(Cutoff, 500, CancellationToken.None);
        }

        Assert.Equal(2, deletedCount);
        var remaining = await ReadRequestsAsync(factory, eligible.Id, exactBoundary.Id, future.Id, processing.Id);
        Assert.DoesNotContain(remaining, request => request.Id == eligible.Id);
        Assert.DoesNotContain(remaining, request => request.Id == exactBoundary.Id);
        Assert.Contains(remaining, request => request.Id == future.Id);
        Assert.Contains(remaining, request =>
            request.Id == processing.Id && request.Status == IdempotencyRequestStatus.Processing);
    }

    [Fact]
    public async Task DeleteCompletedBatch_RespectsBatchSizeAndOldestFirstOrdering()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var oldest = CreateRequest(userId, Cutoff.AddMinutes(-3), completed: true, hashSeed: 11);
        var middle = CreateRequest(userId, Cutoff.AddMinutes(-2), completed: true, hashSeed: 12);
        var newest = CreateRequest(userId, Cutoff.AddMinutes(-1), completed: true, hashSeed: 13);
        await SeedRequestsAsync(factory, newest, oldest, middle);

        int firstDeletedCount;
        using (var firstScope = factory.Services.CreateScope())
        {
            var cleanup = firstScope.ServiceProvider.GetRequiredService<IIdempotencyCleanupStore>();
            firstDeletedCount = await cleanup.DeleteCompletedBatchAsync(Cutoff, 2, CancellationToken.None);
        }

        Assert.Equal(2, firstDeletedCount);
        var afterFirstBatch = await ReadRequestsAsync(factory, oldest.Id, middle.Id, newest.Id);
        var onlyRemaining = Assert.Single(afterFirstBatch);
        Assert.Equal(newest.Id, onlyRemaining.Id);

        int secondDeletedCount;
        int thirdDeletedCount;
        using (var remainingScope = factory.Services.CreateScope())
        {
            var cleanup = remainingScope.ServiceProvider.GetRequiredService<IIdempotencyCleanupStore>();
            secondDeletedCount = await cleanup.DeleteCompletedBatchAsync(Cutoff, 2, CancellationToken.None);
            thirdDeletedCount = await cleanup.DeleteCompletedBatchAsync(Cutoff, 2, CancellationToken.None);
        }

        Assert.Equal(1, secondDeletedCount);
        Assert.Equal(0, thirdDeletedCount);
        Assert.Empty(await ReadRequestsAsync(factory, oldest.Id, middle.Id, newest.Id));
    }

    [Fact]
    public async Task DeleteCompletedBatch_NeverDeletesProcessingRowsRegardlessOfAge()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var recentlyEligible = CreateRequest(userId, Cutoff.AddMinutes(-1), completed: false, hashSeed: 21);
        var longExpired = CreateRequest(userId, Cutoff.AddDays(-30), completed: false, hashSeed: 22);
        await SeedRequestsAsync(factory, recentlyEligible, longExpired);

        int deletedCount;
        using (var cleanupScope = factory.Services.CreateScope())
        {
            var cleanup = cleanupScope.ServiceProvider.GetRequiredService<IIdempotencyCleanupStore>();
            deletedCount = await cleanup.DeleteCompletedBatchAsync(Cutoff, 500, CancellationToken.None);
        }

        Assert.Equal(0, deletedCount);
        var remaining = await ReadRequestsAsync(factory, recentlyEligible.Id, longExpired.Id);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, request => Assert.Equal(IdempotencyRequestStatus.Processing, request.Status));
    }

    [Fact]
    public async Task DeleteCompletedBatch_WhenOldestRowIsLocked_SkipsItAndDeletesNextEligibleRow()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var lockedOldest = CreateRequest(userId, Cutoff.AddMinutes(-2), completed: true, hashSeed: 31);
        var nextEligible = CreateRequest(userId, Cutoff.AddMinutes(-1), completed: true, hashSeed: 32);
        await SeedRequestsAsync(factory, lockedOldest, nextEligible);

        await using var lockingConnection = new NpgsqlConnection(postgres.ConnectionString);
        await lockingConnection.OpenAsync();
        await using var lockingTransaction = await lockingConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM idempotency_requests WHERE id = @id FOR UPDATE",
            lockingConnection,
            lockingTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", lockedOldest.Id);
            Assert.Equal(lockedOldest.Id, await lockCommand.ExecuteScalarAsync());
        }

        using var cleanupScope = factory.Services.CreateScope();
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<IIdempotencyCleanupStore>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var deletedCount = await cleanup.DeleteCompletedBatchAsync(Cutoff, 1, timeout.Token);

        Assert.Equal(1, deletedCount);
        var remaining = await ReadRequestsAsync(factory, lockedOldest.Id, nextEligible.Id);
        Assert.Contains(remaining, request => request.Id == lockedOldest.Id);
        Assert.DoesNotContain(remaining, request => request.Id == nextEligible.Id);

        await lockingTransaction.RollbackAsync();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));

    private static async Task<Guid> SeedUserAsync(WebApplicationFactory<Program> factory)
    {
        await MigrateDatabaseAsync(factory);
        var userId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await db.IdempotencyRequests.ExecuteDeleteAsync();
        db.Users.Add(new User(
            userId,
            $"idempotency-cleanup-{userId:N}@example.com",
            $"idempotency-cleanup-{userId:N}@example.com",
            TestCredentials.CreateHashPlaceholder(),
            UserRole.Customer,
            Cutoff.AddDays(-100)));
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task SeedRequestsAsync(
        WebApplicationFactory<Program> factory,
        params IdempotencyRequest[] requests)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        db.IdempotencyRequests.AddRange(requests);
        await db.SaveChangesAsync();
    }

    private static async Task<List<IdempotencyRequest>> ReadRequestsAsync(
        WebApplicationFactory<Program> factory,
        params Guid[] requestIds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        return await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request => requestIds.Contains(request.Id))
            .OrderBy(request => request.DeleteAfter)
            .ThenBy(request => request.Id)
            .ToListAsync();
    }

    private static IdempotencyRequest CreateRequest(
        Guid userId,
        DateTimeOffset deleteAfter,
        bool completed,
        byte hashSeed)
    {
        var createdAt = deleteAfter.AddHours(-72);
        var request = new IdempotencyRequest(
            Guid.NewGuid(),
            userId,
            IdempotencyOperation.CreateOrder,
            Guid.NewGuid(),
            Enumerable.Range(0, IdempotencyRequest.RequestHashLength)
                .Select(offset => unchecked((byte)(hashSeed + offset)))
                .ToArray(),
            createdAt,
            createdAt.AddHours(24),
            deleteAfter);

        if (completed)
        {
            var resourceId = Guid.NewGuid();
            request.Complete(
                resourceId,
                IdempotencyRequest.CreateOrderCompletedStatusCode,
                $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}",
                createdAt.AddMinutes(1));
        }

        return request;
    }

    private static async Task MigrateDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await db.Database.MigrateAsync();
    }
}

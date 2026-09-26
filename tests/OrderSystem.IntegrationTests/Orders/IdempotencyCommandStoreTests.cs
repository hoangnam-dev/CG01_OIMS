using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Orders;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class IdempotencyCommandStoreTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryClaimCreateOrderAsync_NewIdentity_InsertsProcessingAndReturnsClaimed()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var claim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        IdempotencyClaimResult result;

        using (var commandScope = factory.Services.CreateScope())
        {
            var store = commandScope.ServiceProvider
                .GetRequiredService<IOrderCommandStore>();

            await using var transaction =
                await store.BeginTransactionAsync(CancellationToken.None);

            result = await store.TryClaimCreateOrderAsync(
                claim,
                CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(IdempotencyClaimOutcome.Claimed, result.Outcome);
        Assert.Null(result.StoredResponse);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var persisted = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(request => request.Id == claim.Id);

        Assert.Equal(userId, persisted.UserId);
        Assert.Equal(IdempotencyOperation.CreateOrder, persisted.Operation);
        Assert.Equal(claim.IdempotencyKey, persisted.IdempotencyKey);
        Assert.Equal(claim.RequestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Processing, persisted.Status);
        Assert.Equal(FixedNow, persisted.CreatedAt);
        Assert.Equal(FixedNow.AddHours(24), persisted.ExpiresAt);
        Assert.Equal(FixedNow.AddHours(72), persisted.DeleteAfter);
        Assert.Null(persisted.ResourceId);
        Assert.Null(persisted.HttpStatusCode);
        Assert.Null(persisted.ResponseBodyJson);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_WithoutActiveTransaction_ThrowsInvalidOperationException()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var claim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            Enumerable.Range(32, 32)
                .Select(value => (byte)value)
                .ToArray(),
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72)
        );

        using var commandSope = factory.Services.CreateScope();
        var store = commandSope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TryClaimCreateOrderAsync(claim, CancellationToken.None)
        );

        Assert.Contains(
            "active database transaction",
            exception.Message,
            StringComparison.OrdinalIgnoreCase
        );

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        Assert.False(await db.IdempotencyRequests.AsNoTracking().AnyAsync(request => request.Id == claim.Id));
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ExistingSameHashProcessing_ReturnsProcessing()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var requestHash = Enumerable.Range(64, 32)
            .Select(value => (byte)value)
            .ToArray();

        var originalClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        // Persist and commit the original Processing claim
        using (var ownerScope = factory.Services.CreateScope())
        {
            var ownerStore = ownerScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var ownerTransaction = await ownerStore.BeginTransactionAsync(CancellationToken.None);

            var ownerResult = await ownerStore.TryClaimCreateOrderAsync(originalClaim, CancellationToken.None);

            Assert.Equal(IdempotencyClaimOutcome.Claimed, ownerResult.Outcome);

            await ownerTransaction.CommitAsync(CancellationToken.None);
        }

        // A retry supplies a new candidate row ID and timestamps,
        // but retains the same durable identity and canonical hash
        var retryClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            [.. requestHash],
            FixedNow.AddMinutes(5),
            FixedNow.AddHours(24).AddMinutes(5),
            FixedNow.AddHours(72).AddMinutes(5)
        );

        IdempotencyClaimResult retryResult;

        using (var retryScope = factory.Services.CreateScope())
        {
            var retryStore = retryScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var retryTransaction = await retryStore.BeginTransactionAsync(CancellationToken.None);

            retryResult = await retryStore.TryClaimCreateOrderAsync(retryClaim, CancellationToken.None);

            await retryTransaction.CommitAsync(CancellationToken.None);
        }
        ;

        Assert.Equal(IdempotencyClaimOutcome.Processing, retryResult.Outcome);
        Assert.Null(retryResult.StoredResponse);

        // Verify that the retry did not overwrite the original row.
        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var row = await db.IdempotencyRequests.AsNoTracking()
            .Where(i =>
                i.UserId == userId &&
                i.Operation == IdempotencyOperation.CreateOrder &&
                i.IdempotencyKey == idempotencyKey
            )
            .ToListAsync();

        var persisted = Assert.Single(row);
        Assert.Equal(originalClaim.Id, persisted.Id);
        Assert.NotEqual(retryClaim.Id, persisted.Id);
        Assert.Equal(requestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Processing, persisted.Status);
        Assert.Equal(originalClaim.CreatedAt, persisted.CreatedAt);
        Assert.Equal(originalClaim.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(originalClaim.DeleteAfter, persisted.DeleteAfter);
        Assert.Null(persisted.ResourceId);
        Assert.Null(persisted.HttpStatusCode);
        Assert.Null(persisted.ResponseBodyJson);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ExistingDifferentHash_ReturnsHashConflict()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();

        var originalHash = Enumerable.Range(96, 32)
            .Select(value => (byte)value)
            .ToArray();

        var conflictingHash = Enumerable.Range(128, 32)
            .Select(value => (byte)value)
            .ToArray();

        var originalClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            originalHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        // Commit the original Processing identity.
        using (var ownerScope = factory.Services.CreateScope())
        {
            var ownerStore = ownerScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var ownerTransaction = await ownerStore.BeginTransactionAsync(CancellationToken.None);

            var ownerResult = await ownerStore.TryClaimCreateOrderAsync(originalClaim, CancellationToken.None);

            Assert.Equal(IdempotencyClaimOutcome.Claimed, ownerResult.Outcome);

            await ownerTransaction.CommitAsync(CancellationToken.None);
        }

        var conflictingClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            conflictingHash,
            FixedNow.AddMinutes(5),
            FixedNow.AddHours(24).AddMinutes(5),
            FixedNow.AddHours(72).AddMinutes(5));

        IdempotencyClaimResult conflictResult;

        using (var conflictScope = factory.Services.CreateScope())
        {
            var conflictStore = conflictScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var conflictTransaction = await conflictStore.BeginTransactionAsync(CancellationToken.None);

            conflictResult = await conflictStore.TryClaimCreateOrderAsync(conflictingClaim, CancellationToken.None);

            await conflictTransaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(IdempotencyClaimOutcome.HashConflict, conflictResult.Outcome);
        Assert.Null(conflictResult.StoredResponse);

        // The conflicting retry must not replace or mutate the original row.
        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(originalClaim.Id, persisted.Id);
        Assert.NotEqual(conflictingClaim.Id, persisted.Id);
        Assert.Equal(originalHash, persisted.RequestHash);
        Assert.NotEqual(conflictingHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Processing, persisted.Status);
        Assert.Equal(originalClaim.CreatedAt, persisted.CreatedAt);
        Assert.Equal(originalClaim.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(originalClaim.DeleteAfter, persisted.DeleteAfter);
        Assert.Null(persisted.ResourceId);
        Assert.Null(persisted.HttpStatusCode);
        Assert.Null(persisted.ResponseBodyJson);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ExistingSameHashCompletedBeforeExpiry_ReturnsCompletedReplay()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var requestHash = Enumerable.Range(160, 32)
            .Select(value => (byte)value)
            .ToArray();
        var responseBody = $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}";

        var storedRequest = new IdempotencyRequest(
            Guid.NewGuid(),
            userId,
            IdempotencyOperation.CreateOrder,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        storedRequest.Complete(
            resourceId,
            IdempotencyRequest.CreateOrderCompletedStatusCode,
            responseBody,
            FixedNow.AddHours(1));

        // Seed an already completed durable operation
        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            setupDb.IdempotencyRequests.Add(storedRequest);
            await setupDb.SaveChangesAsync();
        }

        // The retry occurs before the original 24-hour expiry
        var retryClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            [.. requestHash],
            FixedNow.AddHours(2),
            FixedNow.AddHours(26),
            FixedNow.AddHours(74));

        IdempotencyClaimResult result;

        using (var retryScope = factory.Services.CreateScope())
        {
            var store = retryScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction =
                await store.BeginTransactionAsync(CancellationToken.None);

            result = await store.TryClaimCreateOrderAsync(retryClaim, CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(IdempotencyClaimOutcome.CompletedReplay, result.Outcome);

        var storedResponse =
            Assert.IsType<IdempotencyStoredResponse>(result.StoredResponse);

        Assert.Equal(resourceId, storedResponse.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, storedResponse.HttpStatusCode);
        Assert.Equal(responseBody, storedResponse.ResponseBodyJson);

        // Replay must not replace or mutate the original record
        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(storedRequest.Id, persisted.Id);
        Assert.NotEqual(retryClaim.Id, persisted.Id);
        Assert.Equal(requestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Completed, persisted.Status);
        Assert.Equal(resourceId, persisted.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, persisted.HttpStatusCode);
        Assert.Equal(responseBody, persisted.ResponseBodyJson);
        Assert.Equal(FixedNow.AddHours(1), persisted.CompletedAt);
        Assert.Equal(FixedNow, persisted.CreatedAt);
        Assert.Equal(FixedNow.AddHours(24), persisted.ExpiresAt);
        Assert.Equal(FixedNow.AddHours(72), persisted.DeleteAfter);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ExistingSameHashCompletedAtExpiry_ReturnsExpired()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var requestHash = Enumerable.Range(192, 32)
            .Select(value => (byte)value)
            .ToArray();
        var responseBody = $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}";

        var storedRequest = new IdempotencyRequest(
            Guid.NewGuid(),
            userId,
            IdempotencyOperation.CreateOrder,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        storedRequest.Complete(
            resourceId,
            IdempotencyRequest.CreateOrderCompletedStatusCode,
            responseBody,
            FixedNow.AddHours(1));

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            setupDb.IdempotencyRequests.Add(storedRequest);
            await setupDb.SaveChangesAsync();
        }

        var retryAt = storedRequest.ExpiresAt;

        var retryClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            [.. requestHash],
            retryAt,
            retryAt.AddHours(24),
            retryAt.AddHours(72));

        IdempotencyClaimResult result;

        using (var retryScope = factory.Services.CreateScope())
        {
            var store = retryScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            result = await store.TryClaimCreateOrderAsync(retryClaim, CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(IdempotencyClaimOutcome.Expired, result.Outcome);
        Assert.Null(result.StoredResponse);

        // Expiry does not release or delete the durable identity
        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(storedRequest.Id, persisted.Id);
        Assert.NotEqual(retryClaim.Id, persisted.Id);
        Assert.Equal(requestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Completed, persisted.Status);
        Assert.Equal(resourceId, persisted.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, persisted.HttpStatusCode);
        Assert.Equal(responseBody, persisted.ResponseBodyJson);
        Assert.Equal(FixedNow.AddHours(1), persisted.CompletedAt);
        Assert.Equal(FixedNow, persisted.CreatedAt);
        Assert.Equal(FixedNow.AddHours(24), persisted.ExpiresAt);
        Assert.Equal(FixedNow.AddHours(72), persisted.DeleteAfter);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ExpiredCompletedWithDifferentHash_ReturnsHashConflict()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var originalHash = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var conflictingHash = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var responseBody = $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}";

        var storedRequest = new IdempotencyRequest(
            Guid.NewGuid(),
            userId,
            IdempotencyOperation.CreateOrder,
            idempotencyKey,
            originalHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        storedRequest.Complete(
            resourceId,
            IdempotencyRequest.CreateOrderCompletedStatusCode,
            responseBody,
            FixedNow.AddHours(1));

        using (var setupScope = factory.Services.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            db.IdempotencyRequests.Add(storedRequest);
            await db.SaveChangesAsync();
        }

        // After expiry but before delete_after
        var retryAt = FixedNow.AddHours(48);
        var conflictingClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            conflictingHash,
            retryAt,
            retryAt.AddHours(24),
            retryAt.AddHours(72));

        IdempotencyClaimResult result;

        using (var retryScope = factory.Services.CreateScope())
        {
            var store = retryScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            result = await store.TryClaimCreateOrderAsync(conflictingClaim, CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(IdempotencyClaimOutcome.HashConflict, result.Outcome);
        Assert.Null(result.StoredResponse);

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var persisted = await assertionDb.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey);

        Assert.Equal(storedRequest.Id, persisted.Id);
        Assert.NotEqual(conflictingClaim.Id, persisted.Id);
        Assert.Equal(originalHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Completed, persisted.Status);
        Assert.Equal(resourceId, persisted.ResourceId);
        Assert.Equal(responseBody, persisted.ResponseBodyJson);
        Assert.Equal(FixedNow.AddHours(24), persisted.ExpiresAt);
        Assert.Equal(FixedNow.AddHours(72), persisted.DeleteAfter);
    }

    [Fact]
    public async Task TryCompleteCreateOrderAsync_ProcessingRow_StoresOpaqueResponseAndReturnsTrue()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var resourceId = Guid.NewGuid();
        var responseBody = $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}";
        var completedAt = FixedNow.AddHours(1);

        var claim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0xC3, 32).ToArray(),
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        bool completed;

        // Claim and completion must belong to the same database transaction
        using (var commandScope = factory.Services.CreateScope())
        {
            var store = commandScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            var claimResult = await store.TryClaimCreateOrderAsync(claim, CancellationToken.None);

            Assert.Equal(IdempotencyClaimOutcome.Claimed, claimResult.Outcome);

            completed = await store.TryCompleteCreateOrderAsync(
                claim.Id,
                resourceId,
                IdempotencyRequest.CreateOrderCompletedStatusCode,
                responseBody,
                completedAt,
                CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.True(completed);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var persisted = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(request => request.Id == claim.Id);

        Assert.Equal(IdempotencyRequestStatus.Completed, persisted.Status);
        Assert.Equal(resourceId, persisted.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, persisted.HttpStatusCode);
        Assert.Equal(responseBody, persisted.ResponseBodyJson);
        Assert.Equal(completedAt, persisted.CompletedAt);

        // Claim identity and lifecycle timestamps remain immutable
        Assert.Equal(userId, persisted.UserId);
        Assert.Equal(IdempotencyOperation.CreateOrder, persisted.Operation);
        Assert.Equal(claim.IdempotencyKey, persisted.IdempotencyKey);
        Assert.Equal(claim.RequestHash, persisted.RequestHash);
        Assert.Equal(claim.CreatedAt, persisted.CreatedAt);
        Assert.Equal(claim.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(claim.DeleteAfter, persisted.DeleteAfter);
    }

    [Fact]
    public async Task TryCompleteCreateOrderAsync_WithoutActiveTransaction_ThrowsInvalidOperationException()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var resourceId = Guid.NewGuid();
        const string responseBody = "{\"data\":{\"status\":\"created\"}}";

        var claim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0xD1, 32).ToArray(),
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        // Persist a Processing row first
        using (var claimScope = factory.Services.CreateScope())
        {
            var store = claimScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            var result = await store.TryClaimCreateOrderAsync(claim, CancellationToken.None);

            Assert.Equal(IdempotencyClaimOutcome.Claimed, result.Outcome);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Complete without opening a transaction
        using (var completionScope = factory.Services.CreateScope())
        {
            var store = completionScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.TryCompleteCreateOrderAsync(
                    claim.Id,
                    resourceId,
                    IdempotencyRequest.CreateOrderCompletedStatusCode,
                    responseBody,
                    FixedNow.AddHours(1),
                    CancellationToken.None));

            Assert.Contains(
                "active database transaction",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        // The failed completion must leave the row Processing
        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var persisted = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(request => request.Id == claim.Id);

        Assert.Equal(IdempotencyRequestStatus.Processing, persisted.Status);
        Assert.Null(persisted.ResourceId);
        Assert.Null(persisted.HttpStatusCode);
        Assert.Null(persisted.ResponseBodyJson);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task TryCompleteCreateOrderAsync_CompletionBeforeClaim_ThrowsArgumentOutOfRangeException()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var resourceId = Guid.NewGuid();
        var claim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0xA8, 32).ToArray(),
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        var claimResult = await store.TryClaimCreateOrderAsync(claim, CancellationToken.None);
        Assert.Equal(IdempotencyClaimOutcome.Claimed, claimResult.Outcome);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.TryCompleteCreateOrderAsync(
                claim.Id,
                resourceId,
                IdempotencyRequest.CreateOrderCompletedStatusCode,
                $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}",
                FixedNow.AddMinutes(-1),
                CancellationToken.None));

        Assert.Equal("completedAt", exception.ParamName);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TryCompleteCreateOrderAsync_AlreadyCompleted_ReturnsFalseAndPreservesOriginalSnapshot()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);

        var originalResourceId = Guid.NewGuid();
        var replacementResourceId = Guid.NewGuid();
        var originalCompletedAt = FixedNow.AddHours(1);
        var replacementCompletedAt = FixedNow.AddHours(2);
        var originalBody = $"{{\"data\":{{\"id\":\"{originalResourceId:D}\"}}}}";
        var replacementBody = $"{{\"data\":{{\"id\":\"{replacementResourceId:D}\"}}}}";

        var claim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0xE2, 32).ToArray(),
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        // Claim and complete using the original snapshot
        using (var ownerScope = factory.Services.CreateScope())
        {
            var store = ownerScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            var claimResult = await store.TryClaimCreateOrderAsync(claim, CancellationToken.None);

            Assert.Equal(IdempotencyClaimOutcome.Claimed, claimResult.Outcome);

            var firstCompletion = await store.TryCompleteCreateOrderAsync(
                claim.Id,
                originalResourceId,
                IdempotencyRequest.CreateOrderCompletedStatusCode,
                originalBody,
                originalCompletedAt,
                CancellationToken.None);

            Assert.True(firstCompletion);

            await transaction.CommitAsync(CancellationToken.None);
        }

        bool secondCompletion;

        // Attempt to overwrite the already-completed snapshot
        using (var retryScope = factory.Services.CreateScope())
        {
            var store = retryScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

            await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

            secondCompletion = await store.TryCompleteCreateOrderAsync(
                claim.Id,
                replacementResourceId,
                IdempotencyRequest.CreateOrderCompletedStatusCode,
                replacementBody,
                replacementCompletedAt,
                CancellationToken.None);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.False(secondCompletion);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var persisted = await db.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(request => request.Id == claim.Id);

        Assert.Equal(IdempotencyRequestStatus.Completed, persisted.Status);
        Assert.Equal(originalResourceId, persisted.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, persisted.HttpStatusCode);
        Assert.Equal(originalBody, persisted.ResponseBodyJson);
        Assert.Equal(originalCompletedAt, persisted.CompletedAt);

        Assert.NotEqual(replacementResourceId, persisted.ResourceId);
        Assert.NotEqual(replacementBody, persisted.ResponseBodyJson);
        Assert.NotEqual(replacementCompletedAt, persisted.CompletedAt);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_SameKeyForDifferentUsers_ClaimsIndependentNamespaces()
    {
        await using var factory = CreateFactory();
        var firstUserId = await SeedUserAsync(factory);
        var secondUserId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var requestHash = Enumerable.Repeat((byte)0xB7, 32).ToArray();
        var firstClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            firstUserId,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));
        var secondClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            secondUserId,
            idempotencyKey,
            [.. requestHash],
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        IdempotencyClaimResult firstResult;
        using (var firstScope = factory.Services.CreateScope())
        {
            var firstStore = firstScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
            await using var transaction = await firstStore.BeginTransactionAsync(CancellationToken.None);
            firstResult = await firstStore.TryClaimCreateOrderAsync(firstClaim, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        IdempotencyClaimResult secondResult;
        using (var secondScope = factory.Services.CreateScope())
        {
            var secondStore = secondScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
            await using var transaction = await secondStore.BeginTransactionAsync(CancellationToken.None);
            secondResult = await secondStore.TryClaimCreateOrderAsync(secondClaim, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(IdempotencyClaimOutcome.Claimed, firstResult.Outcome);
        Assert.Equal(IdempotencyClaimOutcome.Claimed, secondResult.Outcome);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, request => request.Id == firstClaim.Id && request.UserId == firstUserId);
        Assert.Contains(rows, request => request.Id == secondClaim.Id && request.UserId == secondUserId);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ConcurrentSameIdentityWinnerCommits_LoserReturnsProcessing()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var requestHash = Enumerable.Repeat((byte)0xF1, 32).ToArray();

        var winnerClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        var loserClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            [.. requestHash],
            FixedNow.AddMinutes(1),
            FixedNow.AddHours(24).AddMinutes(1),
            FixedNow.AddHours(72).AddMinutes(1));

        using var winnerScope = factory.Services.CreateScope();
        var winnerStore = winnerScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await using var winnerTransaction = await winnerStore.BeginTransactionAsync(CancellationToken.None);

        var winnerResult = await winnerStore.TryClaimCreateOrderAsync(winnerClaim, CancellationToken.None);

        Assert.Equal(IdempotencyClaimOutcome.Claimed, winnerResult.Outcome);

        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loserTask = ClaimFromIndependentScopeAsync(
            factory,
            loserClaim,
            loserStarted);

        var startSignal = await Task.WhenAny(
            loserStarted.Task,
            loserTask,
            Task.Delay(TimeSpan.FromSeconds(10)));

        if (startSignal == loserTask)
        {
            await loserTask;
        }

        if (startSignal != loserStarted.Task)
        {
            throw new TimeoutException("The losing transaction did not begin its claim within 10 seconds.");
        }

        try
        {
            // The unique-index conflict must wait for the uncommitted winner
            var completedTask = await Task.WhenAny(
                loserTask,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.NotSame(loserTask, completedTask);
        }
        finally
        {
            // Always release PostgreSQL's blocked contender
            await winnerTransaction.CommitAsync(CancellationToken.None);
        }

        var loserResult = await loserTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IdempotencyClaimOutcome.Processing, loserResult.Outcome);
        Assert.Null(loserResult.StoredResponse);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(winnerClaim.Id, persisted.Id);
        Assert.NotEqual(loserClaim.Id, persisted.Id);
        Assert.Equal(requestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Processing, persisted.Status);
        Assert.Equal(winnerClaim.CreatedAt, persisted.CreatedAt);
        Assert.Equal(winnerClaim.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(winnerClaim.DeleteAfter, persisted.DeleteAfter);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ConcurrentSameIdentityWinnerRollsBack_LoserBecomesClaimed()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var requestHash = Enumerable.Repeat((byte)0xF2, 32).ToArray();

        var winnerClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        var loserClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            [.. requestHash],
            FixedNow.AddMinutes(1),
            FixedNow.AddHours(24).AddMinutes(1),
            FixedNow.AddHours(72).AddMinutes(1));

        using var winnerScope = factory.Services.CreateScope();
        var winnerStore = winnerScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await using var winnerTransaction = await winnerStore.BeginTransactionAsync(CancellationToken.None);

        var winnerResult = await winnerStore.TryClaimCreateOrderAsync(winnerClaim, CancellationToken.None);

        Assert.Equal(IdempotencyClaimOutcome.Claimed, winnerResult.Outcome);

        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loserTask = ClaimFromIndependentScopeAsync(
            factory,
            loserClaim,
            loserStarted);

        await loserStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            // Loser must wait while Winner's unique identity is uncommitted
            var completedTask = await Task.WhenAny(
                loserTask,
                Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.NotSame(loserTask, completedTask);
        }
        finally
        {
            // Rollback releases the unique identity instead of making it durable
            await winnerTransaction.RollbackAsync(CancellationToken.None);
        }

        var loserResult = await loserTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IdempotencyClaimOutcome.Claimed, loserResult.Outcome);
        Assert.Null(loserResult.StoredResponse);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        // Winner's uncommitted row disappeared
        Assert.NotEqual(winnerClaim.Id, persisted.Id);

        // Loser acquired the released identity and became the durable owner
        Assert.Equal(loserClaim.Id, persisted.Id);
        Assert.Equal(loserClaim.RequestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Processing, persisted.Status);
        Assert.Equal(loserClaim.CreatedAt, persisted.CreatedAt);
        Assert.Equal(loserClaim.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(loserClaim.DeleteAfter, persisted.DeleteAfter);
        Assert.Null(persisted.ResourceId);
        Assert.Null(persisted.HttpStatusCode);
        Assert.Null(persisted.ResponseBodyJson);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task TryClaimCreateOrderAsync_ConcurrentWinnerCompletesAndCommits_LoserReturnsCompletedReplay()
    {
        await using var factory = CreateFactory();
        var userId = await SeedUserAsync(factory);
        var idempotencyKey = Guid.NewGuid();
        var requestHash = Enumerable.Repeat((byte)0xF3, 32).ToArray();
        var resourceId = Guid.NewGuid();
        var completedAt = FixedNow.AddMinutes(1);
        var responseBody = $"{{\"data\":{{\"id\":\"{resourceId:D}\"}}}}";

        var winnerClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            requestHash,
            FixedNow,
            FixedNow.AddHours(24),
            FixedNow.AddHours(72));

        var loserClaim = new CreateOrderIdempotencyClaim(
            Guid.NewGuid(),
            userId,
            idempotencyKey,
            [.. requestHash],
            FixedNow.AddMinutes(2),
            FixedNow.AddHours(24).AddMinutes(2),
            FixedNow.AddHours(72).AddMinutes(2));

        using var winnerScope = factory.Services.CreateScope();
        var winnerStore = winnerScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await using var winnerTransaction = await winnerStore.BeginTransactionAsync(CancellationToken.None);

        var winnerResult = await winnerStore.TryClaimCreateOrderAsync(winnerClaim, CancellationToken.None);

        Assert.Equal(IdempotencyClaimOutcome.Claimed, winnerResult.Outcome);

        var completionResult = await winnerStore.TryCompleteCreateOrderAsync(
            winnerClaim.Id,
            resourceId,
            IdempotencyRequest.CreateOrderCompletedStatusCode,
            responseBody,
            completedAt,
            CancellationToken.None);

        Assert.True(completionResult);

        // The row is Completed inside Winner's transaction but not yet visible
        var loserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loserTask = ClaimFromIndependentScopeAsync(
            factory,
            loserClaim,
            loserStarted);

        await loserStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            var completedTask = await Task.WhenAny(
                loserTask,
                Task.Delay(TimeSpan.FromSeconds(1)));

            // Loser must wait for Winner's transaction outcome
            Assert.NotSame(loserTask, completedTask);
        }
        finally
        {
            await winnerTransaction.CommitAsync(CancellationToken.None);
        }

        var loserResult = await loserTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IdempotencyClaimOutcome.CompletedReplay, loserResult.Outcome);

        var replay = Assert.IsType<IdempotencyStoredResponse>(loserResult.StoredResponse);

        Assert.Equal(resourceId, replay.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, replay.HttpStatusCode);
        Assert.Equal(responseBody, replay.ResponseBodyJson);

        using var assertionScope = factory.Services.CreateScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var rows = await db.IdempotencyRequests
            .AsNoTracking()
            .Where(request =>
                request.UserId == userId &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        var persisted = Assert.Single(rows);

        Assert.Equal(winnerClaim.Id, persisted.Id);
        Assert.NotEqual(loserClaim.Id, persisted.Id);
        Assert.Equal(requestHash, persisted.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Completed, persisted.Status);
        Assert.Equal(resourceId, persisted.ResourceId);
        Assert.Equal(IdempotencyRequest.CreateOrderCompletedStatusCode, persisted.HttpStatusCode);
        Assert.Equal(responseBody, persisted.ResponseBodyJson);
        Assert.Equal(completedAt, persisted.CompletedAt);
        Assert.Equal(winnerClaim.CreatedAt, persisted.CreatedAt);
        Assert.Equal(winnerClaim.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(winnerClaim.DeleteAfter, persisted.DeleteAfter);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));

    private static async Task<Guid> SeedUserAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        await db.Database.MigrateAsync();

        var userId = Guid.NewGuid();
        db.Users.Add(new User(
            userId,
            $"idempotency-store-{userId:N}@example.com",
            $"idempotency-store-{userId:N}@example.com",
            "test-password-hash",
            UserRole.Customer,
            FixedNow));

        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task<IdempotencyClaimResult>
    ClaimFromIndependentScopeAsync(
        WebApplicationFactory<Program> factory,
        CreateOrderIdempotencyClaim claim,
        TaskCompletionSource claimStarted)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);

        // Signal immediately before the INSERT expected to block
        claimStarted.TrySetResult();

        var result = await store.TryClaimCreateOrderAsync(claim, CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        return result;
    }
}

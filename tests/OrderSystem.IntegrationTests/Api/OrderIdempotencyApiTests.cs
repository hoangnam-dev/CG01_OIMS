using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderIdempotencyApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "API-IDEM-001")]
    public async Task CreateOrder_SameKeyAndEquivalentPayload_ReplaysOneLogicalResult()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory);
        var firstVariant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var secondVariant = await SeedVariantWithInventoryAsync(factory, 10, 20m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        var key = Guid.NewGuid();
        CreateOrderItemRequest[] originalItems =
        [
            new(firstVariant.Id, 1),
            new(secondVariant.Id, 2)
        ];
        CreateOrderItemRequest[] reorderedItems =
        [
            new(secondVariant.Id, 2),
            new(firstVariant.Id, 1)
        ];

        using var firstRequest = CreateRequest(key, originalItems);
        using var firstResponse = await client.SendAsync(firstRequest);
        var firstBytes = await firstResponse.Content.ReadAsByteArrayAsync();
        var firstBody = Encoding.UTF8.GetString(firstBytes);
        using var replayRequest = CreateRequest(key, reorderedItems);
        using var replayResponse = await client.SendAsync(replayRequest);
        var replayBytes = await replayResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal("application/json", firstResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/json", replayResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(firstBytes, replayBytes);
        var orderId = ReadOrderId(firstBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = Assert.Single(await db.Orders.AsNoTracking()
            .Where(candidate => candidate.UserId == customer.Id)
            .ToListAsync());
        Assert.Equal(orderId, order.Id);

        var items = await db.OrderItems.AsNoTracking()
            .Where(item => item.OrderId == order.Id)
            .ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.ProductVariantId == firstVariant.Id && item.Quantity == 1);
        Assert.Contains(items, item => item.ProductVariantId == secondVariant.Id && item.Quantity == 2);

        var inventories = await db.Inventories.AsNoTracking()
            .Where(inventory => inventory.ProductVariantId == firstVariant.Id || inventory.ProductVariantId == secondVariant.Id)
            .ToDictionaryAsync(inventory => inventory.ProductVariantId);
        Assert.Equal(1, inventories[firstVariant.Id].ReservedQuantity);
        Assert.Equal(2, inventories[secondVariant.Id].ReservedQuantity);

        var reserveEntries = await db.InventoryTransactions.AsNoTracking()
            .Where(entry => entry.ReferenceId == order.Id && entry.Type == InventoryTransactionType.Reserve)
            .ToListAsync();
        Assert.Equal(2, reserveEntries.Count);
        Assert.Contains(reserveEntries, entry => entry.ProductVariantId == firstVariant.Id && entry.ReservedQuantityDelta == 1);
        Assert.Contains(reserveEntries, entry => entry.ProductVariantId == secondVariant.Id && entry.ReservedQuantityDelta == 2);

        var idempotency = Assert.Single(await db.IdempotencyRequests.AsNoTracking()
            .Where(request => request.UserId == customer.Id &&
                request.Operation == IdempotencyOperation.CreateOrder &&
                request.IdempotencyKey == key)
            .ToListAsync());
        Assert.Equal(CreateOrderRequestHasher.Hash(originalItems), idempotency.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Completed, idempotency.Status);
        Assert.Equal(order.Id, idempotency.ResourceId);
        Assert.Equal((short)201, idempotency.HttpStatusCode);
        Assert.Equal(firstBody, idempotency.ResponseBodyJson);
        Assert.NotNull(idempotency.CompletedAt);
        Assert.True(idempotency.CompletedAt >= idempotency.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(24), idempotency.ExpiresAt - idempotency.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(72), idempotency.DeleteAfter - idempotency.CreatedAt);

        Assert.Equal($"/api/orders/{orderId}", firstResponse.Headers.Location?.OriginalString);
        Assert.Equal(firstResponse.Headers.Location, replayResponse.Headers.Location);
        Assert.False(firstResponse.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal("true", Assert.Single(replayResponse.Headers.GetValues("Idempotency-Replayed")));
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-002")]
    public async Task CreateOrder_SameKeyWithDifferentPayload_ReturnsConflictWithoutNewEffect()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        var key = Guid.NewGuid();

        using var firstRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var firstResponse = await client.SendAsync(firstRequest);
        var originalOrderId = await ReadOrderIdAsync(firstResponse);
        var correlationId = Guid.NewGuid().ToString("D");
        using var conflictRequest = CreateRequest(key, [new(variant.Id, 2)]);
        conflictRequest.Headers.Add("X-Correlation-ID", correlationId);
        using var conflictResponse = await client.SendAsync(conflictRequest);

        await AssertProblemDetailsAsync(
            conflictResponse,
            HttpStatusCode.Conflict,
            expectedType: "https://oims.example/problems/idempotency-key-reused",
            expectedTitle: "Request conflict",
            expectedCode: "IDEMPOTENCY_KEY_REUSED",
            expectedMessage: "The idempotency key was already used for a different request.",
            expectedInstance: "/api/orders",
            expectedCorrelationId: correlationId,
            forbiddenValues: [key.ToString("D")]);
        await AssertOneCreateEffectAsync(factory, customer.Id, variant.Id, originalOrderId, 1);
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-004")]
    public async Task CreateOrder_ChangedIntentWithNewKey_CreatesIndependentOrders()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var firstRequest = CreateRequest(Guid.NewGuid(), [new(variant.Id, 1)]);
        using var firstResponse = await client.SendAsync(firstRequest);
        using var secondRequest = CreateRequest(Guid.NewGuid(), [new(variant.Id, 2)]);
        using var secondResponse = await client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.NotEqual(await ReadOrderIdAsync(firstResponse), await ReadOrderIdAsync(secondResponse));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        Assert.Equal(2, await db.Orders.AsNoTracking().CountAsync(order => order.UserId == customer.Id));
        Assert.Equal(2, await db.IdempotencyRequests.AsNoTracking().CountAsync(request => request.UserId == customer.Id));
        var inventory = await db.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == variant.Id);
        Assert.Equal(3, inventory.ReservedQuantity);
        Assert.Equal(2, await db.InventoryTransactions.AsNoTracking().CountAsync(entry =>
            entry.ProductVariantId == variant.Id && entry.Type == InventoryTransactionType.Reserve));
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-006")]
    public async Task CreateOrder_SameKeyForDifferentUsers_UsesIndependentNamespaces()
    {
        await using var factory = CreateFactory();
        var firstCustomer = await CreateUserAsync(factory);
        var secondCustomer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await AuthenticateAsync(firstClient, firstCustomer.Email);
        await AuthenticateAsync(secondClient, secondCustomer.Email);

        using var firstRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var firstResponse = await firstClient.SendAsync(firstRequest);
        using var secondRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var secondResponse = await secondClient.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.NotEqual(await ReadOrderIdAsync(firstResponse), await ReadOrderIdAsync(secondResponse));
        Assert.False(firstResponse.Headers.Contains("Idempotency-Replayed"));
        Assert.False(secondResponse.Headers.Contains("Idempotency-Replayed"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        Assert.Equal(2, await db.IdempotencyRequests.AsNoTracking().CountAsync(request =>
            request.IdempotencyKey == key && request.Operation == IdempotencyOperation.CreateOrder));
        Assert.Equal(2, await db.Orders.AsNoTracking().CountAsync(order =>
            order.UserId == firstCustomer.Id || order.UserId == secondCustomer.Id));
        var inventory = await db.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == variant.Id);
        Assert.Equal(2, inventory.ReservedQuantity);
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-007")]
    public async Task CreateOrder_ExpiredAccessToken_ReturnsUnauthorizedWithoutAnyEffect()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateFactory(clock: clock);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var request = CreateRequest(Guid.NewGuid(), [new(variant.Id, 1)]);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoCreateEffectAsync(factory, customer.Id, variant.Id);
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-008")]
    public async Task CreateOrder_RetainedExpiredCompletedRequest_ReturnsExpiredConflictWithoutNewEffect()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeClock(now);
        await using var factory = CreateFactory(clock: clock);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        CreateOrderItemRequest[] items = [new(variant.Id, 1)];
        var stored = new IdempotencyRequest(
            Guid.NewGuid(),
            customer.Id,
            IdempotencyOperation.CreateOrder,
            key,
            CreateOrderRequestHasher.Hash(items),
            now.AddHours(-48),
            now.AddHours(-24),
            now.AddHours(24));
        stored.Complete(Guid.NewGuid(), 201, "{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\"}}", now.AddHours(-47));
        await SeedIdempotencyRequestAsync(factory, stored);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        var correlationId = Guid.NewGuid().ToString("D");
        using var request = CreateRequest(key, items);
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(request);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Conflict,
            expectedType: "https://oims.example/problems/idempotency-key-expired",
            expectedTitle: "Request conflict",
            expectedCode: "IDEMPOTENCY_KEY_EXPIRED",
            expectedMessage: "The replay guarantee for this idempotency key has expired.",
            expectedInstance: "/api/orders",
            expectedCorrelationId: correlationId,
            forbiddenValues:
            [
                key.ToString("D"),
                Convert.ToHexString(CreateOrderRequestHasher.Hash(items)),
                Convert.ToBase64String(CreateOrderRequestHasher.Hash(items)),
                stored.ResponseBodyJson!
            ]);
        await AssertNoCreateEffectAsync(factory, customer.Id, variant.Id, expectedIdempotencyRows: 1);
        await AssertStoredIdempotencyAsync(
            factory,
            stored.Id,
            IdempotencyRequestStatus.Completed,
            CreateOrderRequestHasher.Hash(items));
    }

    [Fact]
    public async Task CreateOrder_CommittedProcessingWithSameHash_ReturnsProcessingConflictWithoutTakeover()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeClock(now);
        await using var factory = CreateFactory(clock: clock);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        CreateOrderItemRequest[] items = [new(variant.Id, 1)];
        var stored = new IdempotencyRequest(
            Guid.NewGuid(), customer.Id, IdempotencyOperation.CreateOrder, key,
            CreateOrderRequestHasher.Hash(items), now.AddMinutes(-5), now.AddHours(23), now.AddHours(71));
        await SeedIdempotencyRequestAsync(factory, stored);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        var correlationId = Guid.NewGuid().ToString("D");
        using var request = CreateRequest(key, items);
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(request);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Conflict,
            expectedType: "https://oims.example/problems/idempotency-request-processing",
            expectedTitle: "Request conflict",
            expectedCode: "IDEMPOTENCY_REQUEST_PROCESSING",
            expectedMessage: "The idempotent request is still being processed.",
            expectedInstance: "/api/orders",
            expectedCorrelationId: correlationId,
            forbiddenValues:
            [
                key.ToString("D"),
                Convert.ToHexString(CreateOrderRequestHasher.Hash(items)),
                Convert.ToBase64String(CreateOrderRequestHasher.Hash(items))
            ]);
        await AssertNoCreateEffectAsync(factory, customer.Id, variant.Id, expectedIdempotencyRows: 1);
        await AssertStoredIdempotencyAsync(
            factory,
            stored.Id,
            IdempotencyRequestStatus.Processing,
            CreateOrderRequestHasher.Hash(items));
    }

    [Fact]
    public async Task CreateOrder_CommittedProcessingWithDifferentHash_ReturnsReusedConflictWithoutTakeover()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeClock(now);
        await using var factory = CreateFactory(clock: clock);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        var storedHash = CreateOrderRequestHasher.Hash([new(variant.Id, 1)]);
        var stored = new IdempotencyRequest(
            Guid.NewGuid(), customer.Id, IdempotencyOperation.CreateOrder, key,
            storedHash,
            now.AddMinutes(-5), now.AddHours(23), now.AddHours(71));
        await SeedIdempotencyRequestAsync(factory, stored);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        var correlationId = Guid.NewGuid().ToString("D");
        using var request = CreateRequest(key, [new(variant.Id, 2)]);
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(request);

        await AssertProblemDetailsAsync(
            response,
            HttpStatusCode.Conflict,
            expectedType: "https://oims.example/problems/idempotency-key-reused",
            expectedTitle: "Request conflict",
            expectedCode: "IDEMPOTENCY_KEY_REUSED",
            expectedMessage: "The idempotency key was already used for a different request.",
            expectedInstance: "/api/orders",
            expectedCorrelationId: correlationId,
            forbiddenValues:
            [
                key.ToString("D"),
                Convert.ToHexString(storedHash),
                Convert.ToBase64String(storedHash)
            ]);
        await AssertNoCreateEffectAsync(factory, customer.Id, variant.Id, expectedIdempotencyRows: 1);
        await AssertStoredIdempotencyAsync(
            factory,
            stored.Id,
            IdempotencyRequestStatus.Processing,
            storedHash);
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-003")]
    public async Task CreateOrder_ConcurrentSameUserKeyAndPayload_CreatesAtMostOneOrder()
    {
        var hook = new ControllableOperationHook(OrderOperationCheckpoints.BeforeIdempotencyClaim, expectedParticipants: 2);
        await using var factory = CreateFactory(hook);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await AuthenticateAsync(firstClient, customer.Email);
        await AuthenticateAsync(secondClient, customer.Email);
        using var firstRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var secondRequest = CreateRequest(key, [new(variant.Id, 1)]);

        var firstTask = firstClient.SendAsync(firstRequest);
        var secondTask = secondClient.SendAsync(secondRequest);
        try
        {
            await hook.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            hook.Release();
        }

        var responses = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(15));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(await ReadOrderIdAsync(firstResponse), await ReadOrderIdAsync(secondResponse));
        Assert.Equal(1, responses.Count(response => response.Headers.Contains("Idempotency-Replayed")));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        Assert.Equal(1, await db.IdempotencyRequests.AsNoTracking().CountAsync(request =>
            request.UserId == customer.Id && request.IdempotencyKey == key));
        Assert.Equal(1, await db.Orders.AsNoTracking().CountAsync(order => order.UserId == customer.Id));
        Assert.Equal(1, await db.OrderItems.AsNoTracking().CountAsync(item => item.ProductVariantId == variant.Id));
        Assert.Equal(1, await db.InventoryTransactions.AsNoTracking().CountAsync(entry =>
            entry.ProductVariantId == variant.Id && entry.Type == InventoryTransactionType.Reserve));
        var inventory = await db.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == variant.Id);
        Assert.Equal(1, inventory.ReservedQuantity);
    }

    [Fact]
    [Trait("Requirement", "API-IDEM-005")]
    public async Task CreateOrder_ResponseLostAfterCommit_RetryResolvesCommittedOrder()
    {
        var hook = new ThrowOnceCheckpointHook(OrderOperationCheckpoints.AfterCreateCommit);
        await using var factory = CreateFactory(hook);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var firstRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, firstResponse.StatusCode);

        using var committedScope = factory.Services.CreateScope();
        var committedDb = committedScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var committedOrderId = await committedDb.Orders.AsNoTracking()
            .Where(order => order.UserId == customer.Id)
            .Select(order => order.Id)
            .SingleAsync();

        using var retryRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var retryResponse = await client.SendAsync(retryRequest);

        Assert.Equal(HttpStatusCode.Created, retryResponse.StatusCode);
        Assert.Equal(committedOrderId, await ReadOrderIdAsync(retryResponse));
        Assert.Equal("true", Assert.Single(retryResponse.Headers.GetValues("Idempotency-Replayed")));
        await AssertOneCreateEffectAsync(factory, customer.Id, variant.Id, committedOrderId, 1);
    }

    [Fact]
    [Trait("Requirement", "DB-TXN-001")]
    public async Task CreateOrder_FailureAfterReservation_RollsBackClaimAndAllowsRetry()
    {
        var hook = new ThrowOnceCheckpointHook(OrderOperationCheckpoints.AfterInventoryReservation);
        await using var factory = CreateFactory(hook);
        var customer = await CreateUserAsync(factory);
        var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
        var key = Guid.NewGuid();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var firstRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.InternalServerError, firstResponse.StatusCode);
        await AssertNoCreateEffectAsync(factory, customer.Id, variant.Id);

        using var retryRequest = CreateRequest(key, [new(variant.Id, 1)]);
        using var retryResponse = await client.SendAsync(retryRequest);

        Assert.Equal(HttpStatusCode.Created, retryResponse.StatusCode);
        await AssertOneCreateEffectAsync(
            factory,
            customer.Id,
            variant.Id,
            await ReadOrderIdAsync(retryResponse),
            1);
    }

    [Fact]
    [Trait("Requirement", "NFR-007")]
    public async Task CreateOrder_RequestLogsDoNotExposeIdempotencyOrSensitivePayloadData()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"oims-idempotency-logs-{Guid.NewGuid():N}");
        var logPath = Path.Combine(logDirectory, "idempotency-.log");
        Directory.CreateDirectory(logDirectory);

        try
        {
            var key = Guid.NewGuid();
            var correlationId = Guid.NewGuid().ToString("D");
            string accessToken;
            string responseSnapshot;
            byte[] requestHash;

            await using (var factory = CreateFactory(logPath: logPath))
            {
                var customer = await CreateUserAsync(factory);
                var variant = await SeedVariantWithInventoryAsync(factory, 10, 10m);
                CreateOrderItemRequest[] items = [new(variant.Id, 1)];
                requestHash = CreateOrderRequestHasher.Hash(items);
                using var client = factory.CreateClient();
                await AuthenticateAsync(client, customer.Email);
                accessToken = client.DefaultRequestHeaders.Authorization!.Parameter!;

                using var originalRequest = CreateRequest(key, items);
                originalRequest.Headers.Add("X-Correlation-ID", correlationId);
                using var originalResponse = await client.SendAsync(originalRequest);
                originalResponse.EnsureSuccessStatusCode();
                responseSnapshot = await originalResponse.Content.ReadAsStringAsync();

                using var replayRequest = CreateRequest(key, items);
                replayRequest.Headers.Add("X-Correlation-ID", correlationId);
                using var replayResponse = await client.SendAsync(replayRequest);
                Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);

                using var conflictRequest = CreateRequest(key, [new(variant.Id, 2)]);
                conflictRequest.Headers.Add("X-Correlation-ID", correlationId);
                using var conflictResponse = await client.SendAsync(conflictRequest);
                Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            }

            var logFiles = Directory.GetFiles(logDirectory, "idempotency-*.log");
            Assert.NotEmpty(logFiles);
            var logContents = string.Join(
                Environment.NewLine,
                await Task.WhenAll(logFiles.Select(ReadSharedFileAsync)));

            Assert.Contains(correlationId, logContents, StringComparison.Ordinal);
            Assert.DoesNotContain(key.ToString("D"), logContents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexString(requestHash), logContents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(requestHash), logContents, StringComparison.Ordinal);
            Assert.DoesNotContain(responseSnapshot, logContents, StringComparison.Ordinal);
            Assert.DoesNotContain(accessToken, logContents, StringComparison.Ordinal);
            Assert.DoesNotContain(TestCredentials.ValidPassword, logContents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(
        IOperationHook? operationHook = null,
        IClock? clock = null,
        string? logPath = null)
    {
        var configurationOverrides = new List<KeyValuePair<string, string?>>
        {
            new("Database:ConnectionString", postgres.ConnectionString)
        };
        if (logPath is not null)
        {
            configurationOverrides.Add(new("Serilog:FilePath", logPath));
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(configurationOverrides.ToArray()))
                .ConfigureServices(services =>
                {
                    if (operationHook is not null)
                    {
                        services.RemoveAll<IOperationHook>();
                        services.AddSingleton(operationHook);
                    }

                    if (clock is not null)
                    {
                        services.RemoveAll<IClock>();
                        services.AddSingleton(clock);
                    }
                }));
    }

    private static async Task<string> ReadSharedFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<TestUser> CreateUserAsync(WebApplicationFactory<Program> factory)
    {
        await MigrateDatabaseAsync(factory);
        var email = $"order-idempotency-{Guid.NewGuid():N}@example.com";
        var user = new TestUser(Guid.NewGuid(), email);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User(
            user.Id,
            email,
            email,
            hasher.Hash(TestCredentials.ValidPassword),
            UserRole.Customer,
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SeededVariant> SeedVariantWithInventoryAsync(
        WebApplicationFactory<Program> factory,
        int onHandQuantity,
        decimal currentPrice)
    {
        await MigrateDatabaseAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product(Guid.NewGuid(), "Idempotency API product", "Description", CatalogStatus.Active, now);
        var variant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"IDEM-{Guid.NewGuid():N}"[..16],
            "Idempotency API variant",
            currentPrice,
            CatalogStatus.Active,
            now);
        db.AddRange(product, variant, new Inventory(Guid.NewGuid(), variant.Id, onHandQuantity, now));
        await db.SaveChangesAsync();
        return new SeededVariant(variant.Id);
    }

    private static async Task SeedIdempotencyRequestAsync(
        WebApplicationFactory<Program> factory,
        IdempotencyRequest request)
    {
        await MigrateDatabaseAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        db.IdempotencyRequests.Add(request);
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage CreateRequest(
        Guid idempotencyKey,
        IReadOnlyList<CreateOrderItemRequest> items)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new
            {
                items = items.Select(item => new
                {
                    productVariantId = item.ProductVariantId,
                    quantity = item.Quantity
                }).ToArray()
            })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static async Task AssertNoCreateEffectAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        Guid productVariantId,
        int expectedIdempotencyRows = 0)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await db.Inventories.AsNoTracking()
            .SingleAsync(item => item.ProductVariantId == productVariantId);
        Assert.False(await db.Orders.AsNoTracking().AnyAsync(order => order.UserId == userId));
        Assert.False(await db.OrderItems.AsNoTracking().AnyAsync(item => item.ProductVariantId == productVariantId));
        Assert.False(await db.InventoryTransactions.AsNoTracking().AnyAsync(entry =>
            entry.ProductVariantId == productVariantId && entry.Type == InventoryTransactionType.Reserve));
        Assert.Equal(expectedIdempotencyRows, await db.IdempotencyRequests.AsNoTracking()
            .CountAsync(request => request.UserId == userId));
        Assert.Equal(0, inventory.ReservedQuantity);
    }

    private static async Task AssertOneCreateEffectAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        Guid productVariantId,
        Guid orderId,
        int reservedQuantity)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = await db.Orders.AsNoTracking().SingleAsync(candidate => candidate.UserId == userId);
        Assert.Equal(orderId, order.Id);
        Assert.Single(await db.OrderItems.AsNoTracking().Where(item => item.OrderId == orderId).ToListAsync());
        Assert.Single(await db.InventoryTransactions.AsNoTracking().Where(entry =>
            entry.ReferenceId == orderId && entry.Type == InventoryTransactionType.Reserve).ToListAsync());
        var inventory = await db.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);
        Assert.Equal(reservedQuantity, inventory.ReservedQuantity);
        Assert.Single(await db.IdempotencyRequests.AsNoTracking().Where(request =>
            request.UserId == userId && request.Operation == IdempotencyOperation.CreateOrder).ToListAsync());
    }

    private static async Task AssertStoredIdempotencyAsync(
        WebApplicationFactory<Program> factory,
        Guid requestId,
        IdempotencyRequestStatus expectedStatus,
        byte[] expectedHash)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var request = await db.IdempotencyRequests.AsNoTracking().SingleAsync(candidate => candidate.Id == requestId);
        Assert.Equal(expectedStatus, request.Status);
        Assert.Equal(expectedHash, request.RequestHash);
    }

    private static async Task<Guid> ReadOrderIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return ReadOrderId(body);
    }

    private static Guid ReadOrderId(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private static async Task MigrateDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = TestCredentials.ValidPassword });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

    private sealed record TestUser(Guid Id, string Email);

    private sealed record SeededVariant(Guid Id);

    private sealed class ThrowOnceCheckpointHook(string targetCheckpoint) : IOperationHook
    {
        private int hasThrown;

        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            if (string.Equals(checkpoint, targetCheckpoint, StringComparison.Ordinal) &&
                Interlocked.Exchange(ref hasThrown, 1) == 0)
            {
                throw new InvalidOperationException($"Injected failure at checkpoint '{targetCheckpoint}'.");
            }

            return Task.CompletedTask;
        }
    }

    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedType,
        string expectedTitle,
        string expectedCode,
        string expectedMessage,
        string expectedInstance,
        string expectedCorrelationId,
        IReadOnlyCollection<string>? forbiddenValues = null)
    {
        Assert.Equal(expectedStatus, response.StatusCode);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        Assert.False(string.IsNullOrWhiteSpace(body));

        using var document = JsonDocument.Parse(body);
        var problem = document.RootElement;

        Assert.Equal(JsonValueKind.Object, problem.ValueKind);

        // Standard ProblemDetails members.
        Assert.Equal(expectedType, problem.GetProperty("type").GetString());

        Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());

        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());

        Assert.Equal(expectedMessage, problem.GetProperty("detail").GetString());

        Assert.Equal(expectedInstance, problem.GetProperty("instance").GetString());

        // OIMS extensions.
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());

        Assert.Equal(expectedMessage, problem.GetProperty("message").GetString());

        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));

        Assert.Equal(expectedCorrelationId, problem.GetProperty("correlationId").GetString());

        // Error responses are not wrapped in ApiResponse<T>.
        Assert.False(problem.TryGetProperty("data", out _));
        Assert.False(problem.TryGetProperty("metadata", out _));

        // Conflict errors are not field-validation errors.
        Assert.False(problem.TryGetProperty("errors", out _));

        // An error response must never be marked as a replayed success.
        Assert.False(response.Headers.Contains("Idempotency-Replayed"));

        if (forbiddenValues is null)
        {
            return;
        }

        foreach (var forbiddenValue in forbiddenValues)
        {
            Assert.DoesNotContain(forbiddenValue, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

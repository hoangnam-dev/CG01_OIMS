using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderCommandApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "API-ORD-005")]
    public async Task CreateOrder_WhenClientCancelsAfterCommit_PreservesCommittedReservation()
    {
        using var requestCancellation = new CancellationTokenSource();
        var hook = new CancelRequestAfterCheckpointHook(
            OrderOperationCheckpoints.AfterCreateCommit,
            requestCancellation);
        await using var factory = CreateFactory(hook);
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 2, currentPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateRequest(variant.Id, quantity: 1);

        var requestTask = client.SendAsync(request, requestCancellation.Token);
        await hook.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await requestTask);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(order => order.UserId == customer.Id);
        var item = await dbContext.OrderItems.AsNoTracking().SingleAsync(item => item.OrderId == order.Id);
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(inventory => inventory.ProductVariantId == variant.Id);
        var reserveLedger = await dbContext.InventoryTransactions.AsNoTracking().SingleAsync(transaction =>
            transaction.ReferenceId == order.Id && transaction.Type == InventoryTransactionType.Reserve);

        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.Equal(variant.Id, item.ProductVariantId);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(2, inventory.OnHandQuantity);
        Assert.Equal(1, inventory.ReservedQuantity);
        Assert.Equal(0, reserveLedger.OnHandQuantityDelta);
        Assert.Equal(1, reserveLedger.ReservedQuantityDelta);
        Assert.False(await dbContext.InventoryTransactions.AnyAsync(transaction =>
            transaction.ReferenceId == order.Id && transaction.Type == InventoryTransactionType.Release));
    }

    [Fact]
    [Trait("Requirement", "API-ORD-008")]
    public async Task CreateOrder_WhenVariantPriceChangesLater_KeepsHistoricalPriceSnapshot()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var admin = await CreateUserAsync(factory, UserRole.Admin);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 3, currentPrice: 10m);
        using var customerClient = factory.CreateClient();
        await AuthenticateAsync(customerClient, customer.Email);
        using var createRequest = CreateRequest(variant.Id, quantity: 2);

        using var createResponse = await customerClient.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();
        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var orderId = createDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var adminClient = factory.CreateClient();
        await AuthenticateAsync(adminClient, admin.Email);
        using var updateResponse = await adminClient.PutAsJsonAsync(
            $"/api/product-variants/{variant.Id}",
            new { name = "Order command API variant", currentPrice = 15m });
        updateResponse.EnsureSuccessStatusCode();

        using var getResponse = await customerClient.GetAsync($"/api/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        using var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        var orderData = getDocument.RootElement.GetProperty("data");
        var item = Assert.Single(orderData.GetProperty("items").EnumerateArray());

        Assert.Equal(20m, orderData.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(10m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(20m, item.GetProperty("lineTotal").GetDecimal());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedVariant = await dbContext.ProductVariants.AsNoTracking().SingleAsync(candidate => candidate.Id == variant.Id);
        var persistedItem = await dbContext.OrderItems.AsNoTracking().SingleAsync(candidate => candidate.OrderId == orderId);
        Assert.Equal(15m, persistedVariant.CurrentPrice);
        Assert.Equal(10m, persistedItem.UnitPrice);
        Assert.Equal(20m, persistedItem.LineTotal);
    }

    [Fact]
    [Trait("Requirement", "API-ORD-011")]
    public async Task CancelOrder_AdminWithValidReason_CancelsReleasesAndWritesAuthenticatedHistory()
    {
        // Arrange
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var admin = await CreateUserAsync(factory, UserRole.Admin);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 2, currentPrice: 12.50m);
        var orderId = await SeedReservedPendingOrderAsync(factory, owner.Id, variant.Id, quantity: 1, unitPrice: 12.50m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{orderId}/cancel")
        {
            Content = JsonContent.Create(new
            {
                reasonCode = "FraudSuspected",
                reason = "  Risk review case FR-2026-0042  ",
                actorType = "Customer",
                actorUserId = owner.Id
            })
        };

        // Act
        using var response = await client.SendAsync(request);

        // Assert HTTP contract.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(orderId, document.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", document.RootElement.GetProperty("data").GetProperty("status").GetString());

        // Assert durable state using a fresh DbContext.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(order => order.Id == orderId);
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(inventory => inventory.ProductVariantId == variant.Id);
        var releaseLedger = await dbContext.InventoryTransactions.AsNoTracking().SingleAsync(transaction =>
            transaction.ReferenceId == orderId &&
            transaction.ProductVariantId == variant.Id &&
            transaction.Type == InventoryTransactionType.Release);
        var history = await dbContext.OrderStatusHistories.AsNoTracking().SingleAsync(history => history.OrderId == orderId);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(2, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(0, releaseLedger.OnHandQuantityDelta);
        Assert.Equal(-1, releaseLedger.ReservedQuantityDelta);
        Assert.Equal(OrderStatus.PendingPayment, history.FromStatus);
        Assert.Equal(OrderStatus.Cancelled, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.Admin, history.ActorType);
        Assert.Equal(admin.Id, history.ActorUserId);
        Assert.Equal(OrderCancellationReasonCode.FraudSuspected, history.ReasonCode);
        Assert.Equal("Risk review case FR-2026-0042", history.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-ORD-012")]
    public async Task CancelOrder_CustomerOwner_UsesServerOwnedReasonCodeAndAuthenticatedActor()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        var orderId = await SeedReservedPendingOrderAsync(factory, customer.Id, variant.Id, quantity: 1, unitPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateCancelRequest(orderId, new { reason = "  Changed my mind  " });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var history = await dbContext.OrderStatusHistories.AsNoTracking().SingleAsync(entry => entry.OrderId == orderId);
        Assert.Equal(OrderStatusHistoryActorType.Customer, history.ActorType);
        Assert.Equal(customer.Id, history.ActorUserId);
        Assert.Equal(OrderCancellationReasonCode.CustomerRequested, history.ReasonCode);
        Assert.Equal("Changed my mind", history.Reason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("FraudSuspected", null)]
    [InlineData(null, " ")]
    [Trait("Requirement", "API-ORD-010")]
    public async Task CancelOrder_AdminWithoutRequiredReason_ReturnsValidationWithoutMutation(string? reasonCode, string? reason)
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var admin = await CreateUserAsync(factory, UserRole.Admin);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        var orderId = await SeedReservedPendingOrderAsync(factory, owner.Id, variant.Id, quantity: 1, unitPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);
        using var request = CreateCancelRequest(orderId, new { reasonCode, reason });

        using var response = await client.SendAsync(request);

        await AssertCancelRejectedWithoutMutationAsync(
            factory, response, orderId, variant.Id, OrderStatus.PendingPayment, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    [Trait("Requirement", "API-ORD-015")]
    public async Task CancelOrder_CustomerSuppliedReasonCode_ReturnsValidationWithoutMutation()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        var orderId = await SeedReservedPendingOrderAsync(factory, customer.Id, variant.Id, quantity: 1, unitPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateCancelRequest(orderId, new { reasonCode = "FraudSuspected", reason = "Untrusted classification" });

        using var response = await client.SendAsync(request);

        await AssertCancelRejectedWithoutMutationAsync(
            factory, response, orderId, variant.Id, OrderStatus.PendingPayment, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task CancelOrder_CustomerDoesNotOwnOrder_ReturnsNotFoundWithoutMutation()
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var otherCustomer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        var orderId = await SeedReservedPendingOrderAsync(factory, owner.Id, variant.Id, quantity: 1, unitPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, otherCustomer.Email);
        using var request = CreateCancelRequest(orderId, new { reason = "Attempted IDOR" });

        using var response = await client.SendAsync(request);

        await AssertCancelRejectedWithoutMutationAsync(
            factory, response, orderId, variant.Id, OrderStatus.PendingPayment, HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
    }

    [Fact]
    public async Task CancelOrder_ConfirmedOrder_ReturnsConflictWithoutMutation()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        var orderId = await SeedReservedPendingOrderAsync(
            factory, customer.Id, variant.Id, quantity: 1, unitPrice: 10m, status: OrderStatus.Confirmed);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateCancelRequest(orderId, new { reason = "Too late" });

        using var response = await client.SendAsync(request);

        await AssertCancelRejectedWithoutMutationAsync(
            factory, response, orderId, variant.Id, OrderStatus.Confirmed, HttpStatusCode.Conflict, "ORDER_NOT_CANCELLABLE");
    }

    [Fact]
    public async Task CancelOrder_AdminWithOverlongReason_ReturnsValidationWithoutMutation()
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var admin = await CreateUserAsync(factory, UserRole.Admin);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        var orderId = await SeedReservedPendingOrderAsync(factory, owner.Id, variant.Id, quantity: 1, unitPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);
        using var request = CreateCancelRequest(orderId, new { reasonCode = "Other", reason = new string('x', 501) });

        using var response = await client.SendAsync(request);

        await AssertCancelRejectedWithoutMutationAsync(
            factory, response, orderId, variant.Id, OrderStatus.PendingPayment, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    [Trait("Requirement", "API-ORD-001")]
    [Trait("Requirement", "API-ORD-009")]
    public async Task CreateOrder_CustomerWithStock_CreatesOrderUsingAuthenticatedIdentityAndCatalogPrice()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 3, currentPrice: 12.50m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new
            {
                items = new[] { new { productVariantId = variant.Id, quantity = 2 } },
                userId = Guid.NewGuid(),
                unitPrice = 0.01m,
                lineTotal = 0.02m,
                totalAmount = 0.02m,
                status = "Cancelled"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var orderId = data.GetProperty("id").GetGuid();
        Assert.Equal($"/api/orders/{orderId}", response.Headers.Location!.OriginalString);
        Assert.Equal(customer.Id, data.GetProperty("userId").GetGuid());
        Assert.Equal("PendingPayment", data.GetProperty("status").GetString());
        Assert.Equal(25m, data.GetProperty("totalAmount").GetDecimal());
        var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal(variant.Id, item.GetProperty("productVariantId").GetGuid());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal(12.50m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(25m, item.GetProperty("lineTotal").GetDecimal());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedOrder = await dbContext.Orders.AsNoTracking().SingleAsync(order => order.Id == orderId);
        var persistedItem = await dbContext.OrderItems.AsNoTracking().SingleAsync(item => item.OrderId == orderId);
        var reserveLedger = await dbContext.InventoryTransactions.AsNoTracking().SingleAsync(transaction =>
            transaction.ReferenceId == orderId && transaction.Type == InventoryTransactionType.Reserve);
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == variant.Id);

        Assert.Equal(customer.Id, persistedOrder.UserId);
        Assert.Equal(OrderStatus.PendingPayment, persistedOrder.Status);
        Assert.Equal(25m, persistedOrder.TotalAmount);
        Assert.Equal(12.50m, persistedItem.UnitPrice);
        Assert.Equal(25m, persistedItem.LineTotal);
        Assert.Equal(2, reserveLedger.ReservedQuantityDelta);
        Assert.Equal(3, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Equal(1, inventory.AvailableQuantity);
    }

    [Fact]
    [Trait("Requirement", "API-ORD-002")]
    public async Task CreateOrder_InsufficientStock_ReturnsConflictAndDoesNotPersistAnyEffect()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateRequest(variant.Id, quantity: 2);

        using var response = await client.SendAsync(request);

        await AssertFailureAndNoCreateEffectsAsync(
            factory,
            response,
            customer.Id,
            variant.Id,
            HttpStatusCode.Conflict,
            "INSUFFICIENT_STOCK");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [Trait("Requirement", "API-ORD-001")]
    public async Task CreateOrder_MissingOrMalformedIdempotencyKey_ReturnsValidationProblemAndDoesNotPersistAnyEffect(string? idempotencyKey)
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateRequest(
            variant.Id,
            quantity: 1,
            idempotencyKey,
            includeIdempotencyKey: idempotencyKey is not null);

        using var response = await client.SendAsync(request);

        await AssertFailureAndNoCreateEffectsAsync(
            factory,
            response,
            customer.Id,
            variant.Id,
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");
    }

    [Fact]
    [Trait("Requirement", "API-ORD-001")]
    public async Task CreateOrder_MultipleIdempotencyKeys_ReturnsValidationProblemAndDoesNotPersistAnyEffect()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        using var request = CreateRequest(variant.Id, quantity: 1);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        await AssertFailureAndNoCreateEffectsAsync(
            factory,
            response,
            customer.Id,
            variant.Id,
            HttpStatusCode.BadRequest,
            "VALIDATION_FAILED");
    }

    [Fact]
    public async Task CreateOrder_AnonymousCaller_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory();
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        using var client = factory.CreateClient();
        using var request = CreateRequest(variant.Id, quantity: 1);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_AdminCaller_ReturnsForbiddenAndDoesNotPersistAnyEffect()
    {
        await using var factory = CreateFactory();
        var admin = await CreateUserAsync(factory, UserRole.Admin);
        var variant = await SeedVariantWithInventoryAsync(factory, onHandQuantity: 1, currentPrice: 10m);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);
        using var request = CreateRequest(variant.Id, quantity: 1);

        using var response = await client.SendAsync(request);

        await AssertFailureAndNoCreateEffectsAsync(
            factory,
            response,
            admin.Id,
            variant.Id,
            HttpStatusCode.Forbidden,
            "FORBIDDEN");
    }

    private WebApplicationFactory<Program> CreateFactory(IOperationHook? operationHook = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
                new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString)))
                .ConfigureServices(services =>
                {
                    if (operationHook is null)
                    {
                        return;
                    }

                    services.RemoveAll<IOperationHook>();
                    services.AddSingleton(operationHook);
                }));

    private static async Task<TestUser> CreateUserAsync(WebApplicationFactory<Program> factory, UserRole role)
    {
        await MigrateDatabaseAsync(factory);
        var email = $"order-command-{Guid.NewGuid():N}@example.com";
        var user = new TestUser(Guid.NewGuid(), email);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        dbContext.Users.Add(new User(user.Id, email, email, hasher.Hash(TestCredentials.ValidPassword), role, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<SeededVariant> SeedVariantWithInventoryAsync(
        WebApplicationFactory<Program> factory,
        int onHandQuantity,
        decimal currentPrice)
    {
        await MigrateDatabaseAsync(factory);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product(Guid.NewGuid(), "Order command API product", "Description", CatalogStatus.Active, now);
        var variant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"OCA-{Guid.NewGuid():N}"[..16],
            "Order command API variant",
            currentPrice,
            CatalogStatus.Active,
            now);
        dbContext.AddRange(product, variant, new Inventory(Guid.NewGuid(), variant.Id, onHandQuantity, now));
        await dbContext.SaveChangesAsync();
        return new SeededVariant(variant.Id);
    }

    private static HttpRequestMessage CreateRequest(
        Guid productVariantId,
        int quantity,
        string? idempotencyKey = null,
        bool includeIdempotencyKey = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { items = new[] { new { productVariantId, quantity } } })
        };
        if (includeIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        }

        return request;
    }

    private static HttpRequestMessage CreateCancelRequest(Guid orderId, object payload) =>
        new(HttpMethod.Post, $"/api/orders/{orderId}/cancel")
        {
            Content = JsonContent.Create(payload)
        };

    private static async Task<Guid> SeedReservedPendingOrderAsync(
        WebApplicationFactory<Program> factory,
        Guid ownerId,
        Guid productVariantId,
        int quantity,
        decimal unitPrice,
        OrderStatus status = OrderStatus.PendingPayment)
    {
        await MigrateDatabaseAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var orderId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var order = new Order(orderId, ownerId, quantity * unitPrice, now.AddMinutes(15), now);
            if (status == OrderStatus.Confirmed)
            {
                order.Confirm(now.AddTicks(1));
            }

            dbContext.AddRange(
                order,
                new OrderItem(Guid.NewGuid(), orderId, productVariantId, quantity, unitPrice),
                new InventoryTransaction(
                    Guid.NewGuid(),
                    productVariantId,
                    InventoryTransactionType.Reserve,
                    onHandQuantityDelta: 0,
                    reservedQuantityDelta: quantity,
                    InventoryReferenceType.Order,
                    orderId,
                    reason: null,
                    now));
            await dbContext.SaveChangesAsync();
        }

        using var reservationScope = factory.Services.CreateScope();
        var store = reservationScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
        await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);
        var result = await store.TryReserveAsync(productVariantId, quantity, now, CancellationToken.None);
        Assert.Equal(InventoryReservationResult.Reserved, result);
        await transaction.CommitAsync(CancellationToken.None);

        return orderId;
    }

    private static async Task AssertCancelRejectedWithoutMutationAsync(
        WebApplicationFactory<Program> factory,
        HttpResponseMessage response,
        Guid orderId,
        Guid productVariantId,
        OrderStatus expectedOrderStatus,
        HttpStatusCode expectedHttpStatus,
        string expectedErrorCode)
    {
        Assert.Equal(expectedHttpStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedErrorCode, document.RootElement.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(order => order.Id == orderId);
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(inventory => inventory.ProductVariantId == productVariantId);
        var releaseCount = await dbContext.InventoryTransactions.AsNoTracking().CountAsync(transaction =>
            transaction.ReferenceId == orderId && transaction.Type == InventoryTransactionType.Release);
        var historyCount = await dbContext.OrderStatusHistories.AsNoTracking().CountAsync(history => history.OrderId == orderId);

        Assert.Equal(expectedOrderStatus, order.Status);
        Assert.Equal(1, inventory.ReservedQuantity);
        Assert.Equal(0, releaseCount);
        Assert.Equal(0, historyCount);
    }

    private static async Task AssertFailureAndNoCreateEffectsAsync(
        WebApplicationFactory<Program> factory,
        HttpResponseMessage response,
        Guid userId,
        Guid productVariantId,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(item => item.ProductVariantId == productVariantId);
        Assert.False(await dbContext.Orders.AsNoTracking().AnyAsync(order => order.UserId == userId));
        Assert.False(await dbContext.OrderItems.AsNoTracking().AnyAsync(item => item.ProductVariantId == productVariantId));
        Assert.False(await dbContext.InventoryTransactions.AsNoTracking().AnyAsync(transaction =>
            transaction.ProductVariantId == productVariantId && transaction.Type == InventoryTransactionType.Reserve));
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Equal(inventory.OnHandQuantity, inventory.AvailableQuantity);
    }

    private static async Task MigrateDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestCredentials.ValidPassword });
        login.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

    private sealed record TestUser(Guid Id, string Email);

    private sealed class CancelRequestAfterCheckpointHook(
        string targetCheckpoint,
        CancellationTokenSource requestCancellation) : IOperationHook
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => reached.Task;

        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken)
        {
            if (!string.Equals(checkpoint, targetCheckpoint, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            reached.TrySetResult();
            requestCancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed record SeededVariant(Guid Id);
}

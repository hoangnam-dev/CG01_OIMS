using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderReadApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "API-AUTHZ-003")]
    public async Task OrderReadEndpoints_AnonymousCaller_IsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/api/orders");
        using var detailResponse = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, detailResponse.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-AUTHZ-003")]
    public async Task GetOrder_CustomerOwner_ReceivesOnlyOwnOrder()
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var otherOwner = await CreateUserAsync(factory, UserRole.Customer);
        var order = await SeedOrderAsync(factory, owner.Id);
        await SeedOrderAsync(factory, otherOwner.Id);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, owner.Email);

        using var listResponse = await client.GetAsync("/api/orders?page=1&pageSize=20&status=PendingPayment&sortDirection=desc");
        using var detailResponse = await client.GetAsync($"/api/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listItems = listDocument.RootElement.GetProperty("data");
        Assert.Equal(1, listItems.GetArrayLength());
        Assert.Equal(order.Id, listItems[0].GetProperty("id").GetGuid());
        using var detailDocument = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal(order.Id, detailDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        Assert.Equal(owner.Id, detailDocument.RootElement.GetProperty("data").GetProperty("userId").GetGuid());
    }

    [Fact]
    [Trait("Requirement", "API-AUTHZ-004")]
    public async Task GetOrder_CustomerNonOwner_ReceivesNotFoundWithoutOrderData()
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var viewer = await CreateUserAsync(factory, UserRole.Customer);
        var order = await SeedOrderAsync(factory, owner.Id);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, viewer.Email);

        using var response = await client.GetAsync($"/api/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ORDER_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
        Assert.False(document.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    [Trait("Requirement", "API-AUTHZ-003")]
    public async Task GetOrder_Admin_ReceivesAnyExistingOrder()
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var admin = await CreateUserAsync(factory, UserRole.Admin);
        var order = await SeedOrderAsync(factory, owner.Id);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);

        using var response = await client.GetAsync($"/api/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(owner.Id, document.RootElement.GetProperty("data").GetProperty("userId").GetGuid());
    }

    [Theory]
    [InlineData("not-a-uuid", HttpStatusCode.BadRequest, "VALIDATION_FAILED")]
    [InlineData("00000000-0000-0000-0000-000000000000", HttpStatusCode.BadRequest, "VALIDATION_FAILED")]
    [InlineData("00000000-0000-0000-0000-000000000001", HttpStatusCode.NotFound, "ORDER_NOT_FOUND")]
    public async Task GetOrder_InvalidOrMissingId_ReturnsExpectedProblem(string id, HttpStatusCode expectedStatus, string expectedCode)
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var response = await client.GetAsync($"/api/orders/{id}");

        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListOrders_InvalidPaginationStatusOrSort_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory();
        var customer = await CreateUserAsync(factory, UserRole.Customer);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        using var response = await client.GetAsync("/api/orders?page=0&pageSize=101&status=Nope&sortDirection=sideways");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
        var errors = document.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("page", out _));
        Assert.True(errors.TryGetProperty("pageSize", out _));
        Assert.True(errors.TryGetProperty("status", out _));
        Assert.True(errors.TryGetProperty("sortDirection", out _));
    }

    [Fact]
    [Trait("Requirement", "API-ORD-003")]
    [Trait("Requirement", "API-ORD-008")]
    public async Task GetOrder_ReadDoesNotMutateInventoryAndUsesHistoricalPriceSnapshot()
    {
        await using var factory = CreateFactory();
        var owner = await CreateUserAsync(factory, UserRole.Customer);
        var order = await SeedOrderAsync(factory, owner.Id, includeInventory: true);
        await UpdateVariantPriceAsync(factory, order.VariantId, 99m);
        var before = await ReadInventoryStateAsync(factory, order.VariantId);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, owner.Email);

        using var response = await client.GetAsync($"/api/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = document.RootElement.GetProperty("data").GetProperty("items")[0];
        Assert.Equal(10m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(20m, item.GetProperty("lineTotal").GetDecimal());
        var after = await ReadInventoryStateAsync(factory, order.VariantId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task OpenApi_ExposesOrderReadCreateCancelAndStatusHistoryOperations()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/orders", out var ordersPath));
        Assert.True(ordersPath.TryGetProperty("get", out _));
        Assert.True(ordersPath.TryGetProperty("post", out var createOperation));

        Assert.True(paths.TryGetProperty("/api/orders/{id}", out var detailPath));
        Assert.True(detailPath.TryGetProperty("get", out _));
        Assert.False(detailPath.TryGetProperty("post", out _));

        Assert.True(paths.TryGetProperty("/api/orders/{id}/cancel", out var cancelPath));
        Assert.True(cancelPath.TryGetProperty("post", out var cancelOperation));
        var idempotencyKeyParameter = createOperation
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(p =>
                p.GetProperty("name").GetString() == "Idempotency-Key" &&
                p.GetProperty("in").GetString() == "header"
            );

        Assert.True(idempotencyKeyParameter.GetProperty("required").GetBoolean());
        var idempotencyDescription = idempotencyKeyParameter.GetProperty("description").GetString();
        Assert.Contains("immutable CreateOrder intent", idempotencyDescription, StringComparison.Ordinal);
        Assert.Contains("replay", idempotencyDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("409", idempotencyDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("Sprint", idempotencyDescription, StringComparison.OrdinalIgnoreCase);

        var schema = idempotencyKeyParameter.GetProperty("schema");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("uuid", schema.GetProperty("format").GetString());

        var responses = createOperation.GetProperty("responses");
        foreach (var expectedStatus in new[] { "201", "400", "401", "403", "404", "409", "500" })
        {
            Assert.True(
                responses.TryGetProperty(expectedStatus, out _),
                $"Create Order OpenAPI contract must document HTTP {expectedStatus}.");
        }

        var replayHeader = responses
            .GetProperty("201")
            .GetProperty("headers")
            .GetProperty("Idempotency-Replayed");
        Assert.Contains(
            "true",
            replayHeader.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "boolean",
            replayHeader.GetProperty("schema").GetProperty("type").GetString());

        var conflictDescription = responses.GetProperty("409").GetProperty("description").GetString();
        Assert.Contains("IDEMPOTENCY_KEY_REUSED", conflictDescription, StringComparison.Ordinal);
        Assert.Contains("IDEMPOTENCY_KEY_EXPIRED", conflictDescription, StringComparison.Ordinal);
        Assert.Contains("IDEMPOTENCY_REQUEST_PROCESSING", conflictDescription, StringComparison.Ordinal);

        var cancelRequestBody = cancelOperation.GetProperty("requestBody");
        Assert.True(cancelRequestBody.GetProperty("required").GetBoolean());

        var cancelContent = cancelRequestBody
            .GetProperty("content")
            .GetProperty("application/json");

        Assert.True(cancelContent.TryGetProperty("schema", out _));

        var cancelResponses = cancelOperation.GetProperty("responses");
        foreach (var expectedStatus in new[] { "200", "400", "401", "403", "404", "409", "500" })
        {
            Assert.True(
                cancelResponses.TryGetProperty(expectedStatus, out _),
                $"Cancel Order OpenAPI contract must document HTTP {expectedStatus}.");
        }

        Assert.True(paths.TryGetProperty("/api/orders/{id}/status-history", out var historyPath));
        Assert.True(historyPath.TryGetProperty("get", out var historyOperation));
        Assert.False(historyPath.TryGetProperty("post", out _));

        var historyParameters = historyOperation
            .GetProperty("parameters")
            .EnumerateArray()
            .ToArray();

        Assert.Contains(historyParameters, parameter =>
            parameter.GetProperty("name").GetString() == "id" &&
            parameter.GetProperty("in").GetString() == "path");

        Assert.Contains(historyParameters, parameter =>
            parameter.GetProperty("name").GetString() == "page" &&
            parameter.GetProperty("in").GetString() == "query");

        Assert.Contains(historyParameters, parameter =>
            parameter.GetProperty("name").GetString() == "pageSize" &&
            parameter.GetProperty("in").GetString() == "query");

        var historyResponses = historyOperation.GetProperty("responses");
        foreach (var expectedStatus in new[] { "200", "400", "401", "403", "404" })
        {
            Assert.True(
                historyResponses.TryGetProperty(expectedStatus, out _),
                $"Order status history OpenAPI contract must document HTTP {expectedStatus}.");
        }
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
                new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));

    private static async Task<TestUser> CreateUserAsync(WebApplicationFactory<Program> factory, UserRole role)
    {
        await MigrateDatabaseAsync(factory);
        var email = $"orders-{Guid.NewGuid():N}@example.com";
        var user = new TestUser(Guid.NewGuid(), email);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        dbContext.Users.Add(new User(user.Id, email, email, hasher.Hash(TestCredentials.ValidPassword), role, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static async Task<SeededOrder> SeedOrderAsync(WebApplicationFactory<Program> factory, Guid ownerId, bool includeInventory = false)
    {
        await MigrateDatabaseAsync(factory);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product(Guid.NewGuid(), "Order API product", "Description", CatalogStatus.Active, now);
        var variant = new ProductVariant(Guid.NewGuid(), product.Id, $"ORD-{Guid.NewGuid():N}"[..16], "Order API variant", 10m, CatalogStatus.Active, now);
        var order = new Order(Guid.NewGuid(), ownerId, 20m, now.AddMinutes(15), now);
        dbContext.Products.Add(product);
        dbContext.ProductVariants.Add(variant);
        dbContext.Orders.Add(order);
        dbContext.OrderItems.Add(new OrderItem(Guid.NewGuid(), order.Id, variant.Id, 2, 10m));
        if (includeInventory)
        {
            dbContext.Inventories.Add(new Inventory(Guid.NewGuid(), variant.Id, 7, now));
            dbContext.InventoryTransactions.Add(new InventoryTransaction(
                Guid.NewGuid(), variant.Id, InventoryTransactionType.Adjustment, 7, 0, null, null, "Test stock", now));
        }

        await dbContext.SaveChangesAsync();
        return new(order.Id, variant.Id);
    }

    private static async Task UpdateVariantPriceAsync(WebApplicationFactory<Program> factory, Guid variantId, decimal price)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var variant = await dbContext.ProductVariants.SingleAsync(candidate => candidate.Id == variantId);
        variant.Update(variant.Name, price, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<InventoryState> ReadInventoryStateAsync(WebApplicationFactory<Program> factory, Guid variantId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var inventory = await dbContext.Inventories.AsNoTracking().SingleAsync(candidate => candidate.ProductVariantId == variantId);
        var transactionCount = await dbContext.InventoryTransactions.CountAsync(candidate => candidate.ProductVariantId == variantId);
        return new(inventory.OnHandQuantity, inventory.ReservedQuantity, transactionCount);
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

    private sealed record SeededOrder(Guid Id, Guid VariantId);

    private sealed record InventoryState(int OnHandQuantity, int ReservedQuantity, int TransactionCount);
}

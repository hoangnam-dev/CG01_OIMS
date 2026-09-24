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
public sealed class OrderCommandApiTests(PostgreSqlFixture postgres)
{
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

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
                new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));

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

    private sealed record SeededVariant(Guid Id);
}

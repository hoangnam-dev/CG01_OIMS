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
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class InventoryApiTests(PostgreSqlFixture postgres)
{
    [Theory]
    [InlineData("/api/inventory/00000000-0000-0000-0000-000000000001", "GET")]
    [InlineData("/api/inventory/00000000-0000-0000-0000-000000000001/adjust", "POST")]
    [InlineData("/api/inventory/00000000-0000-0000-0000-000000000001/transactions", "GET")]
    public async Task InventoryEndpoints_Anonymous_ReturnUnauthorized(string path, string method)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { quantityChange = 1, reason = "count" }) : null
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/inventory/00000000-0000-0000-0000-000000000001", "GET")]
    [InlineData("/api/inventory/00000000-0000-0000-0000-000000000001/adjust", "POST")]
    [InlineData("/api/inventory/00000000-0000-0000-0000-000000000001/transactions", "GET")]
    public async Task InventoryEndpoints_Customer_ReturnForbidden(string path, string method)
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsCustomer(client);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { quantityChange = 1, reason = "count" }) : null
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InventoryEndpoints_Admin_ExposeCurrentStateAdjustmentAndPagedHistory()
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, 10);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);

        using var current = await client.GetAsync($"/api/inventory/{productVariantId}");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using (var currentDocument = JsonDocument.Parse(await current.Content.ReadAsStringAsync()))
        {
            Assert.Equal(10, currentDocument.RootElement.GetProperty("data").GetProperty("onHandQuantity").GetInt32());
            Assert.Equal(10, currentDocument.RootElement.GetProperty("data").GetProperty("availableQuantity").GetInt32());
        }

        using var adjustment = await client.PostAsJsonAsync($"/api/inventory/{productVariantId}/adjust", new
        {
            quantityChange = -2,
            reason = "  stock count  "
        });
        Assert.Equal(HttpStatusCode.OK, adjustment.StatusCode);

        using var history = await client.GetAsync($"/api/inventory/{productVariantId}/transactions?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyDocument = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(1, historyDocument.RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal("Adjustment", historyDocument.RootElement.GetProperty("data")[0].GetProperty("type").GetString());
        Assert.Equal("stock count", historyDocument.RootElement.GetProperty("data")[0].GetProperty("reason").GetString());
        Assert.Equal(1, historyDocument.RootElement.GetProperty("metadata").GetProperty("pagination").GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData(0, "reason")]
    [InlineData(1, " ")]
    public async Task AdjustInventory_InvalidRequest_ReturnsValidationProblem(int quantityChange, string reason)
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, 10);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);

        using var response = await client.PostAsJsonAsync($"/api/inventory/{productVariantId}/adjust", new { quantityChange, reason });

        await AssertValidationFailure(response);
    }

    [Fact]
    public async Task AdjustInventory_OverlongReason_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, 10);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);

        using var response = await client.PostAsJsonAsync($"/api/inventory/{productVariantId}/adjust", new
        {
            quantityChange = 1,
            reason = new string('x', 257)
        });

        await AssertValidationFailure(response);
    }

    [Fact]
    public async Task InventoryEndpoints_InvalidIdAndMissingInventory_MapToStableProblems()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);

        using var invalid = await client.GetAsync("/api/inventory/not-a-uuid");
        await AssertValidationFailure(invalid);

        using var missing = await client.GetAsync($"/api/inventory/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var document = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("INVENTORY_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AdjustInventory_QuantityBelowZero_ReturnsInvariantConflict()
    {
        await using var factory = CreateFactory();
        var productVariantId = await SeedInventoryAsync(factory, 10);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);

        using var response = await client.PostAsJsonAsync($"/api/inventory/{productVariantId}/adjust", new
        {
            quantityChange = -11,
            reason = "count"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVENTORY_INVARIANT_VIOLATION", document.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertValidationFailure(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
    }

    private static async Task<Guid> SeedInventoryAsync(WebApplicationFactory<Program> factory, int onHand)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var product = new Product(Guid.NewGuid(), $"API inventory {Guid.NewGuid():N}", "Description", CatalogStatus.Active, now);
        var variant = new ProductVariant(Guid.NewGuid(), product.Id, UniqueSku(), "Variant", 10m, CatalogStatus.Active, now);
        dbContext.AddRange(product, variant, new Inventory(Guid.NewGuid(), variant.Id, onHand, now));
        await dbContext.SaveChangesAsync();
        return variant.Id;
    }

    private static async Task MigrateDatabase(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
            new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));

    private static async Task AuthenticateAsCustomer(HttpClient client)
    {
        var email = $"inventory-customer-{Guid.NewGuid():N}@example.com";
        using var register = await client.PostAsJsonAsync("/api/auth/register", new { email, password = TestCredentials.ValidPassword });
        register.EnsureSuccessStatusCode();
        await LoginAndSetBearer(client, email, TestCredentials.ValidPassword);
    }

    private static async Task AuthenticateAsAdmin(WebApplicationFactory<Program> factory, HttpClient client)
    {
        var email = $"inventory-admin-{Guid.NewGuid():N}@example.com";
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            dbContext.Users.Add(new User(Guid.NewGuid(), email, email, hasher.Hash(TestCredentials.ValidPassword), UserRole.Admin, DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync();
        }

        await LoginAndSetBearer(client, email, TestCredentials.ValidPassword);
    }

    private static async Task LoginAndSetBearer(HttpClient client, string email, string password)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

    private static string UniqueSku() => $"API-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
}

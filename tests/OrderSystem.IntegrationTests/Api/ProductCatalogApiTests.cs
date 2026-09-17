using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ProductCatalogApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "API-PROD-001")]
    [Trait("Requirement", "API-CONTRACT-006")]
    public async Task ListProducts_Anonymous_ReturnsOnlyActiveRowsWithPaginationEnvelope()
    {
        await using var factory = CreateFactory();
        var (_, _, marker) = await SeedVisibilityProducts(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/products?page=1&pageSize=20&search={marker}&sortBy=name&sortDirection=asc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Active", data[0].GetProperty("status").GetString());
        var pagination = document.RootElement.GetProperty("metadata").GetProperty("pagination");
        Assert.Equal(1, pagination.GetProperty("page").GetInt32());
        Assert.Equal(20, pagination.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, pagination.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, pagination.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    [Trait("Requirement", "API-PROD-002")]
    public async Task ProductDetail_InactiveProduct_IsHiddenFromPublicButVisibleToAdminWhenRequested()
    {
        await using var anonymousFactory = CreateFactory();
        var (_, inactiveId, _) = await SeedVisibilityProducts(anonymousFactory);
        using var anonymousClient = anonymousFactory.CreateClient();

        using var hidden = await anonymousClient.GetAsync($"/api/products/{inactiveId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        await using var adminFactory = CreateFactory("Admin");
        using var adminClient = adminFactory.CreateClient();
        using var visible = await adminClient.GetAsync($"/api/products/{inactiveId}?includeInactive=true");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-004")]
    public async Task ListProducts_UnsupportedSort_ReturnsValidationProblemDetails()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/products?sortBy=price");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("sortBy", out _));
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-001")]
    public async Task ProductDetail_MalformedUuid_ReturnsValidationProblemDetails()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/products/not-a-uuid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("id", out _));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Customer", HttpStatusCode.Forbidden)]
    public async Task CreateProduct_NonAdmin_IsDeniedWithoutDatabaseMutation(string? role, HttpStatusCode expected)
    {
        await using var factory = CreateFactory(role);
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var name = $"Denied-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync("/api/products", new
        {
            name,
            description = "Must not persist"
        });

        Assert.Equal(expected, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        Assert.False(await dbContext.Products.AsNoTracking().AnyAsync(product => product.Name == name));
    }

    [Fact]
    [Trait("Requirement", "API-PROD-003")]
    [Trait("Requirement", "API-VAR-001")]
    [Trait("Requirement", "API-CONTRACT-007")]
    [Trait("Requirement", "DB-CONSTRAINT-012")]
    public async Task AdminCatalogLifecycle_PersistsChangesMapsDuplicateSkuAndDeactivatesWithoutDeletingRows()
    {
        await using var factory = CreateFactory("Admin");
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N", null)[..6];
        var sku = $"API-{suffix}";

        using var createProduct = await client.PostAsJsonAsync("/api/products", new
        {
            name = $"API Product {suffix}",
            description = "Initial description"
        });
        Assert.Equal(HttpStatusCode.Created, createProduct.StatusCode);
        using var productDocument = JsonDocument.Parse(await createProduct.Content.ReadAsStringAsync());
        var productId = productDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        Assert.Equal($"/api/products/{productId}", createProduct.Headers.Location?.OriginalString);

        using var updateProduct = await client.PutAsJsonAsync($"/api/products/{productId}", new
        {
            name = $"Updated API Product {suffix}",
            description = "Updated description"
        });
        Assert.Equal(HttpStatusCode.OK, updateProduct.StatusCode);

        using var createVariant = await client.PostAsJsonAsync($"/api/products/{productId}/variants", new
        {
            sku = sku.ToLowerInvariant(),
            name = "API Variant",
            currentPrice = 25.50m
        });
        Assert.Equal(HttpStatusCode.Created, createVariant.StatusCode);
        using var variantDocument = JsonDocument.Parse(await createVariant.Content.ReadAsStringAsync());
        var variantId = variantDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var duplicateVariant = await client.PostAsJsonAsync($"/api/products/{productId}/variants", new
        {
            sku = $" {sku} ",
            name = "Duplicate",
            currentPrice = 1m
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicateVariant.StatusCode);
        using var conflictDocument = JsonDocument.Parse(await duplicateVariant.Content.ReadAsStringAsync());
        Assert.Equal("SKU_ALREADY_EXISTS", conflictDocument.RootElement.GetProperty("code").GetString());

        using var updateVariant = await client.PutAsJsonAsync($"/api/product-variants/{variantId}", new
        {
            name = "Updated API Variant",
            currentPrice = 30m
        });
        Assert.Equal(HttpStatusCode.OK, updateVariant.StatusCode);

        using var deactivateVariant = await client.DeleteAsync($"/api/product-variants/{variantId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateVariant.StatusCode);
        Assert.Empty(await deactivateVariant.Content.ReadAsByteArrayAsync());

        using var deactivateProduct = await client.DeleteAsync($"/api/products/{productId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateProduct.StatusCode);
        Assert.Empty(await deactivateProduct.Content.ReadAsByteArrayAsync());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persistedProduct = await dbContext.Products.AsNoTracking().SingleAsync(product => product.Id == productId);
        var persistedVariant = await dbContext.ProductVariants.AsNoTracking().SingleAsync(variant => variant.Id == variantId);
        Assert.Equal(CatalogStatus.Inactive, persistedProduct.Status);
        Assert.Equal(CatalogStatus.Inactive, persistedVariant.Status);
        Assert.Equal(productId, persistedVariant.ProductId);
    }

    [Fact]
    public async Task OpenApi_ContainsCatalogRoutesAndSchemas()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/products", out _));
        Assert.True(paths.TryGetProperty("/api/products/{id}", out _));
        Assert.True(paths.TryGetProperty("/api/products/{productId}/variants", out _));
        Assert.True(paths.TryGetProperty("/api/product-variants/{id}", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("ProductDto", out _));
        Assert.True(schemas.TryGetProperty("ProductDetailDto", out _));
        Assert.True(schemas.TryGetProperty("ProductVariantDto", out _));
    }

    private static async Task<(Guid ActiveId, Guid InactiveId, string Marker)> SeedVisibilityProducts(WebApplicationFactory<Program> factory)
    {
        await MigrateDatabase(factory);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var now = DateTimeOffset.UtcNow;
        var marker = Guid.NewGuid().ToString("N", null)[..6];
        var active = new Product(Guid.NewGuid(), $"API-Visibility-{marker}", "Description", CatalogStatus.Active, now);
        var inactive = new Product(Guid.NewGuid(), $"API-Visibility-{marker}", "Description", CatalogStatus.Inactive, now);
        dbContext.Products.AddRange(active, inactive);
        await dbContext.SaveChangesAsync();
        return (active.Id, inactive.Id, marker);
    }

    private static async Task MigrateDatabase(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private WebApplicationFactory<Program> CreateFactory(string? role = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = postgres.ConnectionString
                }));
            if (role is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new TestUserStartupFilter(role)));
            }
        });

    private sealed class TestUserStartupFilter(string role) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, role)
                    ],
                    "Test"));
                await continuation();
            });
            next(app);
        };
    }
}

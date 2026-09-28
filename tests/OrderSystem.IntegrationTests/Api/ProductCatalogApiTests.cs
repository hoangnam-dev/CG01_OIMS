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
    public async Task ListProducts_AdminWithoutInactiveStatus_ReturnsOnlyActiveRows()
    {
        await using var factory = CreateFactory();
        var (_, _, marker) = await SeedVisibilityProducts(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);

        using var response = await client.GetAsync(
            $"/api/products?page=1&pageSize=20&search={marker}&sortBy=name&sortDirection=asc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var product = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("Active", product.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized, "UNAUTHORIZED")]
    [InlineData(true, HttpStatusCode.Forbidden, "FORBIDDEN")]
    [Trait("Requirement", "API-PROD-002")]
    public async Task ListProducts_InactiveStatusRequiresAdmin(
        bool authenticateCustomer,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var factory = CreateFactory();
        var (_, _, marker) = await SeedVisibilityProducts(factory);
        using var client = factory.CreateClient();
        if (authenticateCustomer)
        {
            await AuthenticateAsCustomer(client);
        }

        using var response = await client.GetAsync(
            $"/api/products?page=1&pageSize=20&search={marker}&status=Inactive");

        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
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

        await AuthenticateAsAdmin(anonymousFactory, anonymousClient);
        using var visible = await anonymousClient.GetAsync($"/api/products/{inactiveId}?includeInactive=true");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized, "UNAUTHORIZED")]
    [InlineData(true, HttpStatusCode.Forbidden, "FORBIDDEN")]
    [Trait("Requirement", "API-PROD-002")]
    public async Task ProductDetail_IncludeInactiveRequiresAdmin(
        bool authenticateCustomer,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var factory = CreateFactory();
        var (_, inactiveId, _) = await SeedVisibilityProducts(factory);
        using var client = factory.CreateClient();
        if (authenticateCustomer)
        {
            await AuthenticateAsCustomer(client);
        }

        using var response = await client.GetAsync($"/api/products/{inactiveId}?includeInactive=true");

        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
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
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        if (role == "Customer")
        {
            await AuthenticateAsCustomer(client);
        }
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

    [Theory]
    [InlineData("update-product", false, HttpStatusCode.Unauthorized)]
    [InlineData("update-product", true, HttpStatusCode.Forbidden)]
    [InlineData("deactivate-product", false, HttpStatusCode.Unauthorized)]
    [InlineData("deactivate-product", true, HttpStatusCode.Forbidden)]
    [InlineData("create-variant", false, HttpStatusCode.Unauthorized)]
    [InlineData("create-variant", true, HttpStatusCode.Forbidden)]
    [InlineData("update-variant", false, HttpStatusCode.Unauthorized)]
    [InlineData("update-variant", true, HttpStatusCode.Forbidden)]
    [InlineData("deactivate-variant", false, HttpStatusCode.Unauthorized)]
    [InlineData("deactivate-variant", true, HttpStatusCode.Forbidden)]
    [Trait("Requirement", "API-AUTHZ-001")]
    public async Task CatalogMutation_NonAdminIsDeniedWithoutDatabaseMutation(
        string operation,
        bool authenticateCustomer,
        HttpStatusCode expectedStatus)
    {
        await using var factory = CreateFactory();
        var baseline = await SeedProtectedCatalog(factory);
        using var client = factory.CreateClient();
        if (authenticateCustomer)
        {
            await AuthenticateAsCustomer(client);
        }
        using var request = CreateDeniedCatalogMutationRequest(operation, baseline);

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var product = await dbContext.Products.AsNoTracking().SingleAsync(item => item.Id == baseline.ProductId);
        var variant = await dbContext.ProductVariants.AsNoTracking().SingleAsync(item => item.Id == baseline.VariantId);
        Assert.Equal(baseline.ProductName, product.Name);
        Assert.Equal(baseline.ProductStatus, product.Status);
        Assert.Equal(baseline.ProductUpdatedAt, product.UpdatedAt);
        Assert.Equal(baseline.VariantName, variant.Name);
        Assert.Equal(baseline.VariantPrice, variant.CurrentPrice);
        Assert.Equal(baseline.VariantStatus, variant.Status);
        Assert.Equal(baseline.VariantUpdatedAt, variant.UpdatedAt);
        Assert.Equal(
            baseline.VariantCount,
            await dbContext.ProductVariants.AsNoTracking().CountAsync(item => item.ProductId == baseline.ProductId));
        Assert.Equal(
            baseline.InventoryCount,
            await dbContext.Inventories.AsNoTracking().CountAsync(item => item.ProductVariantId == baseline.VariantId));
    }

    [Fact]
    [Trait("Requirement", "API-PROD-003")]
    [Trait("Requirement", "API-VAR-001")]
    [Trait("Requirement", "API-CONTRACT-007")]
    [Trait("Requirement", "DB-CONSTRAINT-012")]
    public async Task AdminCatalogLifecycle_PersistsChangesMapsDuplicateSkuAndDeactivatesWithoutDeletingRows()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsAdmin(factory, client);
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

    private static async Task<CatalogBaseline> SeedProtectedCatalog(WebApplicationFactory<Program> factory)
    {
        await MigrateDatabase(factory);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product(
            Guid.NewGuid(),
            $"Protected Product {Guid.NewGuid():N}",
            "Original description",
            CatalogStatus.Active,
            now);
        var variant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"SEC-{Guid.NewGuid():N}"[..16],
            "Protected Variant",
            19.95m,
            CatalogStatus.Active,
            now);
        var inventory = new Inventory(Guid.NewGuid(), variant.Id, 7, now);
        dbContext.AddRange(product, variant, inventory);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var persistedProduct = await dbContext.Products.AsNoTracking().SingleAsync(item => item.Id == product.Id);
        var persistedVariant = await dbContext.ProductVariants.AsNoTracking().SingleAsync(item => item.Id == variant.Id);
        return new(
            persistedProduct.Id,
            persistedProduct.Name,
            persistedProduct.Status,
            persistedProduct.UpdatedAt,
            persistedVariant.Id,
            persistedVariant.Name,
            persistedVariant.CurrentPrice,
            persistedVariant.Status,
            persistedVariant.UpdatedAt,
            VariantCount: 1,
            InventoryCount: 1);
    }

    private static HttpRequestMessage CreateDeniedCatalogMutationRequest(
        string operation,
        CatalogBaseline baseline) =>
        operation switch
        {
            "update-product" => new(HttpMethod.Put, $"/api/products/{baseline.ProductId}")
            {
                Content = JsonContent.Create(new { name = "Changed Product", description = "Changed description" })
            },
            "deactivate-product" => new(HttpMethod.Delete, $"/api/products/{baseline.ProductId}"),
            "create-variant" => new(HttpMethod.Post, $"/api/products/{baseline.ProductId}/variants")
            {
                Content = JsonContent.Create(new
                {
                    sku = $"NEW-{Guid.NewGuid():N}"[..16],
                    name = "Unauthorized Variant",
                    currentPrice = 1m
                })
            },
            "update-variant" => new(HttpMethod.Put, $"/api/product-variants/{baseline.VariantId}")
            {
                Content = JsonContent.Create(new { name = "Changed Variant", currentPrice = 1m })
            },
            "deactivate-variant" => new(HttpMethod.Delete, $"/api/product-variants/{baseline.VariantId}"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported catalog operation.")
        };

    private static async Task MigrateDatabase(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString)));
        });

    private static async Task AuthenticateAsCustomer(HttpClient client)
    {
        var email = $"catalog-customer-{Guid.NewGuid():N}@example.com";
        using var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = TestCredentials.ValidPassword
        });
        register.EnsureSuccessStatusCode();
        await LoginAndSetBearer(client, email, TestCredentials.ValidPassword);
    }

    private static async Task AuthenticateAsAdmin(
        WebApplicationFactory<Program> factory,
        HttpClient client)
    {
        var email = $"catalog-admin-{Guid.NewGuid():N}@example.com";
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            dbContext.Users.Add(new User(
                Guid.NewGuid(),
                email,
                email,
                hasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                DateTimeOffset.UtcNow));
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
            "Bearer",
            document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

    private sealed record CatalogBaseline(
        Guid ProductId,
        string ProductName,
        CatalogStatus ProductStatus,
        DateTimeOffset ProductUpdatedAt,
        Guid VariantId,
        string VariantName,
        decimal VariantPrice,
        CatalogStatus VariantStatus,
        DateTimeOffset VariantUpdatedAt,
        int VariantCount,
        int InventoryCount);
}

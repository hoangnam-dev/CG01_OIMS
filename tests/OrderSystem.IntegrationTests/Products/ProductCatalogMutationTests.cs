using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Products;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ProductCatalogMutationTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 17, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductMutations_ValidRequests_PersistLifecycleChanges()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();

        var created = await service.CreateProductAsync(
            new("Sprint product", "Initial description"),
            CancellationToken.None);
        Assert.True(created.IsSuccess);
        Assert.NotNull(created.Value);
        Assert.Equal("Active", created.Value.Status);
        Assert.Equal(FixedNow, created.Value.CreatedAt);

        var updated = await service.UpdateProductAsync(
            created.Value.Id,
            new("Updated product", "Updated description"),
            CancellationToken.None);
        Assert.True(updated.IsSuccess);
        Assert.Equal("Updated product", updated.Value!.Name);

        var deactivated = await service.ChangeProductStatusAsync(
            created.Value.Id,
            CatalogStatus.Inactive,
            CancellationToken.None);
        Assert.True(deactivated.IsSuccess);
        Assert.Equal("Inactive", deactivated.Value!.Status);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Products.AsNoTracking().SingleAsync(
            product => product.Id == created.Value.Id,
            CancellationToken.None);
        Assert.Equal("Updated product", persisted.Name);
        Assert.Equal(CatalogStatus.Inactive, persisted.Status);
    }

    [Fact]
    public async Task VariantMutations_ValidRequests_CanonicalizeAndPersistLifecycleChanges()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
        var product = await service.CreateProductAsync(
            new("Variant parent", "Description"),
            CancellationToken.None);

        var created = await service.CreateVariantAsync(
            product.Value!.Id,
            new("  sku-test-01  ", "Original variant", 10.25m),
            CancellationToken.None);
        Assert.True(created.IsSuccess);
        Assert.Equal("SKU-TEST-01", created.Value!.Sku);
        Assert.Equal("Active", created.Value.Status);

        var updated = await service.UpdateVariantAsync(
            created.Value.Id,
            new("Updated variant", 12.50m),
            CancellationToken.None);
        Assert.True(updated.IsSuccess);
        Assert.Equal("SKU-TEST-01", updated.Value!.Sku);
        Assert.Equal(12.50m, updated.Value.CurrentPrice);

        var deactivated = await service.ChangeVariantStatusAsync(
            created.Value.Id,
            CatalogStatus.Inactive,
            CancellationToken.None);
        Assert.True(deactivated.IsSuccess);
        Assert.Equal("Inactive", deactivated.Value!.Status);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.ProductVariants.AsNoTracking().SingleAsync(
            variant => variant.Id == created.Value.Id,
            CancellationToken.None);
        Assert.Equal("SKU-TEST-01", persisted.Sku);
        Assert.Equal("Updated variant", persisted.Name);
        Assert.Equal(CatalogStatus.Inactive, persisted.Status);
    }

    [Fact]
    public async Task CreateVariant_DuplicateCanonicalSku_ReturnsStableConflict()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
        var firstProduct = await service.CreateProductAsync(new("First parent", "Description"), CancellationToken.None);
        var secondProduct = await service.CreateProductAsync(new("Second parent", "Description"), CancellationToken.None);
        var suffix = Guid.NewGuid().ToString("N", null)[..6];
        var sku = $"SKU-{suffix}";

        var first = await service.CreateVariantAsync(
            firstProduct.Value!.Id,
            new(sku.ToLowerInvariant(), "First", 1m),
            CancellationToken.None);
        var duplicate = await service.CreateVariantAsync(
            secondProduct.Value!.Id,
            new($" {sku} ", "Duplicate", 1m),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("SKU_ALREADY_EXISTS", duplicate.Error!.Code);
    }

    [Fact]
    public async Task Mutations_MissingResources_ReturnStableNotFoundErrors()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var service = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();

        var missingProduct = await service.UpdateProductAsync(
            Guid.NewGuid(),
            new("Missing", "Description"),
            CancellationToken.None);
        var missingParent = await service.CreateVariantAsync(
            Guid.NewGuid(),
            new("MISS-001", "Missing", 1m),
            CancellationToken.None);
        var missingVariant = await service.UpdateVariantAsync(
            Guid.NewGuid(),
            new("Missing", 1m),
            CancellationToken.None);

        Assert.Equal("PRODUCT_NOT_FOUND", missingProduct.Error!.Code);
        Assert.Equal("PRODUCT_NOT_FOUND", missingParent.Error!.Code);
        Assert.Equal("PRODUCT_VARIANT_NOT_FOUND", missingVariant.Error!.Code);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = postgres.ConnectionString
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FakeClock(FixedNow));
            });
        });
}

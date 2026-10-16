using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.Infrastructure.Products.Caching;
using OrderSystem.IntegrationTests.Infrastructure;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace OrderSystem.IntegrationTests.Products.Caching;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ProductCatalogCacheFlowTests(PostgreSqlFixture postgres, RedisFixture redis) : IClassFixture<RedisFixture>
{
    [Fact]
    [Trait("Requirement", "CACHE-002")]
    public async Task CatalogUpdates_CommitThenInvalidateAffectedProductCacheEntry()
    {
        var keyPrefix = $"test-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(redis.ConnectionString, keyPrefix, TimeSpan.FromMinutes(5));
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
        var product = new Product(Guid.NewGuid(), "Invalidation Product", "Original description", CatalogStatus.Active, now);
        var variant = new OrderSystem.Domain.Products.ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"INV-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            "Original Variant",
            25m,
            CatalogStatus.Active,
            now);
        dbContext.AddRange(product, variant);
        await dbContext.SaveChangesAsync();

        var service = provider.GetRequiredService<ProductCatalogService>();
        var keyBuilder = provider.GetRequiredService<ProductCacheKeyBuilder>();
        var redisDatabase = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(product.Id);

        var initialRead = await service.GetProductDetailAsync(product.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);
        Assert.True(initialRead.IsSuccess);
        Assert.True(await redisDatabase.KeyExistsAsync(key));

        var productUpdate = await service.UpdateProductAsync(
            product.Id,
            new UpdateProductRequest("Updated Product", "Updated description"),
            CancellationToken.None);

        Assert.True(productUpdate.IsSuccess);
        Assert.Equal("Updated Product", productUpdate.Value!.Name);
        Assert.False(await redisDatabase.KeyExistsAsync(key));

        var repopulatedRead = await service.GetProductDetailAsync(product.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);
        Assert.True(repopulatedRead.IsSuccess);
        Assert.Equal("Updated Product", repopulatedRead.Value!.Name);
        Assert.True(await redisDatabase.KeyExistsAsync(key));

        var variantUpdate = await service.UpdateVariantAsync(
            variant.Id,
            new UpdateProductVariantRequest("Updated Variant", 30m),
            CancellationToken.None);

        Assert.True(variantUpdate.IsSuccess);
        Assert.Equal("Updated Variant", variantUpdate.Value!.Name);
        Assert.False(await redisDatabase.KeyExistsAsync(key));
    }

    [Fact]
    [Trait("Requirement", "CACHE-004")]
    public async Task CreateVariant_DuplicateSkuDatabaseSaveFails_RetainsExistingProductCacheEntry()
    {
        var keyPrefix = $"test-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(redis.ConnectionString, keyPrefix, TimeSpan.FromMinutes(5));
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        var product = new Product(Guid.NewGuid(), "Failed Save Product", "Cache must survive a rejected save", CatalogStatus.Active, now);
        var duplicateSku = $"DUP-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        var existingVariant = new OrderSystem.Domain.Products.ProductVariant(
            Guid.NewGuid(),
            product.Id,
            duplicateSku,
            "Existing Variant",
            40m,
            CatalogStatus.Active,
            now);
        dbContext.AddRange(product, existingVariant);
        await dbContext.SaveChangesAsync();

        var service = provider.GetRequiredService<ProductCatalogService>();
        var keyBuilder = provider.GetRequiredService<ProductCacheKeyBuilder>();
        var redisDatabase = provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(product.Id);

        var initialRead = await service.GetProductDetailAsync(product.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);
        Assert.True(initialRead.IsSuccess);
        var cachedValueBeforeFailure = await redisDatabase.StringGetAsync(key);
        Assert.False(cachedValueBeforeFailure.IsNull);

        var result = await service.CreateVariantAsync(
            product.Id,
            new CreateProductVariantRequest(duplicateSku, "Rejected Variant", 45m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("SKU_ALREADY_EXISTS", result.Error!.Code);
        var cachedValueAfterFailure = await redisDatabase.StringGetAsync(key);
        Assert.False(cachedValueAfterFailure.IsNull);
        Assert.Equal(cachedValueBeforeFailure, cachedValueAfterFailure);
    }

    [Fact]
    [Trait("Requirement", "CACHE-001")]
    public async Task GetProductDetail_CacheMiss_ReadsPostgreSqlAndPopulatesNamespacedRedisEntryWithTtl()
    {
        var keyPrefix = $"test-{Guid.NewGuid():N}";
        var productTtl = TimeSpan.FromMinutes(5);
        await using var factory = CreateFactory(redis.ConnectionString, keyPrefix, productTtl);
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
        var product = new Product(Guid.NewGuid(), "Cache Miss Product", "Loaded from PostgreSQL", CatalogStatus.Active, now);
        var variant = new OrderSystem.Domain.Products.ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"CACHE-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            "Cache Miss Variant",
            19.95m,
            CatalogStatus.Active,
            now);
        dbContext.AddRange(product, variant);
        await dbContext.SaveChangesAsync();

        var keyBuilder = provider.GetRequiredService<ProductCacheKeyBuilder>();
        var connection = provider.GetRequiredService<IConnectionMultiplexer>();
        var redisDatabase = connection.GetDatabase();
        var key = keyBuilder.BuildProductDetailKey(product.Id);
        await redisDatabase.KeyDeleteAsync(key);

        var service = provider.GetRequiredService<ProductCatalogService>();
        var result = await service.GetProductDetailAsync(product.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(product.Id, result.Value!.Id);
        Assert.Equal(product.Name, result.Value.Name);
        Assert.Equal(variant.Id, Assert.Single(result.Value.Variants).Id);

        var cachedValue = await redisDatabase.StringGetAsync(key);
        Assert.False(cachedValue.IsNull);
        var remainingTtl = await redisDatabase.KeyTimeToLiveAsync(key);
        Assert.NotNull(remainingTtl);
        Assert.True(remainingTtl.Value > TimeSpan.Zero);
        Assert.True(remainingTtl.Value <= productTtl);

        var cachedResult = await provider.GetRequiredService<IProductDetailCache>()
            .ReadAsync(product.Id, CancellationToken.None);
        Assert.Equal(ProductCacheReadStatus.Hit, cachedResult.Status);
        Assert.Equal(product.Id, cachedResult.Product!.Id);
        Assert.Equal(variant.Id, Assert.Single(cachedResult.Product.Variants).Id);
    }

    [Fact]
    [Trait("Requirement", "CACHE-003")]
    public async Task GetProductDetail_RedisDisconnects_ReturnsProductFromHealthyPostgreSql()
    {
        await using var unavailableRedis = new RedisBuilder("redis:8-alpine").Build();
        await unavailableRedis.StartAsync();

        var keyPrefix = $"test-{Guid.NewGuid():N}";
        await using var factory = CreateFactory(
            unavailableRedis.GetConnectionString(),
            keyPrefix,
            TimeSpan.FromMinutes(5));
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero);
        var product = new Product(Guid.NewGuid(), "Redis Outage Product", "PostgreSQL remains authoritative", CatalogStatus.Active, now);
        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync();

        var connection = provider.GetRequiredService<IConnectionMultiplexer>();
        await connection.GetDatabase().PingAsync();
        await unavailableRedis.StopAsync();

        var service = provider.GetRequiredService<ProductCatalogService>();
        var result = await service.GetProductDetailAsync(product.Id, CatalogVisibility.ActiveOnly, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(product.Id, result.Value!.Id);
        Assert.Equal(product.Name, result.Value.Name);
        Assert.Empty(result.Value.Variants);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string redisConnectionString,
        string keyPrefix,
        TimeSpan productTtl) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString),
                    new KeyValuePair<string, string?>("Redis:Configuration", redisConnectionString),
                    new KeyValuePair<string, string?>("Redis:KeyPrefix", keyPrefix),
                    new KeyValuePair<string, string?>("Redis:ProductTtl", productTtl.ToString("c")),
                    new KeyValuePair<string, string?>("Redis:OperationTimeout", "00:00:00.200"),
                    new KeyValuePair<string, string?>("Redis:ConnectTimeout", "00:00:01"))));
}

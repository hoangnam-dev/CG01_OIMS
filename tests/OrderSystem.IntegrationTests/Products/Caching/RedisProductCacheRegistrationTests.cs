using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Products;
using OrderSystem.Application.Products.Contracts;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Products.Caching;
using OrderSystem.IntegrationTests.Infrastructure;
using StackExchange.Redis;

namespace OrderSystem.IntegrationTests.Products.Caching;

[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisProductCacheRegistrationTests(RedisFixture redisFixture)
{
    [Fact]
    public async Task AddOrderSystemProductInfrastructure_RegistersSharedWorkingRedisCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=oims_registration_test",
                ["Redis:Configuration"] = redisFixture.ConnectionString,
                ["Redis:KeyPrefix"] = $"test-{Guid.NewGuid():N}",
                ["Redis:ProductTtl"] = "00:05:00",
                ["Redis:OperationTimeout"] = "00:00:00.500",
                ["Redis:ConnectTimeout"] = "00:00:01",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddOrderSystemPersistence(configuration)
            .AddOrderSystemCommonInfrastructure()
            .AddOrderSystemProductInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        var firstCache = provider.GetRequiredService<IProductDetailCache>();
        var secondCache = provider.GetRequiredService<IProductDetailCache>();

        Assert.IsType<RedisProductDetailCache>(firstCache);
        Assert.Same(firstCache, secondCache);

        var firstConnection = provider.GetRequiredService<IConnectionMultiplexer>();
        var secondConnection = provider.GetRequiredService<IConnectionMultiplexer>();

        Assert.Same(firstConnection, secondConnection);

        var result = await firstCache.ReadAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(ProductCacheReadStatus.Miss, result.Status);
        Assert.Null(result.Product);
    }
}

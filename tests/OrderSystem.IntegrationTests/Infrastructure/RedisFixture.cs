using Testcontainers.Redis;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container;

    public RedisFixture()
    {
        _container = new RedisBuilder("redis:8-alpine").Build();
    }

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class RedisCollectionDefinition : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
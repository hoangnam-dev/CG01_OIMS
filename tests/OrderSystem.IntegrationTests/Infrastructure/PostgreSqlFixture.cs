using Testcontainers.PostgreSql;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("oims_tests")
        .WithUsername("oims")
        .WithPassword("oims_tests_only")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollectionDefinition : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}

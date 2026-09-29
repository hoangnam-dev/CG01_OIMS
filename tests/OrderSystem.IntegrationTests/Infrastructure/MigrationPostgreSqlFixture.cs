using Testcontainers.PostgreSql;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class MigrationPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container;

    public MigrationPostgreSqlFixture()
    {
        container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("oims_migration_tests")
            .WithUsername("oims")
            .WithPassword(TestCredentials.CreatePassword())
            .Build();
    }

    public string ConnectionString => container.GetConnectionString();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class MigrationPostgreSqlCollectionDefinition
    : ICollectionFixture<MigrationPostgreSqlFixture>
{
    public const string Name = "PostgreSQL Migrations";
}

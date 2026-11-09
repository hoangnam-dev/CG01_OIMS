using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderSystem.Infrastructure;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Products.Caching;

[Collection(RedisCollectionDefinition.Name)]
public sealed class RedisHealthCheckTests(RedisFixture redis)
{
    [Fact]
    [Trait("Requirement", "S11-T04")]
    public async Task RedisHealthCheck_ReportsOutageAndRecovery()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=oims_redis_health_test",
                ["Redis:Configuration"] = redis.ConnectionString,
                ["Redis:KeyPrefix"] = "test",
                ["Redis:ProductTtl"] = "00:05:00",
                ["Redis:OperationTimeout"] = "00:00:00.500",
                ["Redis:ConnectTimeout"] = "00:00:01"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddOrderSystemPersistence(configuration)
            .AddOrderSystemCommonInfrastructure()
            .AddOrderSystemProductInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var checks = provider.GetRequiredService<HealthCheckService>();
        var paused = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await WaitForStatusAsync(checks, HealthStatus.Healthy, timeout.Token);
            await redis.PauseAsync();
            paused = true;
            await WaitForStatusAsync(checks, HealthStatus.Degraded, timeout.Token);
            await redis.UnpauseAsync();
            paused = false;
            await WaitForStatusAsync(checks, HealthStatus.Healthy, timeout.Token);
        }
        finally
        {
            if (paused)
            {
                await redis.UnpauseAsync();
            }
        }
    }

    private static async Task WaitForStatusAsync(
        HealthCheckService checks,
        HealthStatus expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await checks.CheckHealthAsync(registration => registration.Name == "redis", cancellationToken);
            if (report.Entries.TryGetValue("redis", out var entry) && entry.Status == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}

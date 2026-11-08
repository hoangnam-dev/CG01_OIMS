using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class RedisHealthCheckRegistrationTests
{
    [Fact]
    [Trait("Requirement", "S11-T04")]
    public void ProductInfrastructure_RegistersRedisAsOptionalDependency()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=oims_health_registration_test",
                ["Redis:Configuration"] = "localhost:6379",
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

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var redis = Assert.Single(registrations, registration => registration.Name == "redis");

        Assert.Equal(HealthStatus.Degraded, redis.FailureStatus);
        Assert.DoesNotContain("ready", redis.Tags);
    }
}

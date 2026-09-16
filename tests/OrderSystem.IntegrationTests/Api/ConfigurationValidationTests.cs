using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OrderSystem.IntegrationTests.Api;

public sealed class ConfigurationValidationTests
{
    [Fact]
    [Trait("Requirement", "NFR-009")]
    public async Task Startup_MissingDatabaseConnectionString_FailsClearly()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = string.Empty
                })));

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.CreateClient().GetAsync("/health/live"));

        Assert.Contains("Database", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionString", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Jwt:AccessTokenLifetime", "00:00:00", "AccessTokenLifetime")]
    [InlineData("Redis:ProductTtl", "00:00:00", "ProductTtl")]
    [InlineData("RabbitMq:Port", "0", "Port")]
    [InlineData("Reservation:Duration", "00:00:00", "Duration")]
    [InlineData("Retry:MaxAttempts", "0", "MaxAttempts")]
    [InlineData("Payment:ReconciliationInterval", "00:00:00", "ReconciliationInterval")]
    [Trait("Requirement", "NFR-009")]
    public async Task Startup_InvalidOperationalSetting_FailsClearly(string key, string value, string expectedName)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [key] = value
                })));

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.CreateClient().GetAsync("/health/live"));

        Assert.Contains(expectedName, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

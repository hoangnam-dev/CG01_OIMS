using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void JwtSigningKey_LessThanThirtyTwoDecodedBytes_IsRejected()
    {
        var options = new JwtOptions
        {
            SigningKey = Convert.ToBase64String(new byte[31])
        };

        var exception = Assert.Throws<InvalidOperationException>(options.GetSigningKeyBytes);

        Assert.Contains("32 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("not-base64", "Base64")]
    [InlineData("c2hvcnQ=", "32 bytes")]
    public async Task Startup_InvalidSigningMaterial_FailsClearly(string? signingKey, string expectedMessage)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Jwt:SigningKey", signingKey))));

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.CreateClient().GetAsync("/health/live"));

        Assert.Contains("Jwt:SigningKey", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessage, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "NFR-009")]
    public async Task Startup_MissingDatabaseConnectionString_FailsClearly()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Database:ConnectionString", string.Empty))));

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
    [InlineData("Authentication:RefreshPermitLimit", "0", "RefreshPermitLimit")]
    [InlineData("Authentication:RefreshWindow", "00:00:00", "RefreshWindow")]
    [InlineData("Authentication:RefreshTokenCleanupInterval", "00:00:00", "RefreshTokenCleanupInterval")]
    [InlineData("Authentication:RefreshTokenCleanupBatchSize", "0", "RefreshTokenCleanupBatchSize")]
    [InlineData("Authentication:ExpiredRefreshTokenRetention", "-00:00:01", "ExpiredRefreshTokenRetention")]
    [Trait("Requirement", "NFR-009")]
    public async Task Startup_InvalidOperationalSetting_FailsClearly(string key, string value, string expectedName)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(new KeyValuePair<string, string?>(key, value))));

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.CreateClient().GetAsync("/health/live"));

        Assert.Contains(expectedName, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

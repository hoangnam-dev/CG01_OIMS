using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

public sealed class HealthEndpointContractTests
{
    [Fact]
    [Trait("Requirement", "S11-T04")]
    public async Task HealthEndpoints_ReportDependencyOutageWithoutRemovingAvailableApiTraffic()
    {
        var postgres = new MutableHealthCheck();
        var redis = new MutableHealthCheck();
        var rabbitMq = new MutableHealthCheck();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration());
            builder.ConfigureTestServices(services => services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration("postgresql", _ => postgres, HealthStatus.Unhealthy, ["ready"]));
                options.Registrations.Add(new HealthCheckRegistration("redis", _ => redis, HealthStatus.Degraded, []));
                options.Registrations.Add(new HealthCheckRegistration("rabbitmq", _ => rabbitMq, HealthStatus.Unhealthy, []));
            }));
        });
        using var client = factory.CreateClient();

        rabbitMq.Status = HealthStatus.Unhealthy;
        using (var ready = await client.GetAsync("/health/ready"))
        using (var live = await client.GetAsync("/health/live"))
        using (var dependencies = await client.GetAsync("/health/dependencies"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, dependencies.StatusCode);
            await AssertDependencyStatusAsync(dependencies, "Unhealthy", "rabbitmq", "Unhealthy");
        }

        rabbitMq.Status = HealthStatus.Healthy;
        redis.Status = HealthStatus.Degraded;
        using (var ready = await client.GetAsync("/health/ready"))
        using (var dependencies = await client.GetAsync("/health/dependencies"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, dependencies.StatusCode);
            await AssertDependencyStatusAsync(dependencies, "Degraded", "redis", "Degraded");
        }

        redis.Status = HealthStatus.Healthy;
        postgres.Status = HealthStatus.Unhealthy;
        using (var ready = await client.GetAsync("/health/ready"))
        using (var live = await client.GetAsync("/health/live"))
        using (var dependencies = await client.GetAsync("/health/dependencies"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, dependencies.StatusCode);
            await AssertDependencyStatusAsync(dependencies, "Unhealthy", "postgresql", "Unhealthy");
        }

        postgres.Status = HealthStatus.Healthy;
        using (var ready = await client.GetAsync("/health/ready"))
        using (var dependencies = await client.GetAsync("/health/dependencies"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, dependencies.StatusCode);
            await AssertDependencyStatusAsync(dependencies, "Healthy", "rabbitmq", "Healthy");
        }
    }

    private static async Task AssertDependencyStatusAsync(
        HttpResponseMessage response,
        string aggregateStatus,
        string name,
        string status)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(aggregateStatus, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(status, document.RootElement.GetProperty("checks").GetProperty(name).GetString());
    }

    private sealed class MutableHealthCheck : IHealthCheck
    {
        public HealthStatus Status { get; set; } = HealthStatus.Healthy;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(Status));
    }
}

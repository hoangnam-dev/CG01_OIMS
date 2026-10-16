using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure;
using OrderSystem.IntegrationTests.Infrastructure;
using System.Globalization;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class MessagingHealthCheckTests(RabbitMqFixture rabbitMq)
{
    [Fact]
    [Trait("Requirement", "S9-T02")]
    public async Task RabbitMqHealthCheck_ReportsOutageAndRecovery_WithoutReadyTag()
    {
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = rabbitMq.Host,
            ["RabbitMq:Port"] = rabbitMq.Port.ToString(CultureInfo.InvariantCulture),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = rabbitMq.Username,
            ["RabbitMq:Password"] = rabbitMq.Password
        };

        var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddOrderSystemMessagingInfrastructure(context.Configuration);
            })
            .Build();

        var brokerStopped = false;
        try
        {
            await host.StartAsync();
            var healthChecks = host.Services.GetService<HealthCheckService>();
            Assert.NotNull(healthChecks);

            var registrations = host.Services
                .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
                .Value
                .Registrations;
            var registration = Assert.Single(registrations, candidate => candidate.Name == "rabbitmq");
            Assert.DoesNotContain("ready", registration.Tags);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await WaitForStatusAsync(healthChecks, HealthStatus.Healthy, timeout.Token);

            await rabbitMq.StopBrokerAsync();
            brokerStopped = true;
            await WaitForStatusAsync(healthChecks, HealthStatus.Unhealthy, timeout.Token);

            await rabbitMq.StartBrokerAsync();
            brokerStopped = false;
            await WaitForStatusAsync(healthChecks, HealthStatus.Healthy, timeout.Token);
        }
        finally
        {
            if (brokerStopped)
            {
                await rabbitMq.StartBrokerAsync();
            }

            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task WaitForStatusAsync(
        HealthCheckService healthChecks,
        HealthStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await healthChecks.CheckHealthAsync(
                registration => registration.Name == "rabbitmq",
                cancellationToken);

            if (report.Entries.TryGetValue("rabbitmq", out var entry) && entry.Status == expectedStatus)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}

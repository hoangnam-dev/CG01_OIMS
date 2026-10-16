using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.IntegrationTests.Infrastructure;
using RabbitMQ.Client;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class MessagingConnectionLifecycleTests(RabbitMqFixture rabbitMq)
{
    [Fact]
    [Trait("Requirement", "S9-T02")]
    public async Task ChannelFactory_CreatesIndependentOpenChannels_OnSharedConnection()
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

        IConnection? connection = null;
        try
        {
            await host.StartAsync();
            var connectionProvider = host.Services.GetRequiredService<IRabbitMqConnectionProvider>();
            var channelFactory = host.Services.GetRequiredService<IRabbitMqChannelFactory>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            connection = await connectionProvider.GetConnectionAsync(timeout.Token);
            var firstChannel = await channelFactory.CreateChannelAsync(timeout.Token);
            var secondChannel = await channelFactory.CreateChannelAsync(timeout.Token);

            Assert.True(connection.IsOpen);
            Assert.True(firstChannel.IsOpen);
            Assert.True(secondChannel.IsOpen);
            Assert.NotSame(firstChannel, secondChannel);

            await firstChannel.DisposeAsync();
            await secondChannel.DisposeAsync();

            Assert.False(firstChannel.IsOpen);
            Assert.False(secondChannel.IsOpen);
            Assert.True(connection.IsOpen);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }

        Assert.NotNull(connection);
        Assert.False(connection.IsOpen);
    }

    [Fact]
    [Trait("Requirement", "S9-T02")]
    public async Task ConnectionProvider_ConcurrentCallersShareConnection_AndHostDisposalClosesIt()
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

        IConnection? connection = null;
        try
        {
            await host.StartAsync();
            var provider = host.Services.GetRequiredService<IRabbitMqConnectionProvider>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var connections = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => provider.GetConnectionAsync(timeout.Token)));

            connection = connections[0];
            Assert.True(connection.IsOpen);
            Assert.All(connections, candidate => Assert.Same(connection, candidate));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }

        Assert.NotNull(connection);
        Assert.False(connection.IsOpen);
    }
}

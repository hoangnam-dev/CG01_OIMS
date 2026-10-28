using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.IntegrationTests.Infrastructure;
using RabbitMQ.Client;
using System.Globalization;
using System.Text.Json;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class MessagingTopologyTests(RabbitMqFixture rabbitMq)
{
    [Fact]
    [Trait("Requirement", "S10-T06")]
    public async Task Broker_RetryPolicyUsesApprovedDelayedAtLeastOnceDeadLettering()
    {
        var policiesJson = await rabbitMq.ListPoliciesAsync();
        using var document = JsonDocument.Parse(policiesJson);
        var retryPolicy = Assert.Single(
            document.RootElement.EnumerateArray(),
            policy => policy.GetProperty("pattern").GetString() == "^oims[.]notification[.]order-created[.]v1[.]retry$");
        var definition = retryPolicy.GetProperty("definition");

        Assert.Equal(30_000, definition.GetProperty("message-ttl").GetInt32());
        Assert.Equal("oims.notification.ingress", definition.GetProperty("dead-letter-exchange").GetString());
        Assert.Equal("order-created.v1", definition.GetProperty("dead-letter-routing-key").GetString());
        Assert.Equal("at-least-once", definition.GetProperty("dead-letter-strategy").GetString());
        Assert.Equal("reject-publish", definition.GetProperty("overflow").GetString());
    }

    [Fact]
    [Trait("Requirement", "S10-T06")]
    public async Task InitializeAsync_DeclaresConsumerSpecificExchanges_Idempotently()
    {
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = rabbitMq.Host,
            ["RabbitMq:Port"] = rabbitMq.Port.ToString(CultureInfo.InvariantCulture),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = rabbitMq.Username,
            ["RabbitMq:Password"] = rabbitMq.Password
        };

        using var host = new HostBuilder()
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

        await host.StartAsync();
        try
        {
            var initializer = host.Services.GetRequiredService<IRabbitMqTopologyInitializer>();
            var channelFactory = host.Services.GetRequiredService<IRabbitMqChannelFactory>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await initializer.InitializeAsync(timeout.Token);
            await initializer.InitializeAsync(timeout.Token);

            await AssertExchangeAsync(channelFactory, "oims.notification.ingress", timeout.Token);
            await AssertExchangeAsync(channelFactory, "oims.notification.retry", timeout.Token);
            await AssertExchangeAsync(channelFactory, "oims.notification.dead-letter", timeout.Token);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S10-T06")]
    public async Task InitializeAsync_DeclaresQuorumQueuesAndConsumerSpecificBindings_Idempotently()
    {
        const string mainQueue = "oims.notification.order-created.v1";
        const string retryQueue = "oims.notification.order-created.v1.retry";
        const string deadLetterQueue = "oims.notification.order-created.v1.dlq";
        const string routingKey = "order-created.v1";
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = rabbitMq.Host,
            ["RabbitMq:Port"] = rabbitMq.Port.ToString(CultureInfo.InvariantCulture),
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = rabbitMq.Username,
            ["RabbitMq:Password"] = rabbitMq.Password
        };

        using var host = new HostBuilder()
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

        await host.StartAsync();
        try
        {
            var initializer = host.Services.GetRequiredService<IRabbitMqTopologyInitializer>();
            var channelFactory = host.Services.GetRequiredService<IRabbitMqChannelFactory>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await initializer.InitializeAsync(timeout.Token);
            await initializer.InitializeAsync(timeout.Token);

            await AssertQuorumQueueAsync(channelFactory, retryQueue, timeout.Token);
            await AssertQuorumQueueAsync(channelFactory, deadLetterQueue, timeout.Token);

            await using var channel = await channelFactory.CreateChannelAsync(timeout.Token);
            await channel.QueuePurgeAsync(mainQueue, timeout.Token);
            await channel.QueuePurgeAsync(retryQueue, timeout.Token);
            await channel.QueuePurgeAsync(deadLetterQueue, timeout.Token);
            ReadOnlyMemory<byte> body = "consumer-specific-route"u8.ToArray();

            await channel.BasicPublishAsync(
                "oims.notification.ingress",
                routingKey,
                mandatory: true,
                body,
                timeout.Token);
            await channel.BasicPublishAsync(
                "oims.notification.retry",
                routingKey,
                mandatory: true,
                body,
                timeout.Token);
            await channel.BasicPublishAsync(
                "oims.notification.dead-letter",
                routingKey,
                mandatory: true,
                body,
                timeout.Token);

            Assert.NotNull(await channel.BasicGetAsync(mainQueue, autoAck: true, timeout.Token));
            Assert.NotNull(await channel.BasicGetAsync(retryQueue, autoAck: true, timeout.Token));
            Assert.NotNull(await channel.BasicGetAsync(deadLetterQueue, autoAck: true, timeout.Token));

            await channel.BasicPublishAsync(
                "oims.notification.ingress",
                "order-created.v2",
                mandatory: false,
                body,
                timeout.Token);
            await channel.BasicPublishAsync(
                "oims.notification.retry",
                "order-created.v2",
                mandatory: false,
                body,
                timeout.Token);
            await channel.BasicPublishAsync(
                "oims.notification.dead-letter",
                "order-created.v2",
                mandatory: false,
                body,
                timeout.Token);

            Assert.Null(await channel.BasicGetAsync(mainQueue, autoAck: true, timeout.Token));
            Assert.Null(await channel.BasicGetAsync(retryQueue, autoAck: true, timeout.Token));
            Assert.Null(await channel.BasicGetAsync(deadLetterQueue, autoAck: true, timeout.Token));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S9-T02")]
    [Trait("TestId", "MSG-TOPOLOGY-001")]
    public async Task InitializeAsync_DeclaresDurableTopicQueueAndExactBinding_Idempotently()
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

        try
        {
            await host.StartAsync();
            var initializer = host.Services.GetService<IRabbitMqTopologyInitializer>();
            var channelFactory = host.Services.GetRequiredService<IRabbitMqChannelFactory>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            Assert.NotNull(initializer);
            await initializer.InitializeAsync(timeout.Token);
            await initializer.InitializeAsync(timeout.Token);

            await using var channel = await channelFactory.CreateChannelAsync(timeout.Token);
            await channel.ExchangeDeclareAsync(
                "oims.events",
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: timeout.Token);
            await channel.QueueDeclareAsync(
                "oims.notification.order-created.v1",
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: timeout.Token);
            await channel.QueuePurgeAsync("oims.notification.order-created.v1", timeout.Token);

            ReadOnlyMemory<byte> body = "topology-probe"u8.ToArray();
            await channel.BasicPublishAsync(
                "oims.events",
                "order.created.v1",
                mandatory: true,
                body,
                timeout.Token);

            var routed = await channel.BasicGetAsync(
                "oims.notification.order-created.v1",
                autoAck: true,
                timeout.Token);

            Assert.NotNull(routed);
            Assert.Equal(body.ToArray(), routed.Body.ToArray());

            await channel.BasicPublishAsync(
                "oims.events",
                "order.created.v2",
                mandatory: false,
                body,
                timeout.Token);

            var incorrectlyRouted = await channel.BasicGetAsync(
                "oims.notification.order-created.v1",
                autoAck: true,
                timeout.Token);

            Assert.Null(incorrectlyRouted);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task AssertExchangeAsync(
        IRabbitMqChannelFactory channelFactory,
        string exchangeName,
        CancellationToken cancellationToken)
    {
        await using var channel = await channelFactory.CreateChannelAsync(cancellationToken);
        var exception = await Record.ExceptionAsync(
            () => channel.ExchangeDeclarePassiveAsync(exchangeName, cancellationToken));

        Assert.Null(exception);
        await channel.ExchangeDeclareAsync(
            exchangeName,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
    }

    private static async Task AssertQuorumQueueAsync(
        IRabbitMqChannelFactory channelFactory,
        string queueName,
        CancellationToken cancellationToken)
    {
        await using var channel = await channelFactory.CreateChannelAsync(cancellationToken);
        var exception = await Record.ExceptionAsync(
            () => channel.QueueDeclarePassiveAsync(queueName, cancellationToken));

        Assert.Null(exception);
        await channel.QueueDeclareAsync(
            queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum"
            },
            cancellationToken: cancellationToken);
    }
}

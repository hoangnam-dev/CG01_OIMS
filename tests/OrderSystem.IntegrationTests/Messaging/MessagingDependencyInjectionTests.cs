using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Configuration;
using RabbitMQ.Client;

namespace OrderSystem.IntegrationTests.Messaging;

public sealed class MessagingDependencyInjectionTests
{
    [Fact]
    [Trait("Requirement", "S9-T02")]
    public async Task MessagingCapability_WithOnlyRabbitMqConfiguration_StartsWithoutRetryConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "oims"
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

        var options = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        Assert.Equal("localhost", options.Host);
        Assert.Equal(5672, options.Port);
        Assert.Equal("/", options.VirtualHost);
        Assert.Equal("oims", options.Username);

        await host.StopAsync();
    }

    [Fact]
    [Trait("Requirement", "S9-T02")]
    public async Task MessagingCapability_UsesApprovedOrderCreatedTopologyDefaults()
    {
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "oims"
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

        var options = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        Assert.Equal("oims.events", options.ExchangeName);
        Assert.Equal("topic", options.ExchangeType);
        Assert.Equal("order.created.v1", options.OrderCreatedRoutingKey);
        Assert.Equal("oims.notification.order-created.v1", options.OrderCreatedQueueName);
        Assert.Equal("OrderCreatedNotificationConsumer", options.OrderCreatedConsumerName);
        Assert.Equal("oims.notification.ingress", options.ConsumerIngressExchangeName);
        Assert.Equal("oims.notification.retry", options.RetryExchangeName);
        Assert.Equal("oims.notification.dead-letter", options.DeadLetterExchangeName);
        Assert.Equal("order-created.v1", options.OrderCreatedConsumerRoutingKey);
        Assert.Equal("oims.notification.order-created.v1.retry", options.OrderCreatedRetryQueueName);
        Assert.Equal("oims.notification.order-created.v1.dlq", options.OrderCreatedDeadLetterQueueName);

        await host.StopAsync();
    }

    [Theory]
    [InlineData("RabbitMq:ExchangeName", " ", "ExchangeName")]
    [InlineData("RabbitMq:ExchangeType", "direct", "ExchangeType")]
    [InlineData("RabbitMq:OrderCreatedRoutingKey", " ", "OrderCreatedRoutingKey")]
    [InlineData("RabbitMq:OrderCreatedQueueName", " ", "OrderCreatedQueueName")]
    [InlineData("RabbitMq:OrderCreatedConsumerName", " ", "OrderCreatedConsumerName")]
    [InlineData("RabbitMq:ConsumerIngressExchangeName", " ", "ConsumerIngressExchangeName")]
    [InlineData("RabbitMq:RetryExchangeName", " ", "RetryExchangeName")]
    [InlineData("RabbitMq:DeadLetterExchangeName", " ", "DeadLetterExchangeName")]
    [InlineData("RabbitMq:OrderCreatedConsumerRoutingKey", " ", "OrderCreatedConsumerRoutingKey")]
    [InlineData("RabbitMq:OrderCreatedRetryQueueName", " ", "OrderCreatedRetryQueueName")]
    [InlineData("RabbitMq:OrderCreatedDeadLetterQueueName", " ", "OrderCreatedDeadLetterQueueName")]
    [Trait("Requirement", "S9-T02")]
    public async Task MessagingCapability_WithInvalidTopology_FailsStartup(
        string key,
        string value,
        string expectedName)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Port"] = "5672",
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = "oims",
            [key] = value
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

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(expectedName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "S9-T02")]
    public void MessagingCapability_RegistersConfiguredRecoverableConnectionFactoryAsSingleton()
    {
        var password = Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = "rabbitmq.test",
            ["RabbitMq:Port"] = "5673",
            ["RabbitMq:VirtualHost"] = "/oims-test",
            ["RabbitMq:Username"] = "oims-test",
            ["RabbitMq:Password"] = password
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

        var first = host.Services.GetRequiredService<IConnectionFactory>();
        var second = host.Services.GetRequiredService<IConnectionFactory>();
        var factory = Assert.IsType<ConnectionFactory>(first);

        Assert.Same(first, second);
        Assert.Equal("rabbitmq.test", factory.HostName);
        Assert.Equal(5673, factory.Port);
        Assert.Equal("/oims-test", factory.VirtualHost);
        Assert.Equal("oims-test", factory.UserName);
        Assert.Equal(password, factory.Password);
        Assert.True(factory.AutomaticRecoveryEnabled);
        Assert.True(factory.TopologyRecoveryEnabled);
    }
}

using System.Text;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;
using RabbitMQ.Client;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class RabbitMqOutboxPublisherTests(RabbitMqFixture rabbitMq)
{
    [Fact]
    [Trait("Requirement", "S12-T05")]
    public async Task PublishAsync_UsesPersistedPayloadAndRoute()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var storedExchange = $"oims.outbox.stored.{suffix}";
        var storedRoute = $"stored.{suffix}";
        var storedQueue = $"oims.outbox.stored.queue.{suffix}";
        var configuredExchange = $"oims.outbox.configured.{suffix}";
        var configuredRoute = $"configured.{suffix}";
        var configuredQueue = $"oims.outbox.configured.queue.{suffix}";
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };
        using var connectionProvider = new RabbitMqConnectionProvider(connectionFactory);
        var channelFactory = new RabbitMqChannelFactory(connectionProvider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var channel = await channelFactory.CreateChannelAsync(timeout.Token);
        await channel.ExchangeDeclareAsync(storedExchange, ExchangeType.Topic, durable: true, cancellationToken: timeout.Token);
        await channel.ExchangeDeclareAsync(configuredExchange, ExchangeType.Topic, durable: true, cancellationToken: timeout.Token);
        await channel.QueueDeclareAsync(storedQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: timeout.Token);
        await channel.QueueDeclareAsync(configuredQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: timeout.Token);
        await channel.QueueBindAsync(storedQueue, storedExchange, storedRoute, cancellationToken: timeout.Token);
        await channel.QueueBindAsync(configuredQueue, configuredExchange, configuredRoute, cancellationToken: timeout.Token);

        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1, payload,
            message.CorrelationId, message.OccurredAt, storedExchange, storedRoute);
        var options = Options.Create(new RabbitMqOptions
        {
            ExchangeName = configuredExchange,
            OrderCreatedRoutingKey = configuredRoute,
            PublisherConfirmTimeout = TimeSpan.FromSeconds(5)
        });
        var publisher = new RabbitMqOutboxPublisher(channelFactory, options);

        await publisher.PublishAsync(intent, timeout.Token);

        var delivery = await channel.BasicGetAsync(storedQueue, autoAck: true, timeout.Token);
        Assert.NotNull(delivery);
        Assert.Equal(Encoding.UTF8.GetBytes(payload), delivery.Body.ToArray());
        Assert.Equal(message.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.Equal(message.CorrelationId.ToString("D"), delivery.BasicProperties.CorrelationId);
        Assert.Null(await channel.BasicGetAsync(configuredQueue, autoAck: true, timeout.Token));
    }
}

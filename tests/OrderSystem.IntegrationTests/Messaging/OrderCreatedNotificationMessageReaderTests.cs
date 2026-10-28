using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderSystem.IntegrationTests.Messaging;

public sealed class OrderCreatedNotificationMessageReaderTests
{
    [Fact]
    [Trait("Requirement", "S10-T06")]
    [Trait("TestId", "MSG-RETRY-004")]
    public void ReadMessage_InternalIngressWithAttemptButNoDeathEvidence_RejectsMetadata()
    {
        var options = new RabbitMqOptions();
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
        var properties = OrderCreatedV1BasicPropertiesFactory.Create(message);
        properties.Headers = new Dictionary<string, object?>
        {
            [OrderCreatedAttemptMetadataReader.AttemptHeaderName] = 2
        };
        var delivery = new BasicDeliverEventArgs(
            consumerTag: options.OrderCreatedConsumerName,
            deliveryTag: 1,
            redelivered: false,
            exchange: options.ConsumerIngressExchangeName,
            routingKey: options.OrderCreatedConsumerRoutingKey,
            properties,
            body: OrderCreatedV1Serializer.Serialize(message),
            cancellationToken: CancellationToken.None);

        Assert.Throws<InvalidDataException>(() =>
            RabbitMqOrderCreatedNotificationConsumer.ReadMessage(delivery, options));
    }
}
using OrderSystem.Application.Messaging.Contracts;
using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

internal static class OrderCreatedV1BasicPropertiesFactory
{
    public static BasicProperties Create(OrderCreatedV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new BasicProperties
        {
            ContentType = "application/json",
            Type = "OrderCreated",
            Persistent = true,
            MessageId = message.MessageId.ToString("D"),
            CorrelationId = message.CorrelationId.ToString("D")
        };
    }
}
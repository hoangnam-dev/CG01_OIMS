namespace OrderSystem.Infrastructure.Configuration;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public string VirtualHost { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string? Password { get; init; }

    public string ExchangeName { get; init; } = "oims.events";

    public string ExchangeType { get; init; } = "topic";

    public string OrderCreatedRoutingKey { get; init; } = "order.created.v1";

    public string OrderCreatedQueueName { get; init; } = "oims.notification.order-created.v1";

    public string OrderCreatedConsumerName { get; init; } = "OrderCreatedNotificationConsumer";

    public TimeSpan PublisherConfirmTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

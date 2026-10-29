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

    public string ConsumerIngressExchangeName { get; init; } = "oims.notification.ingress";

    public string RetryExchangeName { get; init; } = "oims.notification.retry";

    public string DeadLetterExchangeName { get; init; } = "oims.notification.dead-letter";

    public string OrderCreatedConsumerRoutingKey { get; init; } = "order-created.v1";

    public string OrderCreatedRetryQueueName { get; init; } = "oims.notification.order-created.v1.retry";

    public string OrderCreatedDeadLetterQueueName { get; init; } = "oims.notification.order-created.v1.dlq";

    public TimeSpan PublisherConfirmTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PostgreSqlOutageProbeInterval { get; init; } = TimeSpan.FromSeconds(5);
}

using System.Text;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class RabbitMqOutboxPublisher(IRabbitMqChannelFactory channelFactory, IOptions<RabbitMqOptions> options) : IOutboxMessagePublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.PublisherConfirmTimeout);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = message.EventType,
            Persistent = true,
            MessageId = message.MessageId.ToString("D"),
            CorrelationId = message.CorrelationId.ToString("D")
        };
        await using var channel = await channelFactory.CreatePublisherChannelAsync(timeout.Token);
        await channel.BasicPublishAsync(
            message.ExchangeName,
            message.RoutingKey,
            mandatory: true,
            properties,
            Encoding.UTF8.GetBytes(message.Payload),
            timeout.Token
        );
    }
}

using System.Text;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class RabbitMqOutboxPublisher(IRabbitMqChannelFactory channelFactory, IOptions<RabbitMqOptions> options) : IOutboxMessagePublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!OutboxEventContractCatalog.IsSupported(message.EventType, message.EventVersion))
        {
            throw new OutboxPublishFailureException(OutboxFailureCategory.UnsupportedStoredContract);
        }

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
        try
        {
            IChannel channel;
            try
            {
                channel = await channelFactory.CreatePublisherChannelAsync(timeout.Token);
            }
            catch (AlreadyClosedException)
            {
                throw new OutboxPublishFailureException(OutboxFailureCategory.BrokerUnavailable);
            }

            await using var publisherChannel = channel;
            await publisherChannel.BasicPublishAsync(
                message.ExchangeName,
                message.RoutingKey,
                mandatory: true,
                properties,
                Encoding.UTF8.GetBytes(message.Payload),
                timeout.Token
            );
        }
        catch (AlreadyClosedException)
        {
            throw new OutboxPublishFailureException(OutboxFailureCategory.OutcomeUnknown);
        }
        catch (BrokerUnreachableException)
        {
            throw new OutboxPublishFailureException(OutboxFailureCategory.BrokerUnavailable);
        }
        catch (PublishException exception)
        {
            var category = exception.IsReturn
                ? OutboxFailureCategory.Unroutable
                : OutboxFailureCategory.NegativeConfirmation;
            throw new OutboxPublishFailureException(category);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new OutboxPublishFailureException(OutboxFailureCategory.ConfirmTimeout);
        }
    }
}

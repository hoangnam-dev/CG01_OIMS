using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Messaging;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Configuration;
using RabbitMQ.Client.Exceptions;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqOrderCreatedPublisher(
    IRabbitMqChannelFactory channelFactory,
    IOptions<RabbitMqOptions> options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<RabbitMqOrderCreatedPublisher> logger) : IOrderCreatedPublisher
{
    private readonly RabbitMqOptions rabbitMqOptions = options.Value;

    private static readonly Action<ILogger, Guid, Guid, Guid, Guid, int, Exception?> PublishFailed =
        LoggerMessage.Define<Guid, Guid, Guid, Guid, int>(
            LogLevel.Warning,
            new EventId(1, nameof(PublishFailed)),
            "OrderCreated publication failed after Order commit for MessageId {MessageId}, CorrelationId {CorrelationId}, OrderId {OrderId}, UserId {UserId}, EventVersion {EventVersion}");

    public async Task PublishAsync(OrderCreatedV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
        timeout.CancelAfter(rabbitMqOptions.PublisherConfirmTimeout);

        var body = OrderCreatedV1Serializer.Serialize(message);
        var properties = OrderCreatedV1BasicPropertiesFactory.Create(message);

        try
        {
            await using var channel = await channelFactory.CreatePublisherChannelAsync(timeout.Token);

            await channel.BasicPublishAsync
            (
                rabbitMqOptions.ExchangeName,
                rabbitMqOptions.OrderCreatedRoutingKey,
                mandatory: true,
                properties,
                body,
                timeout.Token
            );
        }
        catch (BrokerUnreachableException exception)
        {
            RecordFailure(message, exception, OrderCreatedPublishFailureReason.BrokerUnavailable);
        }
        catch (PublishException exception)
        {
            RecordFailure(message, exception, OrderCreatedPublishFailureReason.PublishRejected);
        }
        catch (OperationCanceledException exception)
            when (timeout.IsCancellationRequested && !applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            RecordFailure(message, exception, OrderCreatedPublishFailureReason.Timeout);
        }
    }

    private void RecordFailure(OrderCreatedV1 message, Exception exception, OrderCreatedPublishFailureReason reason)
    {
        OrderCreatedPublishMetrics.RecordFailure(reason);
        PublishFailed(
            logger,
            message.MessageId,
            message.CorrelationId,
            message.OrderId,
            message.UserId,
            message.EventVersion,
            exception
        );
    }
}
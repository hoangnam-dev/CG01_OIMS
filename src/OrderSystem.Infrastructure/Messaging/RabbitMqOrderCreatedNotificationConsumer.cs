using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Application.Notifications;
using OrderSystem.Infrastructure.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqOrderCreatedNotificationConsumer(
    IRabbitMqTopologyInitializer topologyInitializer,
    IRabbitMqChannelFactory channelFactory,
    IServiceScopeFactory scopeFactory,
    IOperationHook operationHook,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqOrderCreatedNotificationConsumer> logger) : BackgroundService
{
    internal const string AfterCommitBeforeAcknowledgementCheckpoint = "messaging.order-created.after-commit-before-ack";
    private const string ContentType = "application/json";
    private const string MessageType = "OrderCreated";

    private static readonly Action<ILogger, string?, string?, string, string, string, string, Exception?> ContractValidationFailed = LoggerMessage.Define<string?, string?, string, string, string, string>(
        LogLevel.Error,
        new EventId(2101, nameof(ContractValidationFailed)),
        "OrderCreated contract validation failed for MessageId {MessageId}, CorrelationId {CorrelationId}, ConsumerName {ConsumerName}, Exchange {Exchange}, RoutingKey {RoutingKey}, Queue {Queue}");

    private static readonly Action<ILogger, Guid, Guid, Guid, Guid, string, int, Exception?> MessageProcessedAndAcknowledged = LoggerMessage.Define<Guid, Guid, Guid, Guid, string, int>(
        LogLevel.Information,
        new EventId(2100, nameof(MessageProcessedAndAcknowledged)),
        "OrderCreated notification processed and acknowledged for MessageId {MessageId}, CorrelationId {CorrelationId}, OrderId {OrderId}, UserId {UserId}, ConsumerName {ConsumerName}, EventVersion {EventVersion}");

    private static readonly Action<ILogger, Guid, Guid, Guid, Guid, string, int, Exception?> MessageProcessingFailed = LoggerMessage.Define<Guid, Guid, Guid, Guid, string, int>(
        LogLevel.Error,
        new EventId(2102, nameof(MessageProcessingFailed)),
        "OrderCreated notification processing failed for MessageId {MessageId}, CorrelationId {CorrelationId}, OrderId {OrderId}, UserId {UserId}, ConsumerName {ConsumerName}, EventVersion {EventVersion}");

    private readonly RabbitMqOptions _rabbitMqOptions = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await topologyInitializer.InitializeAsync(stoppingToken);
        await using var channel = await channelFactory.CreateChannelAsync(stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var processingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (processingStopped.Task.IsCompleted)
            {
                return;
            }

            OrderCreatedV1? message = null;
            try
            {
                message = ReadMessage(delivery);
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OrderCreatedNotificationProcessor>();

                await processor.ProcessAsync(message, stoppingToken);
                await operationHook.ReachAsync(AfterCommitBeforeAcknowledgementCheckpoint, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);

                MessageProcessedAndAcknowledged(
                    logger,
                    message.MessageId,
                    message.CorrelationId,
                    message.OrderId,
                    message.UserId,
                    _rabbitMqOptions.OrderCreatedConsumerName,
                    message.EventVersion,
                    null
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                processingStopped.TrySetResult();
            }
            catch (Exception exception)
            {
                if (message is null)
                {
                    ContractValidationFailed(
                        logger,
                        delivery.BasicProperties.MessageId,
                        delivery.BasicProperties.CorrelationId,
                        _rabbitMqOptions.OrderCreatedConsumerName,
                        delivery.Exchange,
                        delivery.RoutingKey,
                        _rabbitMqOptions.OrderCreatedQueueName,
                        exception);
                }
                else
                {
                    MessageProcessingFailed(
                        logger,
                        message.MessageId,
                        message.CorrelationId,
                        message.OrderId,
                        message.UserId,
                        _rabbitMqOptions.OrderCreatedConsumerName,
                        message.EventVersion,
                        exception);
                }

                processingStopped.TrySetResult();
            }
        };

        await channel.BasicConsumeAsync(
            _rabbitMqOptions.OrderCreatedQueueName,
            false,
            _rabbitMqOptions.OrderCreatedConsumerName,
            consumer,
            stoppingToken
        );

        try
        {
            await processingStopped.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static OrderCreatedV1 ReadMessage(BasicDeliverEventArgs delivery)
    {
        if (!string.Equals(delivery.BasicProperties.ContentType, ContentType, StringComparison.Ordinal) ||
            !string.Equals(delivery.BasicProperties.Type, MessageType, StringComparison.Ordinal) ||
            delivery.BasicProperties.Persistent != true ||
            !OrderCreatedV1Serializer.TryDeserialize(delivery.Body.Span, out var message) ||
            message is null ||
            !string.Equals(delivery.BasicProperties.MessageId, message.MessageId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(delivery.BasicProperties.CorrelationId, message.CorrelationId.ToString("D"), StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("OrderCreated message contract is invalid");
        }

        return message;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Application.Notifications;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed partial class RabbitMqOrderCreatedNotificationConsumer(
    IRabbitMqTopologyInitializer topologyInitializer,
    IRabbitMqChannelFactory channelFactory,
    IServiceScopeFactory scopeFactory,
    IOperationHook operationHook,
    IClock clock,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqOrderCreatedNotificationConsumer> logger) : BackgroundService
{
    internal const string AfterCommitBeforeAcknowledgementCheckpoint = "messaging.order-created.after-commit-before-ack";
    internal const string PostgreSqlOutageDetectedCheckpoint = "messaging.order-created.postgresql-outage-detected";
    private const string ContentType = "application/json";
    private const string MessageType = "OrderCreated";

    private static readonly Action<ILogger, string?, string?, string, string, string, string, Exception?> ContractValidationFailed = LoggerMessage.Define<string?, string?, string, string, string, string>(
        LogLevel.Error,
        new EventId(2101, nameof(ContractValidationFailed)),
        "OrderCreated contract validation failed for MessageId {MessageId}, CorrelationId {CorrelationId}, ConsumerName {ConsumerName}, Exchange {Exchange}, RoutingKey {RoutingKey}, Queue {Queue}");

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "OrderCreated notification processed and acknowledged for MessageId {MessageId}, CorrelationId {CorrelationId}, OrderId {OrderId}, UserId {UserId}, ConsumerName {ConsumerName}, EventVersion {EventVersion}, Attempt {Attempt}, Outcome {Outcome}, Exchange {Exchange}, RoutingKey {RoutingKey}")]
    private static partial void MessageProcessedAndAcknowledged(ILogger logger, Guid messageId, Guid correlationId, Guid orderId, Guid userId, string consumerName, int eventVersion, int attempt, string outcome, string exchange, string routingKey);

    private static readonly Action<ILogger, Guid, Guid, Guid, Guid, string, int, Exception?> MessageProcessingFailed = LoggerMessage.Define<Guid, Guid, Guid, Guid, string, int>(
        LogLevel.Error,
        new EventId(2102, nameof(MessageProcessingFailed)),
        "OrderCreated notification processing failed for MessageId {MessageId}, CorrelationId {CorrelationId}, OrderId {OrderId}, UserId {UserId}, ConsumerName {ConsumerName}, EventVersion {EventVersion}");

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Information,
        Message = "OrderCreated transferred for MessageId {MessageId}, CorrelationId {CorrelationId}, Attempt {Attempt}, SourceExchange {SourceExchange}, SourceRoutingKey {SourceRoutingKey}, TargetExchange {TargetExchange}, TargetRoutingKey {TargetRoutingKey}")]
    private static partial void MessageTransferred(ILogger logger, string? messageId, string? correlationId, int attempt, string sourceExchange, string sourceRoutingKey, string targetExchange, string targetRoutingKey);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "OrderCreated transfer failed for MessageId {MessageId}, CorrelationId {CorrelationId}, Attempt {Attempt}, SourceExchange {SourceExchange}, SourceRoutingKey {SourceRoutingKey}, TargetExchange {TargetExchange}, TargetRoutingKey {TargetRoutingKey}")]
    private static partial void MessageTransferFailed(ILogger logger, string? messageId, string? correlationId, int attempt, string sourceExchange, string sourceRoutingKey, string targetExchange, string targetRoutingKey, Exception exception);

    [LoggerMessage(
        EventId = 2105,
        Level = LogLevel.Error,
        Message = "OrderCreated source acknowledgement failed after confirmed transfer for MessageId {MessageId}, CorrelationId {CorrelationId}, Attempt {Attempt}, SourceExchange {SourceExchange}, SourceRoutingKey {SourceRoutingKey}, TargetExchange {TargetExchange}, TargetRoutingKey {TargetRoutingKey}")]
    private static partial void SourceAcknowledgementFailed(ILogger logger, string? messageId, string? correlationId, int attempt, string sourceExchange, string sourceRoutingKey, string targetExchange, string targetRoutingKey, Exception exception);

    private readonly RabbitMqOptions _rabbitMqOptions = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await topologyInitializer.InitializeAsync(stoppingToken);
        await using var channel = await channelFactory.CreateChannelAsync(stoppingToken);
        await using var transferChannel = await channelFactory.CreatePublisherChannelAsync(stoppingToken);
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
            var attempt = 1;
            try
            {
                message = ReadMessage(delivery, _rabbitMqOptions, out attempt);
                var processingResult = await ProcessAsync(message, stoppingToken);
                while (processingResult == OrderCreatedNotificationProcessingResult.TransientFailure &&
                       !await CanConnectToPostgreSqlAsync(stoppingToken))
                {
                    await operationHook.ReachAsync(PostgreSqlOutageDetectedCheckpoint, stoppingToken);
                    await WaitForPostgreSqlRecoveryAsync(stoppingToken);
                    processingResult = await ProcessAsync(message, stoppingToken);
                }
                MessagingReliabilityMetrics.RecordMessageAge(MessagingAgeStage.Consumed, message.OccurredAt, clock.UtcNow);
                MessagingReliabilityMetrics.RecordOperation(
                    MessagingOperation.Process,
                    MapProcessingOutcome(processingResult),
                    attempt == 1 ? MessagingRoute.Main : MessagingRoute.Retry,
                    attempt);
                if (processingResult is OrderCreatedNotificationProcessingResult.FutureOccurredAt
                    or OrderCreatedNotificationProcessingResult.PermanentFailure)
                {
                    var transferred = await TryTransferToDeadLetterAndAcknowledgeAsync(
                        transferChannel,
                        channel,
                        delivery,
                        attempt,
                        stoppingToken);
                    if (!transferred)
                    {
                        processingStopped.TrySetResult();
                    }

                    return;
                }

                if (processingResult == OrderCreatedNotificationProcessingResult.TransientFailure)
                {
                    var transferred = attempt < 3
                        ? await TryTransferToRetryAndAcknowledgeAsync(
                            transferChannel,
                            channel,
                            delivery,
                            attempt + 1,
                            stoppingToken
                        )
                        : await TryTransferToDeadLetterAndAcknowledgeAsync(
                            transferChannel,
                            channel,
                            delivery,
                            attempt,
                            stoppingToken
                        );
                    if (!transferred)
                    {
                        processingStopped.TrySetResult();
                    }

                    return;
                }

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
                    attempt,
                    processingResult == OrderCreatedNotificationProcessingResult.Duplicate ? "duplicate" : "success",
                    delivery.Exchange,
                    delivery.RoutingKey
                );
            }
            catch (OrderCreatedMessageContractException exception)
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

                var transferred = await TryTransferToDeadLetterAndAcknowledgeAsync(
                    transferChannel,
                    channel,
                    delivery,
                    attempt,
                    stoppingToken);
                if (!transferred)
                {
                    processingStopped.TrySetResult();
                }
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

    private async Task<OrderCreatedNotificationProcessingResult> ProcessAsync(
        OrderCreatedV1 message,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<OrderCreatedNotificationProcessor>();

        return await processor.ProcessAsync(
            message,
            _rabbitMqOptions.OrderCreatedConsumerName,
            cancellationToken);
    }

    private async Task WaitForPostgreSqlRecoveryAsync(CancellationToken cancellationToken)
    {
        do
        {
            await Task.Delay(_rabbitMqOptions.PostgreSqlOutageProbeInterval, cancellationToken);
        }
        while (!await CanConnectToPostgreSqlAsync(cancellationToken));
    }

    private async Task<bool> CanConnectToPostgreSqlAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        return await dbContext.Database.CanConnectAsync(cancellationToken);
    }

    private static MessagingOutcome MapProcessingOutcome(OrderCreatedNotificationProcessingResult result) => result switch
    {
        OrderCreatedNotificationProcessingResult.Completed => MessagingOutcome.Success,
        OrderCreatedNotificationProcessingResult.Duplicate => MessagingOutcome.Duplicate,
        OrderCreatedNotificationProcessingResult.FutureOccurredAt => MessagingOutcome.PermanentFailure,
        OrderCreatedNotificationProcessingResult.TransientFailure => MessagingOutcome.TransientFailure,
        OrderCreatedNotificationProcessingResult.PermanentFailure => MessagingOutcome.PermanentFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unsupported notification processing result")
    };

    internal static OrderCreatedV1 ReadMessage(BasicDeliverEventArgs delivery, RabbitMqOptions options)
    {
        return ReadMessage(delivery, options, out _);
    }

    private static OrderCreatedV1 ReadMessage(
        BasicDeliverEventArgs delivery,
        RabbitMqOptions options,
        out int attempt
    )
    {
        attempt = OrderCreatedAttemptMetadataReader.Read(
            delivery.Exchange,
            delivery.RoutingKey,
            delivery.BasicProperties.Headers,
            options
        );
        if (!string.Equals(delivery.BasicProperties.ContentType, ContentType, StringComparison.Ordinal) ||
            !string.Equals(delivery.BasicProperties.Type, MessageType, StringComparison.Ordinal) ||
            delivery.BasicProperties.Persistent != true ||
            !OrderCreatedV1Serializer.TryDeserialize(delivery.Body.Span, out var message) ||
            message is null ||
            !string.Equals(delivery.BasicProperties.MessageId, message.MessageId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(delivery.BasicProperties.CorrelationId, message.CorrelationId.ToString("D"), StringComparison.Ordinal)
        )
        {
            throw new OrderCreatedMessageContractException();
        }

        return message;
    }

    private async Task<bool> TryTransferToRetryAndAcknowledgeAsync(
        IChannel transferChannel,
        IChannel consumerChannel,
        BasicDeliverEventArgs delivery,
        int nextAttempt,
        CancellationToken stoppingToken
    )
    {
        var properties = new BasicProperties(delivery.BasicProperties)
        {
            Headers = delivery.BasicProperties.Headers is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(delivery.BasicProperties.Headers)
        };
        properties.Headers[OrderCreatedAttemptMetadataReader.AttemptHeaderName] = nextAttempt;

        return await TryTransferAndAcknowledgeAsync(
            transferChannel,
            consumerChannel,
            delivery,
            _rabbitMqOptions.RetryExchangeName,
            nextAttempt,
            properties,
            stoppingToken
        );
    }

    private async Task<bool> TryTransferToDeadLetterAndAcknowledgeAsync(
        IChannel transferChannel,
        IChannel consumerChannel,
        BasicDeliverEventArgs delivery,
        int attempt,
        CancellationToken stoppingToken
    )
    {
        var properties = new BasicProperties(delivery.BasicProperties)
        {
            Headers = delivery.BasicProperties.Headers is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(delivery.BasicProperties.Headers)
        };
        properties.Headers[OrderCreatedDeadLetterMetadata.OriginalExchangeHeaderName] = delivery.Exchange;
        properties.Headers[OrderCreatedDeadLetterMetadata.OriginalRoutingKeyHeaderName] = delivery.RoutingKey;

        return await TryTransferAndAcknowledgeAsync(
            transferChannel,
            consumerChannel,
            delivery,
            _rabbitMqOptions.DeadLetterExchangeName,
            attempt,
            properties,
            stoppingToken
        );
    }

    private async Task<bool> TryTransferAndAcknowledgeAsync(
        IChannel transferChannel,
        IChannel consumerChannel,
        BasicDeliverEventArgs delivery,
        string targetExchange,
        int attempt,
        BasicProperties properties,
        CancellationToken stoppingToken
    )
    {
        var transferConfirmed = false;
        try
        {
            using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            transferTimeout.CancelAfter(_rabbitMqOptions.PublisherConfirmTimeout);

            await transferChannel.BasicPublishAsync(
                targetExchange,
                _rabbitMqOptions.OrderCreatedConsumerRoutingKey,
                mandatory: true,
                properties,
                delivery.Body,
                transferTimeout.Token);
            transferConfirmed = true;

            var targetRoute = string.Equals(targetExchange, _rabbitMqOptions.RetryExchangeName, StringComparison.Ordinal)
                ? MessagingRoute.Retry
                : MessagingRoute.DeadLetter;
            MessagingReliabilityMetrics.RecordOperation(
                MessagingOperation.Transfer,
                MessagingOutcome.Success,
                targetRoute,
                attempt);
            if (OrderCreatedV1Serializer.TryDeserialize(delivery.Body.Span, out var message) && message is not null)
            {
                MessagingReliabilityMetrics.RecordMessageAge(
                    targetRoute == MessagingRoute.Retry ? MessagingAgeStage.RetryTransfer : MessagingAgeStage.Consumed,
                    message.OccurredAt,
                    clock.UtcNow);
            }
            MessageTransferred(
                logger,
                delivery.BasicProperties.MessageId,
                delivery.BasicProperties.CorrelationId,
                attempt,
                delivery.Exchange,
                delivery.RoutingKey,
                targetExchange,
                _rabbitMqOptions.OrderCreatedConsumerRoutingKey);
            await consumerChannel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            if (transferConfirmed)
            {
                MessagingReliabilityMetrics.RecordOperation(
                    MessagingOperation.Acknowledge,
                    MessagingOutcome.Failed,
                    attempt == 1 ? MessagingRoute.Main : MessagingRoute.Retry,
                    attempt);
                SourceAcknowledgementFailed(
                    logger,
                    delivery.BasicProperties.MessageId,
                    delivery.BasicProperties.CorrelationId,
                    attempt,
                    delivery.Exchange,
                    delivery.RoutingKey,
                    targetExchange,
                    _rabbitMqOptions.OrderCreatedConsumerRoutingKey,
                    exception);
                return false;
            }

            var targetRoute = string.Equals(targetExchange, _rabbitMqOptions.RetryExchangeName, StringComparison.Ordinal)
                ? MessagingRoute.Retry
                : MessagingRoute.DeadLetter;
            MessagingReliabilityMetrics.RecordOperation(
                MessagingOperation.Transfer,
                MessagingOutcome.Failed,
                targetRoute,
                attempt);
            MessageTransferFailed(
                logger,
                delivery.BasicProperties.MessageId,
                delivery.BasicProperties.CorrelationId,
                attempt,
                delivery.Exchange,
                delivery.RoutingKey,
                targetExchange,
                _rabbitMqOptions.OrderCreatedConsumerRoutingKey,
                exception);

            return false;
        }
    }
}

internal sealed class OrderCreatedMessageContractException() : Exception("OrderCreated message contract is invalid");

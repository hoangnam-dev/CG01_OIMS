using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Configuration;
using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed partial class RabbitMqFailedMessageRedriveDispatcher(
    IRabbitMqTopologyInitializer topologyInitializer,
    IRabbitMqChannelFactory channelFactory,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<RabbitMqOptions> rabbitMqOptions,
    IOptions<MessageRedriveOptions> redriveOptions,
    ILogger<RabbitMqFailedMessageRedriveDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan EmptyQueuePollingInterval = TimeSpan.FromSeconds(1);
    private readonly RabbitMqOptions rabbitMq = rabbitMqOptions.Value;
    private readonly bool enabled = redriveOptions.Value.Enabled;
    private readonly TimeSpan rateInterval = redriveOptions.Value.Enabled
        ? TimeSpan.FromSeconds(1d / redriveOptions.Value.RatePerSecond)
        : TimeSpan.Zero;

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Information,
        Message = "Message Failure redrive published for FailureId {FailureId}, MessageId {MessageId}, CorrelationId {CorrelationId}, ConsumerName {ConsumerName}, TargetExchange {TargetExchange}, TargetRoutingKey {TargetRoutingKey}")]
    private static partial void RedrivePublished(ILogger logger, Guid failureId, Guid messageId, Guid? correlationId, string consumerName, string targetExchange, string targetRoutingKey);

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Warning,
        Message = "Message Failure redrive failed for FailureId {FailureId}, MessageId {MessageId}, CorrelationId {CorrelationId}, ConsumerName {ConsumerName}, TargetExchange {TargetExchange}, TargetRoutingKey {TargetRoutingKey}")]
    private static partial void RedriveFailed(ILogger logger, Guid failureId, Guid? messageId, Guid? correlationId, string consumerName, string targetExchange, string targetRoutingKey, Exception exception);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Warning,
        Message = "Message Failure redrive scan failed")]
    private static partial void RedriveScanFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
        {
            return;
        }

        await topologyInitializer.InitializeAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            FailedMessage? failedMessage = null;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IFailedMessageStore>();
                failedMessage = await store.GetNextRedriveRequestedAsync(rabbitMq.OrderCreatedConsumerName, stoppingToken);
                if (failedMessage is null)
                {
                    await Task.Delay(EmptyQueuePollingInterval, stoppingToken);
                    continue;
                }

                var messageId = failedMessage.MessageId
                    ?? throw new InvalidOperationException("A redrive request requires a MessageId");
                await PublishAsync(failedMessage, messageId, stoppingToken);

                var completedAt = clock.UtcNow;
                if (await store.MarkRedrivenAsync(failedMessage.Id, completedAt, stoppingToken))
                {
                    MessagingReliabilityMetrics.RecordOperation(
                        MessagingOperation.Redrive,
                        MessagingOutcome.Success,
                        MessagingRoute.Main);
                    if (failedMessage.OccurredAt is { } occurredAt)
                    {
                        MessagingReliabilityMetrics.RecordMessageAge(MessagingAgeStage.Redrive, occurredAt, completedAt);
                    }
                    RedrivePublished(
                        logger,
                        failedMessage.Id,
                        messageId,
                        failedMessage.CorrelationId,
                        failedMessage.ConsumerName,
                        rabbitMq.ConsumerIngressExchangeName,
                        rabbitMq.OrderCreatedConsumerRoutingKey
                    );
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (failedMessage is null)
                {
                    RedriveScanFailed(logger, exception);
                }
                else
                {
                    MessagingReliabilityMetrics.RecordOperation(
                        MessagingOperation.Redrive,
                        MessagingOutcome.Failed,
                        MessagingRoute.Main);
                    RedriveFailed(
                        logger,
                        failedMessage.Id,
                        failedMessage.MessageId,
                        failedMessage.CorrelationId,
                        failedMessage.ConsumerName,
                        rabbitMq.ConsumerIngressExchangeName,
                        rabbitMq.OrderCreatedConsumerRoutingKey,
                        exception
                    );
                }
            }

            var delay = failedMessage is null ? EmptyQueuePollingInterval : rateInterval;
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task PublishAsync(FailedMessage failedMessage, Guid messageId, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(rabbitMq.PublisherConfirmTimeout);
        await using var channel = await channelFactory.CreatePublisherChannelAsync(timeout.Token);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = failedMessage.EventType,
            Persistent = true,
            MessageId = messageId.ToString("D"),
            CorrelationId = failedMessage.CorrelationId?.ToString("D")
        };

        await channel.BasicPublishAsync(
            rabbitMq.ConsumerIngressExchangeName,
            rabbitMq.OrderCreatedConsumerRoutingKey,
            mandatory: true,
            properties,
            failedMessage.Body,
            timeout.Token
        );
    }
}

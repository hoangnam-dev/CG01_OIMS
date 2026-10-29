using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Notifications;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqOrderCreatedDeadLetterIntakeConsumer(
    IRabbitMqChannelFactory channelFactory,
    IServiceScopeFactory scopeFactory,
    IOperationHook operationHook,
    IClock clock,
    IIdGenerator idGenerator,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqOrderCreatedDeadLetterIntakeConsumer> logger) : BackgroundService
{
    internal const string AfterCommitBeforeAcknowledgementCheckpoint = "messaging.order-created-dlq.after-commit-before-ack";
    internal const string AfterAcknowledgementCheckpoint = "messaging.order-created-dlq.after-ack";
    private static readonly TimeSpan ProcessingWindow = TimeSpan.FromDays(7);

    private static readonly Action<ILogger, string?, string?, string, Exception?> IntakeFailed =
        LoggerMessage.Define<string?, string?, string>(
            LogLevel.Error,
            new EventId(2110, nameof(IntakeFailed)),
            "OrderCreated dead-letter intake failed for MessageId {MessageId}, CorrelationId {CorrelationId}, Queue {Queue}");

    private readonly RabbitMqOptions _rabbitMqOptions = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var channel = await channelFactory.CreateChannelAsync(stoppingToken);
        await DeclareTopologyAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var processingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (processingStopped.Task.IsCompleted)
            {
                return;
            }

            try
            {
                var failedMessage = CreateFailedMessage(delivery);
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IFailedMessageStore>();
                await store.CaptureAsync(failedMessage, stoppingToken);

                await operationHook.ReachAsync(AfterCommitBeforeAcknowledgementCheckpoint, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                await operationHook.ReachAsync(AfterAcknowledgementCheckpoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                processingStopped.TrySetResult();
            }
            catch (Exception exception)
            {
                IntakeFailed(
                    logger,
                    delivery.BasicProperties.MessageId,
                    delivery.BasicProperties.CorrelationId,
                    _rabbitMqOptions.OrderCreatedDeadLetterQueueName,
                    exception
                );
                processingStopped.TrySetResult();
            }
        };

        await channel.BasicConsumeAsync(
            _rabbitMqOptions.OrderCreatedDeadLetterQueueName,
            false,
            $"{_rabbitMqOptions.OrderCreatedConsumerName}.DeadLetterIntake",
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

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            _rabbitMqOptions.DeadLetterExchangeName,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.QueueDeclareAsync(
            _rabbitMqOptions.OrderCreatedDeadLetterQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            _rabbitMqOptions.OrderCreatedDeadLetterQueueName,
            _rabbitMqOptions.DeadLetterExchangeName,
            _rabbitMqOptions.OrderCreatedConsumerRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken
        );
    }

    private FailedMessage CreateFailedMessage(BasicDeliverEventArgs delivery)
    {
        var body = delivery.Body.ToArray();
        var attemptCount = ReadAttemptCount(delivery.BasicProperties.Headers);
        var envelope = ReadEnvelope(body);
        var classification = Classify(body, envelope, attemptCount);
        var route = ReadOriginalRoute(delivery);
        var rawMessageId = delivery.BasicProperties.MessageId;

        return new FailedMessage(
            idGenerator.NewId(),
            _rabbitMqOptions.OrderCreatedConsumerName,
            ParseGuid(rawMessageId),
            ParseGuid(rawMessageId) is null ? rawMessageId : null,
            ParseGuid(delivery.BasicProperties.CorrelationId),
            delivery.BasicProperties.Type,
            envelope.EventVersion,
            envelope.OccurredAt,
            body,
            FailedMessageBodyFingerprint.Compute(body),
            route.SafeHeaders,
            route.Exchange,
            route.RoutingKey,
            classification.Kind,
            classification.Code,
            attemptCount,
            clock.UtcNow
        );
    }

    private FailureClassification Classify(byte[] body, MessageEnvelope envelope, int attemptCount)
    {
        if (envelope.EventVersion is not null and not 1)
        {
            return new(FailedMessageFailureKind.UnsupportedVersion, "unsupported_version");
        }

        if (!OrderCreatedV1Serializer.TryDeserialize(body, out var message) || message is null)
        {
            return new(FailedMessageFailureKind.Malformed, "malformed_message");
        }

        if (message.OccurredAt > clock.UtcNow)
        {
            return new(FailedMessageFailureKind.Malformed, "future_occurred_at");
        }

        if (clock.UtcNow > message.OccurredAt.Add(ProcessingWindow))
        {
            return new(FailedMessageFailureKind.Expired, "event_expired");
        }

        return attemptCount == 3
            ? new(FailedMessageFailureKind.RetryExhausted, "retry_exhausted")
            : new(FailedMessageFailureKind.Permanent, "permanent_failure");
    }

    private static MessageEnvelope ReadEnvelope(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            int? eventVersion = root.TryGetProperty("eventVersion", out var versionElement) && versionElement.TryGetInt32(out var version)
                ? version
                : null;
            DateTimeOffset? occurredAt = null;
            if (root.TryGetProperty("occurredAt", out var occurredAtElement) &&
                occurredAtElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    occurredAtElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedOccurredAt
                )
            )
            {
                occurredAt = parsedOccurredAt;
            }

            return new(eventVersion, occurredAt);
        }
        catch (JsonException)
        {
            return new(null, null);
        }
    }

    private static OriginalRoute ReadOriginalRoute(BasicDeliverEventArgs delivery)
    {
        if (delivery.BasicProperties.Headers is not null)
        {
            var originalExchange = ReadUtf8(
                delivery.BasicProperties.Headers,
                OrderCreatedDeadLetterMetadata.OriginalExchangeHeaderName
            );
            var originalRoutingKey = ReadUtf8(
                delivery.BasicProperties.Headers,
                OrderCreatedDeadLetterMetadata.OriginalRoutingKeyHeaderName
            );
            if (!string.IsNullOrWhiteSpace(originalExchange) &&
                !string.IsNullOrWhiteSpace(originalRoutingKey)
            )
            {
                return new(
                    originalExchange,
                    originalRoutingKey,
                    JsonSerializer.Serialize(new { originalExchange, originalRoutingKey })
                );
            }
        }

        if (delivery.BasicProperties.Headers is not null &&
            delivery.BasicProperties.Headers.TryGetValue(OrderCreatedAttemptMetadataReader.DeathHeaderName, out var deathValue) &&
            deathValue is IReadOnlyList<object?> { Count: > 0 } deaths &&
            deaths[0] is IDictionary<string, object?> latestDeath
        )
        {
            var exchange = ReadUtf8(latestDeath, "exchange");
            var routingKey = ReadFirstRoutingKey(latestDeath);
            if (!string.IsNullOrWhiteSpace(exchange) && !string.IsNullOrWhiteSpace(routingKey))
            {
                var safeHeaders = JsonSerializer.Serialize(new
                {
                    deathReason = ReadUtf8(latestDeath, "reason"),
                    deathQueue = ReadUtf8(latestDeath, "queue"),
                    deathCount = latestDeath.TryGetValue("count", out var count) ? count : null
                });
                return new(exchange, routingKey, safeHeaders);
            }
        }

        return new(delivery.Exchange, delivery.RoutingKey, "{}");
    }

    private static int ReadAttemptCount(IDictionary<string, object?>? headers)
    {
        return headers is not null &&
               headers.TryGetValue(OrderCreatedAttemptMetadataReader.AttemptHeaderName, out var attemptValue) &&
               attemptValue is int attempt &&
               attempt is >= 1 and <= 3
            ? attempt
            : 1;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;

    private static string? ReadUtf8(IDictionary<string, object?> values, string key)
    {
        return values.TryGetValue(key, out var value) && value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;
    }

    private static string? ReadFirstRoutingKey(IDictionary<string, object?> values)
    {
        if (!values.TryGetValue("routing-keys", out var value) ||
            value is not IReadOnlyList<object?> { Count: > 0 } routingKeys ||
            routingKeys[0] is not byte[] bytes
        )
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record MessageEnvelope(int? EventVersion, DateTimeOffset? OccurredAt);

    private sealed record FailureClassification(FailedMessageFailureKind Kind, string Code);

    private sealed record OriginalRoute(string Exchange, string RoutingKey, string SafeHeaders);
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqOrderCreatedNotificationConsumer> logger) : BackgroundService
{
    private const string ContentType = "application/json";
    private const string MessageType = "OrderCreated";

    private static readonly Action<ILogger, Exception?> ConsumerStoppedAfterFailure = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2101, nameof(ConsumerStoppedAfterFailure)),
        "OrderCreated notification consumer stopped after message processing failure");

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

            try
            {
                var message = ReadMessage(delivery);
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OrderCreatedNotificationProcessor>();

                await processor.ProcessAsync(message, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                processingStopped.TrySetResult();
            }
            catch (Exception exception)
            {
                ConsumerStoppedAfterFailure(logger, exception);
                processingStopped.TrySetResult();
            }
        };

        await channel.BasicConsumeAsync(
            _rabbitMqOptions.OrderCreatedQueueName,
            false,
            _rabbitMqOptions.OrderCreatedConsumerName,
            consumer,
            stoppingToken);

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
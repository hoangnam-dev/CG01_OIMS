using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace OrderSystem.IntegrationTests.Messaging;

public sealed class RabbitMqOrderCreatedPublisherTests
{
    [Fact]
    [Trait("Requirement", "S9-T04")]
    public async Task PublishAsync_WhenOperationalFailureOccurs_IncrementsDeliveryGapMetric()
    {
        var measurements =
            new ConcurrentQueue<(
                long Value,
                KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                        "OrderSystem.Infrastructure.Messaging" &&
                    instrument.Name ==
                        "oims.order_created.publish_failures")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
                measurements.Enqueue((measurement, tags.ToArray())));
        listener.Start();

        var channelFactory = new BlockingPublisherChannelFactory();
        var applicationLifetime = new TestHostApplicationLifetime();
        var options = Options.Create(new RabbitMqOptions
        {
            ExchangeName = "oims.events",
            OrderCreatedRoutingKey = "order.created.v1",
            PublisherConfirmTimeout = TimeSpan.FromMilliseconds(50)
        });
        var publisher = new RabbitMqOrderCreatedPublisher(
            channelFactory,
            options,
            applicationLifetime,
            NullLogger<RabbitMqOrderCreatedPublisher>.Instance);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(
                2026,
                10,
                17,
                8,
                0,
                0,
                TimeSpan.Zero));

        await publisher.PublishAsync(message);

        var recorded = Assert.Single(
            measurements,
            measurement =>
                measurement.Value == 1 &&
                measurement.Tags.Any(tag =>
                    tag.Key == "reason" &&
                    Equals(tag.Value, "timeout")));
        Assert.Equal(
            [new KeyValuePair<string, object?>("reason", "timeout")],
            recorded.Tags);
    }

    [Fact]
    [Trait("Requirement", "S9-T04")]
    public async Task PublishAsync_WhenInternalPublishTimeoutExpires_CompletesWithoutThrowing()
    {
        var channelFactory = new BlockingPublisherChannelFactory();
        var applicationLifetime = new TestHostApplicationLifetime();
        var options = Options.Create(new RabbitMqOptions
        {
            ExchangeName = "oims.events",
            OrderCreatedRoutingKey = "order.created.v1",
            PublisherConfirmTimeout = TimeSpan.FromMilliseconds(50)
        });
        var publisher = new RabbitMqOrderCreatedPublisher(
            channelFactory,
            options,
            applicationLifetime,
            NullLogger<RabbitMqOrderCreatedPublisher>.Instance);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(
                2026,
                10,
                17,
                8,
                0,
                0,
                TimeSpan.Zero));

        var exception = await Record.ExceptionAsync(
            () => publisher.PublishAsync(message));

        Assert.True(channelFactory.WasCancellationObserved);
        Assert.False(applicationLifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Null(exception);
    }

    private sealed class BlockingPublisherChannelFactory : IRabbitMqChannelFactory
    {
        private readonly TaskCompletionSource<IChannel> pendingChannel =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancellationObserved { get; private set; }

        public Task<IChannel> CreateChannelAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<IChannel> CreatePublisherChannelAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await pendingChannel.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                WasCancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted =>
            CancellationToken.None;

        public CancellationToken ApplicationStopping =>
            CancellationToken.None;

        public CancellationToken ApplicationStopped =>
            CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
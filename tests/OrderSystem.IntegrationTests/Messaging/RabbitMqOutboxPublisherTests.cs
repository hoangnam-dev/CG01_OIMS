using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Outbox;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class RabbitMqOutboxPublisherTests(RabbitMqFixture rabbitMq)
{
    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_CallerCancels_PreservesCancellation()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var channelFactory = new WaitingPublisherChannelFactory();
        var options = Options.Create(new RabbitMqOptions
        {
            PublisherConfirmTimeout = TimeSpan.FromSeconds(5)
        });
        var publisher = new RabbitMqOutboxPublisher(channelFactory, options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(1, channelFactory.Attempts);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_PublisherDeadlineExpires_ReportsConfirmTimeout()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var channelFactory = new WaitingPublisherChannelFactory();
        var options = Options.Create(new RabbitMqOptions
        {
            PublisherConfirmTimeout = TimeSpan.FromMilliseconds(50)
        });
        var publisher = new RabbitMqOutboxPublisher(channelFactory, options);

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, CancellationToken.None));

        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.ConfirmTimeout, failure.Category);
        Assert.Equal(1, channelFactory.Attempts);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_BrokerConnectionFails_ReportsBrokerUnavailable()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var channelFactory = new UnavailablePublisherChannelFactory();
        var publisher = new RabbitMqOutboxPublisher(channelFactory, Options.Create(new RabbitMqOptions()));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, CancellationToken.None));

        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.BrokerUnavailable, failure.Category);
        Assert.Equal(1, channelFactory.Attempts);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_UnsupportedStoredOrderCreatedVersion_ReportsUnsupportedStoredContract()
    {
        var message = new OrderCreatedV1(
            EventVersion: 2,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 2,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var channelFactory = new UnavailablePublisherChannelFactory();
        var publisher = new RabbitMqOutboxPublisher(channelFactory, Options.Create(new RabbitMqOptions()));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, CancellationToken.None));

        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.UnsupportedStoredContract, failure.Category);
        Assert.Equal(0, channelFactory.Attempts);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_ChannelAlreadyClosedBeforePublish_ReportsBrokerUnavailable()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var channelFactory = new ClosedPublisherChannelFactory();
        var publisher = new RabbitMqOutboxPublisher(channelFactory, Options.Create(new RabbitMqOptions()));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, CancellationToken.None));

        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.BrokerUnavailable, failure.Category);
        Assert.Equal(1, channelFactory.Attempts);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_ChannelClosesDuringPublish_ReportsOutcomeUnknown()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var channelFactory = new ClosedDuringPublishChannelFactory();
        var publisher = new RabbitMqOutboxPublisher(channelFactory, Options.Create(new RabbitMqOptions()));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, CancellationToken.None));

        Assert.True(channelFactory.PublishEntered);
        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.OutcomeUnknown, failure.Category);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_MandatoryRouteHasNoQueue_ReportsUnroutable()
    {
        var exchange = $"oims.outbox.unroutable.{Guid.NewGuid():N}";
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };
        using var connectionProvider = new RabbitMqConnectionProvider(connectionFactory);
        var channelFactory = new RabbitMqChannelFactory(connectionProvider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var channel = await channelFactory.CreateChannelAsync(timeout.Token);
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, cancellationToken: timeout.Token);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, exchange, "unbound.route");
        var publisher = new RabbitMqOutboxPublisher(channelFactory, Options.Create(new RabbitMqOptions()));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, timeout.Token));

        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.Unroutable, failure.Category);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public async Task PublishAsync_BrokerRejectsFullQueue_ReportsNegativeConfirmation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"oims.outbox.nack.{suffix}";
        var queue = $"oims.outbox.nack.queue.{suffix}";
        var route = $"nack.{suffix}";
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };
        using var connectionProvider = new RabbitMqConnectionProvider(connectionFactory);
        var channelFactory = new RabbitMqChannelFactory(connectionProvider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var channel = await channelFactory.CreatePublisherChannelAsync(timeout.Token);
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: false, autoDelete: true, cancellationToken: timeout.Token);
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true,
            arguments: new Dictionary<string, object?>
            {
                ["x-max-length"] = 1,
                ["x-overflow"] = "reject-publish"
            }, cancellationToken: timeout.Token);
        await channel.QueueBindAsync(queue, exchange, route, cancellationToken: timeout.Token);
        await channel.BasicPublishAsync(exchange, route, mandatory: true,
            new BasicProperties { Persistent = true }, "first"u8.ToArray(), timeout.Token);

        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, exchange, route);
        var publisher = new RabbitMqOutboxPublisher(channelFactory, Options.Create(new RabbitMqOptions()));

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(intent, timeout.Token));

        var failure = Assert.IsType<OutboxPublishFailureException>(error);
        Assert.Equal(OutboxFailureCategory.NegativeConfirmation, failure.Category);
    }

    [Fact]
    [Trait("Requirement", "S12-T05")]
    public async Task PublishAsync_UsesPersistedPayloadAndRoute()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var storedExchange = $"oims.outbox.stored.{suffix}";
        var storedRoute = $"stored.{suffix}";
        var storedQueue = $"oims.outbox.stored.queue.{suffix}";
        var configuredExchange = $"oims.outbox.configured.{suffix}";
        var configuredRoute = $"configured.{suffix}";
        var configuredQueue = $"oims.outbox.configured.queue.{suffix}";
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMq.Host,
            Port = rabbitMq.Port,
            VirtualHost = "/",
            UserName = rabbitMq.Username,
            Password = rabbitMq.Password
        };
        using var connectionProvider = new RabbitMqConnectionProvider(connectionFactory);
        var channelFactory = new RabbitMqChannelFactory(connectionProvider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var channel = await channelFactory.CreateChannelAsync(timeout.Token);
        await channel.ExchangeDeclareAsync(storedExchange, ExchangeType.Topic, durable: true, cancellationToken: timeout.Token);
        await channel.ExchangeDeclareAsync(configuredExchange, ExchangeType.Topic, durable: true, cancellationToken: timeout.Token);
        await channel.QueueDeclareAsync(storedQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: timeout.Token);
        await channel.QueueDeclareAsync(configuredQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: timeout.Token);
        await channel.QueueBindAsync(storedQueue, storedExchange, storedRoute, cancellationToken: timeout.Token);
        await channel.QueueBindAsync(configuredQueue, configuredExchange, configuredRoute, cancellationToken: timeout.Token);

        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1, payload,
            message.CorrelationId, message.OccurredAt, storedExchange, storedRoute);
        var options = Options.Create(new RabbitMqOptions
        {
            ExchangeName = configuredExchange,
            OrderCreatedRoutingKey = configuredRoute,
            PublisherConfirmTimeout = TimeSpan.FromSeconds(5)
        });
        var publisher = new RabbitMqOutboxPublisher(channelFactory, options);

        await publisher.PublishAsync(intent, timeout.Token);

        var delivery = await channel.BasicGetAsync(storedQueue, autoAck: true, timeout.Token);
        Assert.NotNull(delivery);
        Assert.Equal(Encoding.UTF8.GetBytes(payload), delivery.Body.ToArray());
        Assert.Equal(message.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.Equal(message.CorrelationId.ToString("D"), delivery.BasicProperties.CorrelationId);
        Assert.Null(await channel.BasicGetAsync(configuredQueue, autoAck: true, timeout.Token));
    }

    private sealed class WaitingPublisherChannelFactory : IRabbitMqChannelFactory
    {
        public int Attempts { get; private set; }

        public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default) =>
            CreatePublisherChannelAsync(cancellationToken);

        public async Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken = default)
        {
            Attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Publisher deadline did not expire");
        }
    }

    private sealed class UnavailablePublisherChannelFactory : IRabbitMqChannelFactory
    {
        public int Attempts { get; private set; }

        public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default) =>
            CreatePublisherChannelAsync(cancellationToken);

        public Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            return Task.FromException<IChannel>(
                new BrokerUnreachableException(new InvalidOperationException("Synthetic broker outage")));
        }
    }

    private sealed class ClosedPublisherChannelFactory : IRabbitMqChannelFactory
    {
        public int Attempts { get; private set; }

        public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default) =>
            CreatePublisherChannelAsync(cancellationToken);

        public Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var reason = new ShutdownEventArgs(
                ShutdownInitiator.Peer, 320, "Broker stopped",
                new IOException("Synthetic broker outage"), CancellationToken.None);
            return Task.FromException<IChannel>(new AlreadyClosedException(reason));
        }
    }

    private sealed class ClosedDuringPublishChannelFactory : IRabbitMqChannelFactory
    {
        private readonly IChannel channel = DispatchProxy.Create<IChannel, ClosedDuringPublishChannelProxy>();

        public bool PublishEntered => ((ClosedDuringPublishChannelProxy)channel).PublishEntered;

        public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default) =>
            CreatePublisherChannelAsync(cancellationToken);

        public Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(channel);
        }
    }
}

public class ClosedDuringPublishChannelProxy : DispatchProxy
{
    public bool PublishEntered { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IChannel.BasicPublishAsync))
        {
            PublishEntered = true;
            var reason = new ShutdownEventArgs(
                ShutdownInitiator.Peer, 320, "Broker stopped",
                new IOException("Synthetic channel close during publish"), CancellationToken.None);
            return new ValueTask(Task.FromException(new AlreadyClosedException(reason)));
        }

        if (targetMethod?.Name == nameof(IAsyncDisposable.DisposeAsync))
        {
            return ValueTask.CompletedTask;
        }

        throw new InvalidOperationException("Unexpected channel operation in test");
    }
}

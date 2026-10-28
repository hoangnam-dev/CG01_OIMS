using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqTopologyInitializer : IRabbitMqTopologyInitializer
{
    private readonly IRabbitMqChannelFactory _channelFactory;
    private readonly RabbitMqOptions _options;

    public RabbitMqTopologyInitializer(
        IRabbitMqChannelFactory channelFactory,
        IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(options);
        _channelFactory = channelFactory;
        _options = options.Value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var channel = await _channelFactory.CreateChannelAsync(cancellationToken);

        await channel.ExchangeDeclareAsync(
            _options.ExchangeName,
            _options.ExchangeType,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.ExchangeDeclareAsync(
            _options.ConsumerIngressExchangeName,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.ExchangeDeclareAsync(
            _options.RetryExchangeName,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.ExchangeDeclareAsync(
            _options.DeadLetterExchangeName,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.QueueDeclareAsync(
            _options.OrderCreatedQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            _options.OrderCreatedQueueName,
            _options.ExchangeName,
            _options.OrderCreatedRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken
        );
        var quorumQueueArguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum"
        };
        await channel.QueueDeclareAsync(
            _options.OrderCreatedRetryQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: quorumQueueArguments,
            cancellationToken: cancellationToken
        );
        await channel.QueueDeclareAsync(
            _options.OrderCreatedDeadLetterQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: quorumQueueArguments,
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            _options.OrderCreatedQueueName,
            _options.ConsumerIngressExchangeName,
            _options.OrderCreatedConsumerRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            _options.OrderCreatedRetryQueueName,
            _options.RetryExchangeName,
            _options.OrderCreatedConsumerRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            _options.OrderCreatedDeadLetterQueueName,
            _options.DeadLetterExchangeName,
            _options.OrderCreatedConsumerRoutingKey,
            arguments: null,
            cancellationToken: cancellationToken
        );
    }
}

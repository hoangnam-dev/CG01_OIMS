using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

public interface IRabbitMqChannelFactory
{
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
    Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken = default);
}

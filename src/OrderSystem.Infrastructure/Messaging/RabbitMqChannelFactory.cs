using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqChannelFactory : IRabbitMqChannelFactory
{
    private readonly IRabbitMqConnectionProvider _connectionProvider;

    public RabbitMqChannelFactory(IRabbitMqConnectionProvider connectionProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
    }

    public async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken);
    }

    public async Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        return await connection.CreateChannelAsync(options, cancellationToken);
    }
}

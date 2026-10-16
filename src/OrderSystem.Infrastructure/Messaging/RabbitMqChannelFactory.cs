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
}

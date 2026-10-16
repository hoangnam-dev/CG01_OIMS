using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

public interface IRabbitMqConnectionProvider
{
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}

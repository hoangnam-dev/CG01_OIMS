namespace OrderSystem.Infrastructure.Messaging;

public interface IRabbitMqTopologyInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

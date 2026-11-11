using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal interface IOutboxMessagePublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

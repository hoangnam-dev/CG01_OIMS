using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class OutboxPublishFailureException(OutboxFailureCategory category) : Exception("Outbox publication failed")
{
    public OutboxFailureCategory Category { get; } = category;
}

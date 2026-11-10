namespace OrderSystem.Infrastructure.Persistence;

internal enum OutboxMessageStatus
{
    Pending,
    Publishing,
    Published,
    NeedsAttention
}

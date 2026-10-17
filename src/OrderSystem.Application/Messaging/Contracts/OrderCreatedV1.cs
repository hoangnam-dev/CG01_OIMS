namespace OrderSystem.Application.Messaging.Contracts;

public sealed record OrderCreatedV1(
    int EventVersion,
    Guid MessageId,
    Guid OrderId,
    Guid UserId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt
);
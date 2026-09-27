namespace OrderSystem.Domain.Idempotency;

public enum IdempotencyRequestStatus
{
    Processing = 1,
    Completed = 2
}
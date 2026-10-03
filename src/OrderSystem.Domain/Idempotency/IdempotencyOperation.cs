namespace OrderSystem.Domain.Idempotency;

public enum IdempotencyOperation
{
    CreateOrder = 1,
    InitiatePayment = 2
}
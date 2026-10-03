namespace OrderSystem.Application.Payments.Contracts;

public enum PaymentResultApplicationStatus
{
    Accepted = 1,
    Duplicate = 2,
    PaymentNotFound = 3,
    EventConflict = 4
}
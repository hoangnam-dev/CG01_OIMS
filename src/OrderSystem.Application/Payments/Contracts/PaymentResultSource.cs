namespace OrderSystem.Application.Payments.Contracts;

public enum PaymentResultSource
{
    SynchronousResponse = 1,
    Webhook = 2,
    Reconciliation = 3
}
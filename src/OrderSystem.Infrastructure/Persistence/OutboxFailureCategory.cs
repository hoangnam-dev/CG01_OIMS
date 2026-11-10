namespace OrderSystem.Infrastructure.Persistence;

internal enum OutboxFailureCategory
{
    BrokerUnavailable,
    ConfirmTimeout,
    NegativeConfirmation,
    OutcomeUnknown,
    Unroutable,
    UnsupportedStoredContract
}

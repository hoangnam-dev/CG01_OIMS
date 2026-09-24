namespace OrderSystem.Domain.Orders;

public enum OrderCancellationReasonCode
{
    CustomerRequested,
    CustomerSupport,
    FraudSuspected,
    DuplicateOrder,
    InventoryIssue,
    PolicyViolation,
    Other
}

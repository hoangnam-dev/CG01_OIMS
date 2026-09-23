namespace OrderSystem.Application.Orders;

public static class OrderOperationCheckpoints
{
    public const string BeforeInventoryReservation = "orders.create.before-reservation";
    public const string AfterInventoryReservation = "orders.create.after-reservation";
    public const string AfterCreateCommit = "orders.create.after-commit";
    public const string BeforeCancellationLock = "orders.cancel.before-lock";
    public const string AfterCancellationLock = "orders.cancel.after-lock";
}

namespace OrderSystem.Domain.Inventories;

public enum InventoryTransactionType
{
    Adjustment = 1,
    Receipt = 2,
    Reserve = 3,
    Release = 4,
    Issue = 5
}

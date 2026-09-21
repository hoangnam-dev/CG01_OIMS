using OrderSystem.Domain.Inventories;

namespace OrderSystem.Application.Inventories.Contracts;

public sealed record AdjustInventoryRequest(
    int QuantityChange,
    string? Reason);

public sealed record InventoryTransactionListRequest(
    int Page = 1,
    int PageSize = 20,
    InventoryTransactionType? Type = null);

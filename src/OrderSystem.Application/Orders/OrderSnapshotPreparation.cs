using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Domain.Orders;

namespace OrderSystem.Application.Orders;

public sealed record OrderVariantSnapshot(
    Guid ProductVariantId,
    decimal CurrentPrice,
    bool ProductIsActive,
    bool VariantIsActive);

public sealed record PreparedOrderSnapshot(
    IReadOnlyList<OrderItem> Items,
    decimal TotalAmount);

public static class OrderSnapshotPreparation
{
    public static PreparedOrderSnapshot Prepare(
        Guid orderId,
        CreateOrderRequest request,
        IReadOnlyList<OrderVariantSnapshot> variants,
        IIdGenerator idGenerator)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order ID is required.", nameof(orderId));
        }
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(idGenerator);

        var requestedItems = request.Items ?? throw new ArgumentException("Order items are required.", nameof(request));
        if (variants.GroupBy(variant => variant.ProductVariantId).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Loaded variants must not contain duplicate IDs.");
        }

        var variantsById = variants.ToDictionary(variant => variant.ProductVariantId);
        if (requestedItems.Count != variantsById.Count || requestedItems.Any(item => !variantsById.ContainsKey(item.ProductVariantId)))
        {
            throw new InvalidOperationException("Loaded variants must exactly match the requested variants.");
        }

        var items = new List<OrderItem>(requestedItems.Count);
        decimal totalAmount = 0;
        foreach (var item in requestedItems.OrderBy(item => item.ProductVariantId))
        {
            var variant = variantsById[item.ProductVariantId];
            if (!variant.ProductIsActive || !variant.VariantIsActive)
            {
                throw new InvalidOperationException("Orders can contain only active products and variants.");
            }

            var orderItem = new OrderItem(
                idGenerator.NewId(),
                orderId,
                variant.ProductVariantId,
                item.Quantity,
                variant.CurrentPrice);
            items.Add(orderItem);
            totalAmount = checked(totalAmount + orderItem.LineTotal);
        }

        return new PreparedOrderSnapshot(Array.AsReadOnly(items.ToArray()), totalAmount);
    }
}

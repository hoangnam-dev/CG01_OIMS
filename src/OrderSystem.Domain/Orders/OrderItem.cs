using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Orders;

public sealed class OrderItem
{
    private OrderItem()
    {
    }

    public OrderItem(
        Guid id,
        Guid orderId,
        Guid productVariantId,
        int quantity,
        decimal unitPrice)
    {
        Id = DomainGuard.RequiredGuid(id);
        OrderId = DomainGuard.RequiredGuid(orderId);
        ProductVariantId = DomainGuard.RequiredGuid(productVariantId);
        Quantity = DomainGuard.Positive(quantity);
        UnitPrice = DomainGuard.NotNegative(unitPrice);
        LineTotal = checked(Quantity * UnitPrice);
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductVariantId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal LineTotal { get; private set; }
}

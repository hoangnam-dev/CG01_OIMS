using OrderSystem.Domain.Orders;

namespace OrderSystem.UnitTests.Orders;

public sealed class OrderFulfillmentContractTests
{
    [Fact]
    public void ShipmentStatusCatalog_DefinesPendingAsStablePersistedValue()
    {
        var shipmentStatusType = typeof(OrderStatus).Assembly.GetType("OrderSystem.Domain.Shipments.ShipmentStatus");

        Assert.NotNull(shipmentStatusType);
        Assert.True(shipmentStatusType!.IsEnum);
        Assert.Contains("Pending", Enum.GetNames(shipmentStatusType));
    }

    [Theory]
    [InlineData("FulfillmentFailed")]
    public void OrderStatus_FulfillmentCatalog_ContainsStablePersistedValue(string persistedValue)
    {
        var parsed = Enum.TryParse<OrderStatus>(persistedValue, ignoreCase: false, out var status);

        Assert.True(parsed);
        Assert.Equal(persistedValue, status.ToString());
    }

    [Theory]
    [InlineData("ShipmentCreated")]
    [InlineData("ShipmentDelivered")]
    [InlineData("ShipmentReturned")]
    public void OrderStatusReasonCode_FulfillmentCatalog_ContainsStablePersistedValues(string persistedValue)
    {
        var parsed = Enum.TryParse<OrderStatusReasonCode>(persistedValue, ignoreCase: false, out var reasonCode);

        Assert.True(parsed);
        Assert.Equal(persistedValue, reasonCode.ToString());
    }
}

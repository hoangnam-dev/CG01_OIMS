using OrderSystem.Application.Common.Results;

namespace OrderSystem.UnitTests.Common;

public sealed class ApplicationErrorsTests
{
    [Fact]
    public void ShipmentErrorCatalog_DefinesStableCommandConflictCodes()
    {
        Assert.Equal(
            "SHIPMENT_ALREADY_EXISTS",
            ApplicationErrors.Shipments.AlreadyExists.Code);

        Assert.Equal(
            "ORDER_NOT_READY_FOR_FULFILLMENT",
            ApplicationErrors.Shipments.OrderNotReadyForFulfillment.Code);

        Assert.Equal(
            "INVALID_SHIPMENT_STATUS",
            ApplicationErrors.Shipments.InvalidStatus.Code);

        Assert.Equal(
            "SHIPMENT_ALREADY_RESTOCKED",
            ApplicationErrors.Shipments.AlreadyRestocked.Code);
    }
}
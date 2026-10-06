using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Results;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Shipments;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Shipments;

public sealed class ShipmentCommandService(
    IShipmentCommandStore store,
    ICurrentUser currentUser,
    IClock clock,
    IIdGenerator idGenerator
)
{
    public async Task<ApplicationResult<Guid>> CreateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<Guid>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<Guid>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can create Shipments."));
        }
        if (orderId == Guid.Empty)
        {
            return ApplicationResult.Failure<Guid>(ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Order ID is required."]
                    })
                );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var order = await store.GetOrderForUpdateAsync(orderId, cancellationToken);
        if (order is null)
        {
            return ApplicationResult.Failure<Guid>(ApplicationErrors.Orders.NotFound.Create());
        }
        if (await store.ShipmentExistsForOrderAsync(orderId, cancellationToken))
        {
            return ApplicationResult.Failure<Guid>(ApplicationErrors.Shipments.AlreadyExists.Create());
        }
        if (order.Status != OrderStatus.Confirmed)
        {
            return ApplicationResult.Failure<Guid>(ApplicationErrors.Shipments.OrderNotReadyForFulfillment.Create());
        }

        var now = clock.UtcNow;
        var shipmentId = idGenerator.NewId();
        var shipment = new Shipment(shipmentId, order.Id, now);

        order.StartProcessing(now);

        var orderHistory = new OrderStatusHistory(
            idGenerator.NewId(),
            order.Id,
            OrderStatus.Confirmed,
            OrderStatus.Processing,
            OrderStatusHistoryActorType.Admin,
            userId,
            now,
            reason: null,
            OrderStatusReasonCode.ShipmentCreated
        );

        store.AddShipment(shipment);
        store.AddOrderStatusHistory(orderHistory);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipmentId);
    }
}
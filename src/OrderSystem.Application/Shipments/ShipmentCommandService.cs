using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Shipments.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
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
    public async Task<ApplicationResult<ShipmentDto>> CreateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can create Shipments."));
        }
        if (orderId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.ValidationFailed.Create(
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
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Orders.NotFound.Create());
        }
        if (await store.ShipmentExistsForOrderAsync(orderId, cancellationToken))
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.AlreadyExists.Create());
        }
        if (order.Status != OrderStatus.Confirmed)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.OrderNotReadyForFulfillment.Create());
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

        var shipmentActivity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.Created,
            fromStatus: null,
            toStatus: ShipmentStatus.Pending,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null
        );

        store.AddShipment(shipment);
        store.AddOrderStatusHistory(orderHistory);
        store.AddShipmentActivityHistory(shipmentActivity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> StartPickingAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.Pending)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;
        shipment.StartPicking(now);

        var activity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.PickingStarted,
            ShipmentStatus.Pending,
            ShipmentStatus.Picking,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null);

        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> PackAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.ValidationFailed.Create(
                validationErrors: new Dictionary<string, string[]>
                {
                    ["id"] = ["A valid Shipment ID is required."]
                })
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.Picking)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;
        shipment.Pack(now);

        var activityHistory = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.Packed,
            ShipmentStatus.Picking,
            ShipmentStatus.Packed,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null
        );

        store.AddShipmentActivityHistory(activityHistory);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> ShipAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }
        if (shipment.Status != ShipmentStatus.Packed)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;
        var orderItems = await store.ListOrderItemsAsync(shipment.OrderId, cancellationToken);
        var issueTransactions = new List<InventoryTransaction>();

        var sortedItem = orderItems.OrderBy(item => item.ProductVariantId).ThenBy(item => item.Id);
        foreach (var item in sortedItem)
        {
            var issued = await store.TryIssueAsync(item.ProductVariantId, item.Quantity, now, cancellationToken);
            if (!issued)
            {
                throw new InvalidOperationException($"Reserved Inventory is unavailable for Product Variant '{item.ProductVariantId}'.");
            }

            issueTransactions.Add(new InventoryTransaction(
                idGenerator.NewId(),
                item.ProductVariantId,
                InventoryTransactionType.Issue,
                onHandQuantityDelta: -item.Quantity,
                reservedQuantityDelta: -item.Quantity,
                InventoryReferenceType.Shipment,
                shipment.Id,
                reason: null,
                now
            ));
        }

        shipment.Ship(now);

        var activityHistory = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.Shipped,
            ShipmentStatus.Packed,
            ShipmentStatus.Shipped,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null
        );

        store.AddInventoryTransactions(issueTransactions);
        store.AddShipmentActivityHistory(activityHistory);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> StartDeliveryAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }
        if (shipment.Status is not ShipmentStatus.Shipped and not ShipmentStatus.DeliveryFailed)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var fromStatus = shipment.Status;
        var now = clock.UtcNow;

        shipment.StartDelivery(now);
        var activity = new ShipmentActivityHistory(
        idGenerator.NewId(),
        shipment.Id,
        ShipmentActivityType.OutForDeliveryStarted,
        fromStatus,
        ShipmentStatus.OutForDelivery,
        ShipmentActivityActorType.Admin,
        userId,
        now,
        reason: null);

        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> MarkDeliveredAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.OutForDelivery)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var order = await store.GetOrderForUpdateAsync(shipment.OrderId, cancellationToken);
        if (order is null || order.Status != OrderStatus.Processing)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;

        shipment.MarkDelivered(now);
        order.Complete(now);

        var orderHistory = new OrderStatusHistory(
            idGenerator.NewId(),
            order.Id,
            OrderStatus.Processing,
            OrderStatus.Completed,
            OrderStatusHistoryActorType.Admin,
            userId,
            now,
            reason: null,
            OrderStatusReasonCode.ShipmentDelivered
        );

        var activity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.Delivered,
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.Delivered,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null
        );

        store.AddOrderStatusHistory(orderHistory);
        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> MarkDeliveryFailedAsync(Guid shipmentId, MarkDeliveryFailedRequest request, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        var normalizedReason = request?.Reason?.Trim();
        if (string.IsNullOrEmpty(normalizedReason) || normalizedReason.Length > Shipment.MaximumFailureReasonLength)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["reason"] =
                        [
                            $"A delivery failure reason between 1 and {Shipment.MaximumFailureReasonLength} characters is required."
                        ]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.OutForDelivery)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;
        shipment.MarkDeliveryFailed(normalizedReason, now);

        var activity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.DeliveryFailed,
            ShipmentStatus.OutForDelivery,
            ShipmentStatus.DeliveryFailed,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            normalizedReason
        );

        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> StartReturnAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.DeliveryFailed)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;
        shipment.StartReturn(now);

        var activity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.ReturnStarted,
            ShipmentStatus.DeliveryFailed,
            ShipmentStatus.Returning,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null
        );

        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> MarkReturnedAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null
        )
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.Returning)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var payment = await store.GetPaymentForUpdateByOrderIdAsync(shipment.OrderId, cancellationToken)
            ?? throw new InvalidOperationException("A returning Shipment must have a Payment.");

        if (payment.Status != PaymentStatus.Succeeded)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var order = await store.GetOrderForUpdateAsync(shipment.OrderId, cancellationToken);
        if (order is null || order.Status != OrderStatus.Processing)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        var now = clock.UtcNow;

        shipment.MarkReturned(now);
        order.FailFulfillment(now);
        payment.MarkRefundPending(PaymentRefundIdempotencyKey.Create(payment.Id), now);

        var orderHistory = new OrderStatusHistory(
            idGenerator.NewId(),
            order.Id,
            OrderStatus.Processing,
            OrderStatus.FulfillmentFailed,
            OrderStatusHistoryActorType.Admin,
            userId,
            now,
            reason: null,
            OrderStatusReasonCode.ShipmentReturned
        );

        var activity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.Returned,
            ShipmentStatus.Returning,
            ShipmentStatus.Returned,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null
        );

        store.AddOrderStatusHistory(orderHistory);
        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }

    public async Task<ApplicationResult<ShipmentDto>> RestockAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Unauthorized.Create());
        }

        if (currentUser.Role is not UserRole.Admin)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Forbidden.Create(message: "Only Administrators can update Shipments."));
        }

        if (shipmentId == Guid.Empty)
        {
            return ApplicationResult.Failure<ShipmentDto>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["id"] = ["A valid Shipment ID is required."]
                    }
                )
            );
        }

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var shipment = await store.GetShipmentForUpdateAsync(shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.NotFound.Create());
        }

        if (shipment.Status != ShipmentStatus.Returned)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.InvalidStatus.Create());
        }

        if (shipment.RestockedAt is not null)
        {
            return ApplicationResult.Failure<ShipmentDto>(ApplicationErrors.Shipments.AlreadyRestocked.Create());
        }

        var orderItems = await store.ListOrderItemsAsync(shipment.OrderId, cancellationToken);
        var now = clock.UtcNow;
        var returnTransactions = new List<InventoryTransaction>();

        foreach (var item in orderItems.OrderBy(item => item.ProductVariantId).ThenBy(item => item.Id))
        {
            var restocked = await store.TryRestockAsync(
                item.ProductVariantId,
                item.Quantity,
                now,
                cancellationToken
            );

            if (!restocked)
            {
                throw new InvalidOperationException($"Inventory is unavailable for Product Variant '{item.ProductVariantId}'.");
            }

            returnTransactions.Add(new InventoryTransaction(
                idGenerator.NewId(),
                item.ProductVariantId,
                InventoryTransactionType.Return,
                onHandQuantityDelta: item.Quantity,
                reservedQuantityDelta: 0,
                InventoryReferenceType.Shipment,
                shipment.Id,
                reason: null,
                now
            ));
        }

        shipment.MarkRestocked(now);

        var activity = new ShipmentActivityHistory(
            idGenerator.NewId(),
            shipment.Id,
            ShipmentActivityType.Restocked,
            ShipmentStatus.Returned,
            ShipmentStatus.Returned,
            ShipmentActivityActorType.Admin,
            userId,
            now,
            reason: null);

        store.AddInventoryTransactions(returnTransactions);
        store.AddShipmentActivityHistory(activity);

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApplicationResult.Success(shipment.ToDto());
    }
}
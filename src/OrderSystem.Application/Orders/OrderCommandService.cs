using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Application.Orders.Validation;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Orders;

public sealed class OrderCommandService(
    IOrderCommandStore store,
    ICurrentUser currentUser,
    IClock clock,
    IIdGenerator idGenerator,
    IOrderReadStore readStore,
    ICreateOrderResponseSnapshotSerializer responseSnapshotSerializer,
    IOperationHook operationHook,
    TimeSpan reservationDuration)
{
    private static readonly TimeSpan IdempotencyReplayDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan IdempotencyRetentionDuration = TimeSpan.FromHours(72);

    public async Task<ApplicationResult<CreateOrderOutcome>> CreateAsync(
        Guid idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is null)
        {
            return Unauthorized<CreateOrderOutcome>();
        }

        if (currentUser.Role != UserRole.Customer)
        {
            return ApplicationResult.Failure<CreateOrderOutcome>(ApplicationErrors.Forbidden.Create(
                message: "Only Customers can create Orders."));
        }

        if (idempotencyKey == Guid.Empty)
        {
            return ValidationFailure<CreateOrderOutcome>(new Dictionary<string, string[]>
            {
                ["idempotencyKey"] = ["A non-empty Idempotency Key is required."]
            });
        }

        var handlingRequest = OrderRequestValidators.Validate(request);

        if (!handlingRequest.IsValid)
        {
            return ValidationFailure<CreateOrderOutcome>(handlingRequest.Errors);
        }

        var validatedRequest = handlingRequest.Value!;
        var requestHash = CreateOrderRequestHasher.Hash(validatedRequest.Items);
        var now = clock.UtcNow;
        var claimId = idGenerator.NewId();

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        await operationHook.ReachAsync(
            OrderOperationCheckpoints.BeforeIdempotencyClaim,
            cancellationToken);

        var claimResult = await store.TryClaimCreateOrderAsync(
            new CreateOrderIdempotencyClaim(
                claimId,
                userId,
                idempotencyKey,
                requestHash,
                now,
                now.Add(IdempotencyReplayDuration),
                now.Add(IdempotencyRetentionDuration)),
            cancellationToken);

        switch (claimResult.Outcome)
        {
            case IdempotencyClaimOutcome.CompletedReplay:
                var storedResponse = claimResult.StoredResponse
                    ?? throw new InvalidOperationException(
                        "A completed idempotency replay is missing its stored response.");

                return ApplicationResult.Success(new CreateOrderOutcome(
                    storedResponse.ResourceId,
                    storedResponse.HttpStatusCode,
                    storedResponse.ResponseBodyJson,
                    IsReplay: true));

            case IdempotencyClaimOutcome.HashConflict:
                return ApplicationResult.Failure<CreateOrderOutcome>(
                    ApplicationErrors.Idempotency.KeyReused.Create());

            case IdempotencyClaimOutcome.Processing:
                return ApplicationResult.Failure<CreateOrderOutcome>(
                    ApplicationErrors.Idempotency.RequestProcessing.Create());

            case IdempotencyClaimOutcome.Expired:
                return ApplicationResult.Failure<CreateOrderOutcome>(
                    ApplicationErrors.Idempotency.KeyExpired.Create());

            case IdempotencyClaimOutcome.Claimed:
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported idempotency claim outcome '{claimResult.Outcome}'.");
        }

        var productVariantIds = validatedRequest.Items
            .Select(item => item.ProductVariantId)
            .ToArray();

        var snapshots = await store.LoadVariantSnapshotsAsync(productVariantIds, cancellationToken);

        var snapshotErrors = ValidateSnapshot(productVariantIds, snapshots);
        if (snapshotErrors is not null)
        {
            return ApplicationResult.Failure<CreateOrderOutcome>(snapshotErrors);
        }

        var orderId = idGenerator.NewId();
        var prepared = OrderSnapshotPreparation.Prepare(
            orderId,
            validatedRequest,
            snapshots,
            idGenerator);

        var order = new Order(
            orderId,
            userId,
            prepared.TotalAmount,
            now.Add(reservationDuration),
            now);

        await operationHook.ReachAsync(
            OrderOperationCheckpoints.BeforeInventoryReservation,
            cancellationToken);

        foreach (var item in prepared.Items.OrderBy(i => i.ProductVariantId))
        {
            var reservationResult = await store.TryReserveAsync(
                item.ProductVariantId,
                item.Quantity,
                now,
                cancellationToken);
            switch (reservationResult)
            {
                case InventoryReservationResult.Reserved:
                    break;
                case InventoryReservationResult.InsufficientStock:
                    return ApplicationResult.Failure<CreateOrderOutcome>(
                        ApplicationErrors.Orders.InsufficientStock.Create());
                case InventoryReservationResult.InventoryMissing:
                    throw new InvalidOperationException($"Inventory is missing for Product Variant '{item.ProductVariantId}'.");
                default:
                    throw new InvalidOperationException($"Unknown inventory reservation result '{reservationResult}'.");
            }
        }

        var reserveTransaction = prepared.Items
            .Select(i => new InventoryTransaction(
                idGenerator.NewId(),
                i.ProductVariantId,
                InventoryTransactionType.Reserve,
                onHandQuantityDelta: 0,
                reservedQuantityDelta: i.Quantity,
                InventoryReferenceType.Order,
                orderId,
                reason: null,
                now))
            .ToArray();

        store.AddOrder(order);
        store.AddOrderItems(prepared.Items);
        store.AddInventoryTransactions(reserveTransaction);

        await operationHook.ReachAsync(
            OrderOperationCheckpoints.AfterInventoryReservation,
            cancellationToken);

        await store.SaveChangesAsync(cancellationToken);

        var orderDto = await readStore.GetAsync(
            orderId,
            OrderReadScope.OwnOrders,
            userId,
            cancellationToken);

        if (orderDto is null)
        {
            throw new InvalidOperationException(
                $"Staged Order '{orderId}' could not be projected before commit.");
        }

        var responseBodyJson = responseSnapshotSerializer.Serialize(orderDto);
        var completed = await store.TryCompleteCreateOrderAsync(
            claimId,
            orderId,
            httpStatusCode: 201,
            responseBodyJson,
            completedAt: clock.UtcNow,
            cancellationToken);

        if (!completed)
        {
            throw new InvalidOperationException(
                $"Claimed idempotency request '{claimId}' could not be completed.");
        }

        await transaction.CommitAsync(cancellationToken);

        await operationHook.ReachAsync(
            OrderOperationCheckpoints.AfterCreateCommit,
            cancellationToken);

        return ApplicationResult.Success(new CreateOrderOutcome(
            orderId,
            HttpStatusCode: 201,
            responseBodyJson,
            IsReplay: false));
    }

    public async Task<ApplicationResult<OrderDto>> CancelAsync(
      Guid orderId,
      CancelOrderRequest request,
      CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is not { } role)
        {
            return Unauthorized<OrderDto>();
        }

        if (role is not (UserRole.Customer or UserRole.Admin))
        {
            return CancellationForbidden();
        }

        if (orderId == Guid.Empty)
        {
            return ValidationFailure<OrderDto>(new Dictionary<string, string[]>
            {
                ["id"] = ["A valid Order ID is required."]
            });
        }

        if (request.Reason is { } reason && reason.Trim().Length > 500)
        {
            return ValidationFailure<OrderDto>(new Dictionary<string, string[]>
            {
                ["reason"] = ["Cancellation reason cannot exceed 500 characters."]
            });
        }

        if (role == UserRole.Customer && request.ReasonCode is not null)
        {
            return ValidationFailure<OrderDto>(new Dictionary<string, string[]>
            {
                ["reasonCode"] = ["Customers cannot supply a cancellation reason code."]
            });
        }

        if (role == UserRole.Admin &&
            (request.ReasonCode is null ||
             request.ReasonCode == OrderCancellationReasonCode.CustomerRequested ||
             string.IsNullOrWhiteSpace(request.Reason)))
        {
            return ValidationFailure<OrderDto>(new Dictionary<string, string[]>
            {
                ["reasonCode"] = ["An approved cancellation reason code is required for an Admin cancellation."],
                ["reason"] = ["A non-empty cancellation explanation is required for an Admin cancellation."]
            });
        }

        var scope = role == UserRole.Admin ? OrderReadScope.AllOrders : OrderReadScope.OwnOrders;
        Guid? ownerId = role == UserRole.Customer ? userId : null;
        var now = clock.UtcNow;

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        await operationHook.ReachAsync(
          OrderOperationCheckpoints.BeforeCancellationLock,
          cancellationToken);

        var order = await store.GetOrderForUpdateAsync(
          orderId,
          scope,
          ownerId,
          cancellationToken);

        if (order is null)
        {
            return OrderNotFound();
        }

        await operationHook.ReachAsync(
          OrderOperationCheckpoints.AfterCancellationLock,
          cancellationToken);

        if (order.Status != OrderStatus.PendingPayment)
        {
            return NotCancellable();
        }

        var orderItems = await store.ListOrderItemsAsync(orderId, cancellationToken);
        foreach (var item in orderItems.OrderBy(item => item.ProductVariantId).ThenBy(item => item.Id))
        {
            var released = await store.TryReleaseAsync(
              item.ProductVariantId,
              item.Quantity,
              now,
              cancellationToken);

            if (!released)
            {
                throw new InvalidOperationException(
                  $"Unable to release the inventory reservation for Product Variant '{item.ProductVariantId}'.");
            }

            await operationHook.ReachAsync(
              OrderOperationCheckpoints.AfterCancellationRelease,
              cancellationToken);
        }

        order.Cancel(now);

        var releaseTransactions = orderItems
          .OrderBy(item => item.ProductVariantId)
          .ThenBy(item => item.Id)
          .Select(item => new InventoryTransaction(
            idGenerator.NewId(),
            item.ProductVariantId,
            InventoryTransactionType.Release,
            onHandQuantityDelta: 0,
            reservedQuantityDelta: -item.Quantity,
            InventoryReferenceType.Order,
            order.Id,
            reason: null,
            now))
          .ToArray();

        var actorType = role == UserRole.Admin
          ? OrderStatusHistoryActorType.Admin
          : OrderStatusHistoryActorType.Customer;
        var reasonCode = role == UserRole.Admin
          ? request.ReasonCode!.Value
          : OrderCancellationReasonCode.CustomerRequested;
        var history = new OrderStatusHistory(
          idGenerator.NewId(),
          order.Id,
          OrderStatus.PendingPayment,
          OrderStatus.Cancelled,
          actorType,
          userId,
          now,
          request.Reason,
          reasonCode);

        store.AddInventoryTransactions(releaseTransactions);
        store.AddOrderStatusHistory(history);
        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var orderDto = await readStore.GetAsync(orderId, scope, ownerId, cancellationToken);
        if (orderDto is null)
        {
            throw new InvalidOperationException($"Committed Order '{orderId}' could not be projected.");
        }

        return ApplicationResult.Success(orderDto);
    }

    private static ApplicationError? ValidateSnapshot(
        IReadOnlyCollection<Guid> requestVariantIds,
        IReadOnlyList<OrderVariantSnapshot> snapshots)
    {
        var snapshotById = snapshots.ToDictionary(snapshot => snapshot.ProductVariantId);

        if (requestVariantIds.Any(id => !snapshotById.ContainsKey(id)))
        {
            return ApplicationErrors.Products.VariantNotFound.Create(
                message: "One or more Product Variants were not found.");
        }

        if (snapshotById.Values.Any(snapshot => !snapshot.ProductIsActive))
        {
            return ApplicationErrors.Products.NotActive.Create();
        }

        if (snapshotById.Values.Any(snapshot => !snapshot.VariantIsActive))
        {
            return ApplicationErrors.Products.VariantNotActive.Create();
        }

        return null;
    }

    private static ApplicationResult<T> Unauthorized<T>() =>
        ApplicationResult.Failure<T>(ApplicationErrors.Unauthorized.Create());

    private static ApplicationResult<T> ValidationFailure<T>(
        IReadOnlyDictionary<string, string[]> errors) =>
        ApplicationResult.Failure<T>(ApplicationErrors.ValidationFailed.Create(
            validationErrors: errors));

    private static ApplicationResult<OrderDto> OrderNotFound() =>
      ApplicationResult.Failure<OrderDto>(ApplicationErrors.Orders.NotFound.Create());

    private static ApplicationResult<OrderDto> NotCancellable() =>
      ApplicationResult.Failure<OrderDto>(ApplicationErrors.Orders.NotCancellable.Create());

    private static ApplicationResult<OrderDto> CancellationForbidden() =>
      ApplicationResult.Failure<OrderDto>(ApplicationErrors.Forbidden.Create(
        message: "The current user role is not authorized to cancel Orders."));
}

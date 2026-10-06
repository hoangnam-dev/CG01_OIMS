using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public sealed class PaymentResultApplicationService(
    IPaymentResultApplicationStore store,
    IClock clock,
    IIdGenerator idGenerator,
    IPaymentResultApplicationScopeFactory? scopeFactory = null)
{
    public async Task<PaymentResultApplicationOutcome> ApplyAsync(ApplyPaymentResultCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var transaction = await store.BeginTransactionAsync(cancellationToken);

        var payment = await store.GetPaymentForUpdateAsync(command.ProviderPaymentId, cancellationToken);

        if (payment is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.PaymentNotFound);
        }

        var now = clock.UtcNow;
        if (command.Source == PaymentResultSource.Webhook)
        {
            var providerEventData = command.ProviderEvent
            ?? throw new InvalidOperationException("Webhook result is missing provider event data");

            var providerEvent = new ProviderPaymentEvent(
                idGenerator.NewId(),
                payment.Id,
                providerEventData.Provider,
                providerEventData.ProviderEventId,
                command.ProviderPaymentId,
                providerEventData.EventType,
                providerEventData.PayloadHash,
                command.OccurredAt,
                receivedAt: now,
                processedAt: now
            );

            var claimOutcome = await store.ClaimProviderPaymentEventAsync(providerEvent, cancellationToken);

            if (claimOutcome is ProviderPaymentEventClaimOutcome.Duplicate or
    ProviderPaymentEventClaimOutcome.Conflict)
            {
                await transaction.CommitAsync(cancellationToken);
                var status = claimOutcome == ProviderPaymentEventClaimOutcome.Duplicate
                    ? PaymentResultApplicationStatus.Duplicate
                    : PaymentResultApplicationStatus.EventConflict;
                return new PaymentResultApplicationOutcome(status);
            }
        }

        var order = await store.GetOrderForUpdateAsync(payment.OrderId, cancellationToken)
        ?? throw new InvalidOperationException("Payment references a missing Order");

        var isOrderNotPayableReconciliation = command.Outcome == ProviderPaymentOutcome.Failed &&
            command.Source == PaymentResultSource.Reconciliation &&
            string.Equals(command.FailureCode, PaymentFailureCodes.OrderNotPayable, StringComparison.Ordinal);

        if (command.Outcome == ProviderPaymentOutcome.Succeeded &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.Processing &&
            order.Status == OrderStatus.Cancelled)
        {
            await transaction.RollbackAsync(cancellationToken);

            return await ApplyCompensationInFreshScopeAsync(command, cancellationToken);
        }
        else if (command.Outcome == ProviderPaymentOutcome.Succeeded &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.Processing &&
            order.Status == OrderStatus.PendingPayment)
        {
            payment.MarkSucceeded(command.ProviderPaymentId, now);
            order.Confirm(now);

            store.AddOrderStatusHistory(
                new OrderStatusHistory(
                    idGenerator.NewId(),
                    order.Id,
                    OrderStatus.PendingPayment,
                    OrderStatus.Confirmed,
                    OrderStatusHistoryActorType.System,
                    actorUserId: null,
                    occurredAt: now,
                    reason: "Authoritative payment succeeded",
                    reasonCode: OrderStatusReasonCode.Other
                )
            );
        }
        else if (command.Outcome == ProviderPaymentOutcome.Failed &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.Processing &&
            order.Status == OrderStatus.PendingPayment &&
            (!isOrderNotPayableReconciliation || payment.Status == PaymentStatus.Pending))
        {
            var failureCode = command.FailureCode ?? throw new InvalidOperationException("A failed Payment result is missing its failure code");

            var orderItems = await store.ListOrderItemsAsync(order.Id, cancellationToken);

            var releaseTransactions = new List<InventoryTransaction>(orderItems.Count);

            foreach (var item in orderItems)
            {
                var released = await store.TryReleaseReservationAsync(
                    item.ProductVariantId,
                    item.Quantity,
                    now,
                    cancellationToken
                );

                if (!released)
                {
                    throw new InvalidOperationException($"Unable to release reserved inventory for Order '{order.Id}', Product Variant '{item.ProductVariantId}'");
                }

                releaseTransactions.Add(
                    new InventoryTransaction(
                        idGenerator.NewId(),
                        item.ProductVariantId,
                        InventoryTransactionType.Release,
                        onHandQuantityDelta: 0,
                        reservedQuantityDelta: -item.Quantity,
                        InventoryReferenceType.Order,
                        order.Id,
                        reason: null,
                        createdAt: now
                    )
                );

            }

            payment.MarkFailed(command.ProviderPaymentId, failureCode, now);
            order.ExpireAfterPaymentFailure(now);

            store.AddInventoryTransactions(releaseTransactions);
            store.AddOrderStatusHistory(
                new OrderStatusHistory(
                    idGenerator.NewId(),
                    order.Id,
                    OrderStatus.PendingPayment,
                    OrderStatus.Expired,
                    OrderStatusHistoryActorType.System,
                    actorUserId: null,
                    occurredAt: now,
                    reason: "Authoritative payment failed",
                    reasonCode: OrderStatusReasonCode.Other
                )
            );
        }
        else if (command.Outcome == ProviderPaymentOutcome.Succeeded &&
            payment.Status is PaymentStatus.Pending or PaymentStatus.Processing &&
            order.Status == OrderStatus.Expired)
        {
            var orderItems = await store.ListOrderItemsAsync(order.Id, cancellationToken);

            var reserveTransactions = new List<InventoryTransaction>(orderItems.Count);

            foreach (var item in orderItems)
            {
                var reservationResult = await store.TryReserveAsync(
                    item.ProductVariantId,
                    item.Quantity,
                    now,
                    cancellationToken
                );

                if (reservationResult != InventoryReservationResult.Reserved)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return await ApplyCompensationInFreshScopeAsync(command, cancellationToken);
                }

                reserveTransactions.Add(
                    new InventoryTransaction(
                        idGenerator.NewId(),
                        item.ProductVariantId,
                        InventoryTransactionType.Reserve,
                        onHandQuantityDelta: 0,
                        reservedQuantityDelta: item.Quantity,
                        InventoryReferenceType.Order,
                        order.Id,
                        reason: null,
                        createdAt: now
                    )
                );
            }

            payment.MarkSucceeded(command.ProviderPaymentId, now);
            order.RecoverFromExpiredPayment(now);

            store.AddInventoryTransactions(reserveTransactions);
            store.AddOrderStatusHistory(
                new OrderStatusHistory(
                    idGenerator.NewId(),
                    order.Id,
                    OrderStatus.Expired,
                    OrderStatus.Confirmed,
                    OrderStatusHistoryActorType.System,
                    actorUserId: null,
                    occurredAt: now,
                    reason: "Authoritative payment succeeded after inventory reacquisition",
                    reasonCode: OrderStatusReasonCode.Other
                )
            );
        }
        else if (isOrderNotPayableReconciliation &&
            payment.Status == PaymentStatus.Pending &&
            order.Status is OrderStatus.Expired or OrderStatus.Cancelled
        )
        {
            payment.MarkFailed(command.ProviderPaymentId, PaymentFailureCodes.OrderNotPayable, now);
        }

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.Accepted);
    }

    private async Task<PaymentResultApplicationOutcome> ApplyCompensationInFreshScopeAsync(ApplyPaymentResultCommand command, CancellationToken cancellationToken)
    {
        if (scopeFactory is null)
        {
            throw new InvalidOperationException("Payment result application scope factory is not configured");
        }

        await using var applicationScope = await scopeFactory.CreateAsync(cancellationToken);

        var compensationStore = applicationScope.Store;

        await using var transaction = await compensationStore.BeginTransactionAsync(cancellationToken);

        var payment = await compensationStore.GetPaymentForUpdateAsync(command.ProviderPaymentId, cancellationToken);

        if (payment is null)
        {
            await transaction.CommitAsync(cancellationToken);

            return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.PaymentNotFound);
        }

        var now = clock.UtcNow;

        if (command.Source == PaymentResultSource.Webhook)
        {
            var providerEventData = command.ProviderEvent
                ?? throw new InvalidOperationException("Webhook result is missing provider event data");

            var providerEvent = new ProviderPaymentEvent(
                idGenerator.NewId(),
                payment.Id,
                providerEventData.Provider,
                providerEventData.ProviderEventId,
                command.ProviderPaymentId,
                providerEventData.EventType,
                providerEventData.PayloadHash,
                command.OccurredAt,
                receivedAt: now,
                processedAt: now);

            var claimOutcome = await compensationStore.ClaimProviderPaymentEventAsync(providerEvent, cancellationToken);

            if (claimOutcome is ProviderPaymentEventClaimOutcome.Duplicate or ProviderPaymentEventClaimOutcome.Conflict)
            {
                await transaction.CommitAsync(cancellationToken);

                var status = claimOutcome == ProviderPaymentEventClaimOutcome.Duplicate
                    ? PaymentResultApplicationStatus.Duplicate
                    : PaymentResultApplicationStatus.EventConflict;

                return new PaymentResultApplicationOutcome(status);
            }
        }

        var order = await compensationStore.GetOrderForUpdateAsync(payment.OrderId, cancellationToken)
            ?? throw new InvalidOperationException("Payment references a missing Order");

        if (order.Status is
            OrderStatus.Confirmed or
            OrderStatus.Processing or
            OrderStatus.Completed)
        {
            await transaction.CommitAsync(cancellationToken);

            return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.Accepted);
        }

        if (payment.Status is
            PaymentStatus.RefundPending or
            PaymentStatus.Refunded)
        {
            await transaction.CommitAsync(cancellationToken);

            return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.Accepted);
        }

        if (order.Status is not OrderStatus.Expired and
            not OrderStatus.Cancelled)
        {
            await transaction.RollbackAsync(cancellationToken);

            return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.Accepted);
        }

        if (payment.Status is
            PaymentStatus.Pending or
            PaymentStatus.Processing)
        {
            payment.MarkSucceeded(command.ProviderPaymentId, now);
        }

        if (payment.Status != PaymentStatus.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);

            return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.Accepted);
        }

        payment.MarkRefundPending(PaymentRefundIdempotencyKey.Create(payment.Id), now);

        await compensationStore.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new PaymentResultApplicationOutcome(PaymentResultApplicationStatus.Accepted);
    }
}
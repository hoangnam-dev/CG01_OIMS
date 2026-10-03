using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Application.Payments;

public sealed class PaymentResultApplicationService(
    IPaymentResultApplicationStore store,
    IClock clock,
    IIdGenerator idGenerator)
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
}
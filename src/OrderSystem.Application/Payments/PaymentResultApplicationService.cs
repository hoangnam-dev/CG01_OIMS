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
            return new PaymentResultApplicationOutcome();
        }

        var order = await store.GetOrderForUpdateAsync(payment.OrderId, cancellationToken)
        ?? throw new InvalidOperationException("Payment references a missing Order");

        var now = clock.UtcNow;

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
            order.Status == OrderStatus.PendingPayment)
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

        await store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PaymentResultApplicationOutcome();
    }
}
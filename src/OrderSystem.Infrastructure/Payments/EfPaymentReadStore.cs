using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Payments;

internal sealed class EfPaymentReadStore(OrderSystemDbContext dbContext) : IPaymentReadStore
{
    public Task<PaymentResponse?> GetByPaymentIdAsync(
        Guid paymentId,
        PaymentReadScope scope,
        Guid? currentUserId,
        CancellationToken cancellationToken) =>
        Project(
            ApplyScope(scope, currentUserId)
            .Where(payment => payment.Id == paymentId)
        )
        .SingleOrDefaultAsync(cancellationToken);

    public Task<PaymentResponse?> GetByOrderIdAsync(
        Guid orderId,
        PaymentReadScope scope,
        Guid? currentUserId,
        CancellationToken cancellationToken) =>
        Project(
            ApplyScope(scope, currentUserId)
            .Where(payment => payment.OrderId == orderId)
        )
        .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<Payment> ApplyScope(PaymentReadScope scope, Guid? currentUserId) =>
        scope switch
        {
            PaymentReadScope.OwnPayments when currentUserId is { } userId =>
                from payment in dbContext.Payments.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking()
                    on payment.OrderId equals order.Id
                where order.UserId == userId
                select payment,

            PaymentReadScope.AllPayments => dbContext.Payments.AsNoTracking(),

            _ => throw new ArgumentException(
                "An own-payment query requires a current user ID.",
                nameof(currentUserId))
        };

    private static IQueryable<PaymentResponse> Project(IQueryable<Payment> payments) =>
        payments.Select(payment => new PaymentResponse(
            payment.Id,
            payment.OrderId,
            payment.Status,
            payment.Amount,
            payment.Provider,
            payment.ProviderPaymentId,
            payment.FailureCode,
            payment.CreatedAt,
            payment.UpdatedAt
        )
    );
}
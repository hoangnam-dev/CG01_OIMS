using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Payments;

public sealed class PaymentQueryService(IPaymentReadStore store, ICurrentUser currentUser)
{
    private sealed record CallerResolution(
        PaymentReadScope? Scope,
        Guid? UserId,
        ApplicationError? Error
    );
    public async Task<ApplicationResult<PaymentResponse>> GetByPaymentIdAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        if (caller.Error is not null)
        {
            return ApplicationResult.Failure<PaymentResponse>(caller.Error);
        }

        if (paymentId == Guid.Empty)
        {
            return ApplicationResult.Failure<PaymentResponse>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["paymentId"] = ["Payment ID is required."]
                    }
                )
            );
        }

        var payment = await store.GetByPaymentIdAsync(paymentId, caller.Scope!.Value, caller.UserId, cancellationToken);

        return payment is null
            ? ApplicationResult.Failure<PaymentResponse>(ApplicationErrors.Payments.NotFound.Create())
            : ApplicationResult.Success(payment);
    }

    public async Task<ApplicationResult<PaymentResponse>> GetByOrderIdAsync(
    Guid orderId,
    CancellationToken cancellationToken)
    {
        var caller = ResolveCaller();
        if (caller.Error is not null)
        {
            return ApplicationResult.Failure<PaymentResponse>(caller.Error);
        }

        if (orderId == Guid.Empty)
        {
            return ApplicationResult.Failure<PaymentResponse>(
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["orderId"] = ["Order ID is required."]
                    }
                )
            );
        }

        var payment = await store.GetByOrderIdAsync(orderId, caller.Scope!.Value, caller.UserId, cancellationToken);

        return payment is null
            ? ApplicationResult.Failure<PaymentResponse>(ApplicationErrors.Payments.NotFound.Create())
            : ApplicationResult.Success(payment);
    }

    private CallerResolution ResolveCaller()
    {
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not { } userId ||
            userId == Guid.Empty ||
            currentUser.Role is not { } role
        )
        {
            return new(null, null, ApplicationErrors.Unauthorized.Create());
        }

        return role switch
        {
            UserRole.Customer => new(PaymentReadScope.OwnPayments, userId, null),
            UserRole.Admin => new(PaymentReadScope.AllPayments, userId, null),
            _ => new(null, null, ApplicationErrors.Forbidden.Create(message: "The current user role is not authorized to read Payments."))
        };
    }
}
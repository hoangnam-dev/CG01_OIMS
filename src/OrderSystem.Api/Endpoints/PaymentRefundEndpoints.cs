using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Api.Endpoints;

public static class PaymentRefundEndpoints
{
    public static IEndpointRouteBuilder MapPaymentRefundEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/payments/{paymentId}/refund", RetryManualRefundAsync)
            .WithTags("Payments")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status200OK)
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> RetryManualRefundAsync(string paymentId, HttpContext context, PaymentRefundProcessor processor, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(paymentId, out var parsedPaymentId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "paymentId");
        }

        var outcome = await processor.RetryManualAsync(parsedPaymentId, cancellationToken);
        if (outcome is null)
        {
            return ApplicationResultHttpMapper.ToProblem(context, ApplicationErrors.Payments.RefundNotRetryable.Create());
        }

        return outcome.Status switch
        {
            PaymentRefundRetryStatus.PaymentNotFound => ApplicationResultHttpMapper.ToProblem(context, ApplicationErrors.Payments.NotFound.Create()),
            PaymentRefundRetryStatus.NotRetryable => ApplicationResultHttpMapper.ToProblem(context, ApplicationErrors.Payments.RefundNotRetryable.Create()),
            PaymentRefundRetryStatus.Refunded => Results.Ok(new ApiResponse<PaymentResponse>(RequirePayment(outcome), Metadata: null)),
            PaymentRefundRetryStatus.RefundPending => Results.Json(new ApiResponse<PaymentResponse>(RequirePayment(outcome), Metadata: null), statusCode: StatusCodes.Status202Accepted),
            _ => throw new InvalidOperationException("Manual refund retry produced an unsupported Payment status")
        };
    }

    private static PaymentResponse RequirePayment(PaymentRefundRetryResult outcome) =>
    outcome.Payment ?? throw new InvalidOperationException($"Manual refund retry outcome '{outcome.Status}' requires a Payment response");
}

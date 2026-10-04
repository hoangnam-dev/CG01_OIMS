using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Domain.Payments;

namespace OrderSystem.Api.Endpoints;

public static class PaymentInitiationEndpoints
{
    public static IEndpointRouteBuilder MapPaymentInitiationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/orders/{orderId}/payments", InitiateAsync)
            .WithTags("Payments")
            .RequireAuthorization(AuthorizationPolicies.Customer)
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status201Created)
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status200OK)
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        endpoints.MapGet("/api/payments/{paymentId}", GetByPaymentIdAsync)
            .WithTags("Payments")
            .RequireAuthorization()
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapGet("/api/orders/{orderId}/payment", GetByOrderIdAsync)
            .WithTags("Payments")
            .RequireAuthorization()
            .Produces<ApiResponse<PaymentResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> InitiateAsync(
        string orderId,
        InitiatePaymentRequestBody request,
        HttpContext context,
        PaymentCommandService paymentCommandService,
        IPaymentInitiationStore paymentStore,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderId, out var parsedOrderId) || parsedOrderId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "orderId");
        }

        if (!TryGetIdempotencyKey(context.Request.Headers, out var idempotencyKey))
        {
            return ApplicationResultHttpMapper.ToProblem(
                context,
                ApplicationErrors.ValidationFailed.Create(
                    new Dictionary<string, string[]>
                    {
                        ["Idempotency-Key"] = ["A single, non-empty UUID Idempotency-Key header is required."]
                    }
                )
            );
        }

        if (!TryParseScenario(request.Scenario, out var scenario))
        {
            return ApplicationResultHttpMapper.ToProblem(
                context,
                ApplicationErrors.ValidationFailed.Create(
                    new Dictionary<string, string[]>
                    {
                        ["scenario"] = ["A supported payment scenario is required."]
                    }
                )
            );
        }

        var result = await paymentCommandService.InitiateAsync(
            new InitiatePaymentRequest(parsedOrderId, idempotencyKey, scenario),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var userId = currentUser.UserId
            ?? throw new InvalidOperationException("An authenticated customer user ID is required.");

        var initiation = result.Value
            ?? throw new InvalidOperationException("A successful payment initiation must return a result.");

        var payment = await paymentStore.GetPaymentForOwnerAsync(initiation.PaymentId, userId, cancellationToken);

        if (payment is null)
        {
            throw new InvalidOperationException("A successful payment initiation must return an owned payment.");
        }

        var response = new PaymentResponse(
            payment.Id,
            payment.OrderId,
            payment.Status,
            payment.Amount,
            payment.Provider,
            payment.ProviderPaymentId,
            payment.FailureCode,
            payment.CreatedAt,
            payment.UpdatedAt);

        if (initiation.IsReplay)
        {
            context.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Ok(new ApiResponse<PaymentResponse>(response, null));
        }

        return payment.Status is PaymentStatus.Pending or PaymentStatus.Processing
            ? Results.Json(new ApiResponse<PaymentResponse>(response, null), statusCode: StatusCodes.Status202Accepted)
            : Results.Created($"/api/payments/{payment.Id}", new ApiResponse<PaymentResponse>(response, null));
    }

    private static async Task<IResult> GetByPaymentIdAsync(
        string paymentId,
        HttpContext context,
        PaymentQueryService paymentQueryService,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(paymentId, out var parsedPaymentId) || parsedPaymentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "paymentId");
        }

        var result = await paymentQueryService.GetByPaymentIdAsync(parsedPaymentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<PaymentResponse>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> GetByOrderIdAsync(
        string orderId,
        HttpContext context,
        PaymentQueryService paymentQueryService,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(orderId, out var parsedOrderId) || parsedOrderId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "orderId");
        }

        var result = await paymentQueryService.GetByOrderIdAsync(parsedOrderId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<PaymentResponse>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static bool TryGetIdempotencyKey(IHeaderDictionary headers, out Guid idempotencyKey)
    {
        idempotencyKey = Guid.Empty;

        return headers.TryGetValue("Idempotency-Key", out var values)
            && values.Count == 1
            && Guid.TryParse(values[0], out idempotencyKey)
            && idempotencyKey != Guid.Empty;
    }

    private static bool TryParseScenario(string? value, out PaymentScenario scenario)
    {
        scenario = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            scenario = PaymentScenarioCodes.Parse(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

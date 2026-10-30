using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Notifications;
using OrderSystem.Application.Notifications.Contracts;

namespace OrderSystem.Api.Endpoints;

public static class MessageFailureEndpoints
{
    public static IEndpointRouteBuilder MapMessageFailureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var failures = endpoints.MapGroup("/api/operations/message-failures")
            .WithTags("Message Failure Operations")
            .RequireAuthorization(AuthorizationPolicies.Admin);

        failures.MapGet("", ListAsync)
            .Produces<ApiResponse<MessageFailureSummaryDto[]>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        failures.MapGet("/{id}", GetAsync)
            .Produces<ApiResponse<MessageFailureDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        failures.MapPost("/{id}/payload", GetPayloadAsync)
            .Produces<ApiResponse<MessageFailurePayloadDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        failures.MapPost("/{id}/redrive", RequestRedriveAsync)
            .Produces<ApiResponse<MessageFailureDto>>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        failures.MapPost("/{id}/close", CloseAsync)
            .Produces<ApiResponse<MessageFailureDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] MessageFailureListParameters parameters,
        HttpContext context,
        MessageFailureOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(
            new(parameters.Page ?? 1, parameters.PageSize ?? 20, parameters.Status),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var page = result.Value!;
        return Results.Ok(new ApiResponse<MessageFailureSummaryDto[]>(
            page.Items.ToArray(),
            new(new PaginationMetadata(page.Page, page.PageSize, page.TotalCount, page.TotalPages))));
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext context,
        MessageFailureOperationsService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var failedMessageId) || failedMessageId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.GetAsync(failedMessageId, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(new ApiResponse<MessageFailureDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> RequestRedriveAsync(
        string id,
        MessageFailureActionRequest request,
        HttpContext context,
        MessageFailureOperationsService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var failedMessageId) || failedMessageId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.RequestRedriveAsync(
            failedMessageId,
            request.Reason,
            cancellationToken
        );

        return result.IsSuccess
            ? Results.Json(
                new ApiResponse<MessageFailureDto>(result.Value!, null),
                statusCode: StatusCodes.Status202Accepted)
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> GetPayloadAsync(
        string id,
        MessageFailureActionRequest request,
        HttpContext context,
        MessageFailureOperationsService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var failedMessageId) || failedMessageId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.GetPayloadAsync(
            failedMessageId,
            request.Reason,
            cancellationToken
        );

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<MessageFailurePayloadDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> CloseAsync(
        string id,
        MessageFailureActionRequest request,
        HttpContext context,
        MessageFailureOperationsService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var failedMessageId) || failedMessageId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.CloseAsync(
            failedMessageId,
            request.Reason,
            cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<MessageFailureDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    public sealed class MessageFailureListParameters
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }

        public Domain.Notifications.FailedMessageStatus? Status { get; init; }
    }
}

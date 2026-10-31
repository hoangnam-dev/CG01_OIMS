using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Notifications;
using OrderSystem.Application.Notifications.Contracts;

namespace OrderSystem.Api.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var notifications = endpoints.MapGroup("/api/notifications")
            .WithTags("Notifications")
            .RequireAuthorization(AuthorizationPolicies.Customer);

        notifications.MapGet("", ListAsync)
            .Produces<ApiResponse<NotificationDto[]>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] NotificationListParameters parameters,
        HttpContext context,
        NotificationQueryService service,
        CancellationToken cancellationToken
    )
    {
        var result = await service.ListAsync(
            new(parameters.Page ?? 1, parameters.PageSize ?? 20),
            cancellationToken
        );
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var page = result.Value!;
        return Results.Ok(new ApiResponse<NotificationDto[]>(
            page.Items.ToArray(),
            new(new PaginationMetadata(page.Page, page.PageSize, page.TotalCount, page.TotalPages))
        ));
    }

    public sealed class NotificationListParameters
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }
}

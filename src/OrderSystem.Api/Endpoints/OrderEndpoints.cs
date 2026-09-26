using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Orders;

namespace OrderSystem.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/orders")
            .WithTags("Orders")
            .RequireAuthorization();

        orders.MapGet("", ListOrders)
            .Produces<ApiResponse<OrderDto[]>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        orders.MapGet("/{id}", GetOrder)
            .Produces<ApiResponse<OrderDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        orders.MapPost("", CreateOrder)
            .RequireAuthorization(AuthorizationPolicies.Customer)
            .Produces<ApiResponse<OrderDto>>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        orders.MapPost("/{id}/cancel", CancelOrder)
            .Produces<ApiResponse<OrderDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        orders.MapGet("/{id}/status-history", ListStatusHistory)
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .Produces<ApiResponse<OrderStatusHistoryDto[]>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListOrders(
        [AsParameters] OrderListParameters parameters,
        HttpContext context,
        OrderQueryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(
            new(
                parameters.Page ?? 1,
                parameters.PageSize ?? 20,
                ParseStatus(parameters.Status),
                ParseSortDirection(parameters.SortDirection),
                parameters.UserId),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var page = result.Value!;
        return Results.Ok(new ApiResponse<OrderDto[]>(
            page.Items.ToArray(),
            new(new PaginationMetadata(page.Page, page.PageSize, page.TotalCount, page.TotalPages))));
    }

    private static async Task<IResult> GetOrder(
        string id,
        HttpContext context,
        OrderQueryService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var orderId))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.GetAsync(orderId, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(new ApiResponse<OrderDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> CreateOrder(
        CreateOrderRequest request,
        HttpContext context,
        OrderCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 ||
            !Guid.TryParse(values[0], out var key) ||
            key == Guid.Empty
        )
        {
            return ApplicationResultHttpMapper.ToProblem(
                context,
                ApplicationErrors.ValidationFailed.Create(
                    validationErrors: new Dictionary<string, string[]>
                    {
                        ["idempotencyKey"] = ["A single, non-empty UUID Idempotency-Key header is required"]
                    }
                )
            );
        }

        var result = await service.CreateAsync(key, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var outcome = result.Value!;
        context.Response.Headers.Location = $"/api/orders/{outcome.ResourceId}";
        if (outcome.IsReplay)
        {
            context.Response.Headers["Idempotency-Replayed"] = "true";
        }

        return Results.Content(
            outcome.ResponseBodyJson,
            MediaTypeNames.Application.Json,
            Encoding.UTF8,
            outcome.HttpStatusCode);
    }

    private static async Task<IResult> CancelOrder(
        string id,
        CancelOrderRequest request,
        HttpContext context,
        OrderCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var orderId) || orderId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.CancelAsync(orderId, request, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<OrderDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> ListStatusHistory(
        string id,
        [AsParameters] OrderStatusHistoryParameters parameters,
        HttpContext context,
        OrderQueryService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var orderId) || orderId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.ListStatusHistoryAsync(
            orderId,
            new(
                parameters.Page ?? 1,
                parameters.PageSize ?? 50
            ),
            cancellationToken
        );
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var page = result.Value!;

        return Results.Ok(new ApiResponse<OrderStatusHistoryDto[]>(
            page.Items.ToArray(),
            new(new PaginationMetadata(
                page.Page,
                page.PageSize,
                page.TotalCount,
                page.TotalPages
            ))
        ));
    }

    private static OrderStatus? ParseStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? null
            : Enum.TryParse<OrderStatus>(status, true, out var value) && Enum.IsDefined(value)
                ? value
                : (OrderStatus)int.MaxValue;

    private static SortDirection ParseSortDirection(string? sortDirection) =>
        string.IsNullOrWhiteSpace(sortDirection) || sortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase)
            ? SortDirection.Descending
            : sortDirection.Equals("asc", StringComparison.OrdinalIgnoreCase)
                ? SortDirection.Ascending
                : (SortDirection)int.MaxValue;

    public sealed class OrderListParameters
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }

        public string? Status { get; init; }

        public string? SortDirection { get; init; }

        public Guid? UserId { get; init; }
    }

    public sealed class OrderStatusHistoryParameters
    {
        [FromQuery(Name = "page")]
        public int? Page { get; init; }

        [FromQuery(Name = "pageSize")]
        public int? PageSize { get; init; }
    }
}

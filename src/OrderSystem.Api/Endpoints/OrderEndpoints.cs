using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Common.Models;
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
}

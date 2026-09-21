using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Inventories;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Domain.Inventories;

namespace OrderSystem.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var inventory = endpoints.MapGroup("/api/inventory")
            .WithTags("Inventory")
            .RequireAuthorization(AuthorizationPolicies.Admin);

        inventory.MapGet("/{productVariantId}", GetInventory)
            .Produces<ApiResponse<InventoryDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        inventory.MapPost("/{productVariantId}/adjust", AdjustInventory)
            .Produces<ApiResponse<InventoryDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        inventory.MapGet("/{productVariantId}/transactions", ListTransactions)
            .Produces<ApiResponse<InventoryTransactionDto[]>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetInventory(
        string productVariantId,
        HttpContext context,
        InventoryService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(productVariantId, out var id))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "productVariantId");
        }

        var result = await service.GetInventoryAsync(id, cancellationToken);
        return ToOkOrProblem(context, result);
    }

    private static async Task<IResult> AdjustInventory(
        string productVariantId,
        AdjustInventoryRequest request,
        HttpContext context,
        InventoryService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(productVariantId, out var id))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "productVariantId");
        }

        var result = await service.AdjustInventoryAsync(id, request, cancellationToken);
        return ToOkOrProblem(context, result);
    }

    private static async Task<IResult> ListTransactions(
        string productVariantId,
        [AsParameters] InventoryTransactionParameters parameters,
        HttpContext context,
        InventoryService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(productVariantId, out var id))
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "productVariantId");
        }

        var result = await service.ListTransactionsAsync(
            id,
            new(parameters.Page ?? 1, parameters.PageSize ?? 20, parameters.Type),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        var page = result.Value!;
        return Results.Ok(new ApiResponse<InventoryTransactionDto[]>(
            page.Items.ToArray(),
            new(new PaginationMetadata(page.Page, page.PageSize, page.TotalCount, page.TotalPages))));
    }

    private static IResult ToOkOrProblem<T>(HttpContext context, ApplicationResult<T> result) =>
        result.IsSuccess
            ? Results.Ok(new ApiResponse<T>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);

    public sealed class InventoryTransactionParameters
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }

        public InventoryTransactionType? Type { get; init; }
    }
}

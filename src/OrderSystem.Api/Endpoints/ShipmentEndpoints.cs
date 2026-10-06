using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Shipments;
using OrderSystem.Application.Shipments.Contracts;

namespace OrderSystem.Api.Endpoints;

public static class ShipmentEndpoints
{
    public static IEndpointRouteBuilder MapShipmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var shipments = endpoints.MapGroup("/api/shipments")
            .WithTags("Shipments")
            .RequireAuthorization(AuthorizationPolicies.Admin);

        shipments.MapPost("/{id}/start-picking", StartPickingAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/pack", PackAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/ship", ShipAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/out-for-delivery", StartDeliveryAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/delivery-failed", MarkDeliveryFailedAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/mark-delivered", MarkDeliveredAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/start-return", StartReturnAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/mark-returned", MarkReturnedAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        shipments.MapPost("/{id}/restock", RestockAsync)
            .Produces<ApiResponse<ShipmentDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> StartPickingAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.StartPickingAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> PackAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.PackAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> ShipAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.ShipAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> StartDeliveryAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.StartDeliveryAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> MarkDeliveryFailedAsync(
        string id,
        MarkDeliveryFailedRequest request,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.MarkDeliveryFailedAsync(shipmentId, request, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> MarkDeliveredAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.MarkDeliveredAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> StartReturnAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.StartReturnAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> MarkReturnedAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.MarkReturnedAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> RestockAsync(
        string id,
        HttpContext context,
        ShipmentCommandService service,
        CancellationToken cancellationToken
    )
    {
        if (!Guid.TryParse(id, out var shipmentId) || shipmentId == Guid.Empty)
        {
            return ApplicationResultHttpMapper.InvalidUuid(context, "id");
        }

        var result = await service.RestockAsync(shipmentId, cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new ApiResponse<ShipmentDto>(result.Value!, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }
}
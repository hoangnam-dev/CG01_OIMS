using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Application.Orders.Validation;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Orders;

public sealed class OrderQueryService(IOrderReadStore store, ICurrentUser currentUser)
{
    public async Task<ApplicationResult<PagedResult<OrderDto>>> ListAsync(
        OrderListRequest request,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller<PagedResult<OrderDto>>();
        if (caller.Error is not null)
        {
            return ApplicationResult.Failure<PagedResult<OrderDto>>(caller.Error);
        }

        var validation = OrderRequestValidators.ValidateList(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<PagedResult<OrderDto>>(validation.Errors);
        }

        var scope = caller.Scope!.Value;
        if (scope == OrderReadScope.OwnOrders && request.UserId.HasValue)
        {
            return ValidationFailure<PagedResult<OrderDto>>(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["userId"] = ["User ID filtering requires Admin access."]
            });
        }

        var ownerId = scope == OrderReadScope.OwnOrders
            ? caller.UserId
            : request.UserId;
        var page = await store.ListAsync(request, scope, ownerId, cancellationToken);
        return ApplicationResult.Success(page);
    }

    public async Task<ApplicationResult<OrderDto>> GetAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller<OrderDto>();
        if (caller.Error is not null)
        {
            return ApplicationResult.Failure<OrderDto>(caller.Error);
        }

        if (orderId == Guid.Empty)
        {
            return ValidationFailure<OrderDto>(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["id"] = ["Order ID is required."]
            });
        }

        var order = await store.GetAsync(orderId, caller.Scope!.Value, caller.UserId, cancellationToken);
        return order is null
            ? OrderNotFound()
            : ApplicationResult.Success(order);
    }

    private CallerResolution<T> ResolveCaller<T>()
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty || currentUser.Role is not { } role)
        {
            return new(null, null, Unauthorized<T>().Error);
        }

        return role switch
        {
            UserRole.Customer => new(OrderReadScope.OwnOrders, userId, null),
            UserRole.Admin => new(OrderReadScope.AllOrders, userId, null),
            _ => new(null, null, Forbidden<T>().Error)
        };
    }

    private static ApplicationResult<T> ValidationFailure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        ApplicationResult.Failure<T>(new(
            ApplicationErrorKind.Validation,
            "VALIDATION_FAILED",
            "One or more validation errors occurred.",
            errors));

    private static ApplicationResult<T> Unauthorized<T>() =>
        ApplicationResult.Failure<T>(new(
            ApplicationErrorKind.Unauthorized,
            "UNAUTHORIZED",
            "Authentication is required."));

    private static ApplicationResult<T> Forbidden<T>() =>
        ApplicationResult.Failure<T>(new(
            ApplicationErrorKind.Forbidden,
            "FORBIDDEN",
            "The current user role is not authorized to read Orders."));

    private static ApplicationResult<OrderDto> OrderNotFound() =>
        ApplicationResult.Failure<OrderDto>(new(
            ApplicationErrorKind.NotFound,
            "ORDER_NOT_FOUND",
            "The Order was not found."));

    private sealed record CallerResolution<T>(OrderReadScope? Scope, Guid? UserId, ApplicationError? Error);
}

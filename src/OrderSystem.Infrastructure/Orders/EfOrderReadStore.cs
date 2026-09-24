using Microsoft.EntityFrameworkCore;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Orders;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Orders;

internal sealed class EfOrderReadStore(OrderSystemDbContext dbContext) : IOrderReadStore
{
    public async Task<PagedResult<OrderDto>> ListAsync(
        OrderListRequest request,
        OrderReadScope scope,
        Guid? currentUserId,
        CancellationToken cancellationToken)
    {
        var orders = ApplyFilters(dbContext.Orders.AsNoTracking(), request, scope, currentUserId);
        var totalCount = await orders.LongCountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        var offset = (long)(request.Page - 1) * request.PageSize;
        if (offset >= totalCount)
        {
            return new([], request.Page, request.PageSize, totalCount, totalPages);
        }

        var pageOrders = await ApplyOrdering(orders, request.SortDirection)
            .Skip((int)offset)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);
        var itemsByOrderId = await GetItemsByOrderIdAsync(pageOrders.Select(order => order.Id), cancellationToken);
        var items = pageOrders.Select(order => order.ToDto(itemsByOrderId.GetValueOrDefault(order.Id, []))).ToArray();
        return new(items, request.Page, request.PageSize, totalCount, totalPages);
    }

    public async Task<OrderDto?> GetAsync(
        Guid orderId,
        OrderReadScope scope,
        Guid? currentUserId,
        CancellationToken cancellationToken)
    {
        var order = await ApplyScope(dbContext.Orders.AsNoTracking(), scope, currentUserId)
            .SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var itemsByOrderId = await GetItemsByOrderIdAsync([order.Id], cancellationToken);
        return order.ToDto(itemsByOrderId.GetValueOrDefault(order.Id, []));
    }

    public async Task<PagedResult<OrderStatusHistoryDto>?> ListStatusHistoryAsync(
        Guid orderId,
        OrderStatusHistoryListRequest request,
        CancellationToken cancellationToken
    )
    {
        var orderExists = await dbContext.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId, cancellationToken);

        if (!orderExists)
        {
            return null;
        }

        var history = dbContext.OrderStatusHistories.AsNoTracking().Where(h => h.OrderId == orderId);

        var totalCount = await history.LongCountAsync(cancellationToken);
        var totalPage = (int)Math.Ceiling(totalCount / (double)request.PageSize);
        var offset = (long)(request.Page - 1) *  request.PageSize;

        if(offset >= totalCount)
        {
            return new([], request.Page, request.PageSize, totalCount, totalPage);
        }

        var items = await history.OrderByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Skip((int)offset)
            .Take(request.PageSize)
            .Select(h => new OrderStatusHistoryDto(
                h.Id,
                h.OrderId,
                h.FromStatus,
                h.ToStatus,
                h.ActorType,
                h.ActorUserId,
                h.ReasonCode,
                h.Reason,
                h.OccurredAt
            ))
            .ToArrayAsync(cancellationToken);

        return new(items, request.Page, request.PageSize, totalCount, totalPage);
    }

    private static IQueryable<Order> ApplyFilters(
        IQueryable<Order> orders,
        OrderListRequest request,
        OrderReadScope scope,
        Guid? currentUserId)
    {
        orders = ApplyScope(orders, scope, currentUserId);
        if (scope == OrderReadScope.AllOrders && request.UserId is { } userId)
        {
            orders = orders.Where(order => order.UserId == userId);
        }

        return request.Status is { } status
            ? orders.Where(order => order.Status == status)
            : orders;
    }

    private static IQueryable<Order> ApplyScope(IQueryable<Order> orders, OrderReadScope scope, Guid? currentUserId) =>
        scope switch
        {
            OrderReadScope.OwnOrders when currentUserId is { } userId => orders.Where(order => order.UserId == userId),
            OrderReadScope.AllOrders => orders,
            _ => throw new ArgumentException("An own-order query requires a current user ID.", nameof(currentUserId))
        };

    private static IOrderedQueryable<Order> ApplyOrdering(IQueryable<Order> orders, SortDirection direction) =>
        direction switch
        {
            SortDirection.Ascending => orders.OrderBy(order => order.CreatedAt).ThenBy(order => order.Id),
            SortDirection.Descending => orders.OrderByDescending(order => order.CreatedAt).ThenByDescending(order => order.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported Order ordering.")
        };

    private async Task<Dictionary<Guid, IReadOnlyList<OrderItemDto>>> GetItemsByOrderIdAsync(
        IEnumerable<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        var ids = orderIds.ToArray();
        var itemRows = await (
            from item in dbContext.OrderItems.AsNoTracking()
            join variant in dbContext.ProductVariants.AsNoTracking() on item.ProductVariantId equals variant.Id
            where ids.Contains(item.OrderId)
            orderby item.OrderId, item.Id
            select new { item.OrderId, Item = new OrderItemDto(item.Id, item.ProductVariantId, variant.Sku, variant.Name, item.Quantity, item.UnitPrice, item.LineTotal) })
            .ToListAsync(cancellationToken);

        return itemRows
            .GroupBy(row => row.OrderId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OrderItemDto>)group.Select(row => row.Item).ToArray());
    }
}

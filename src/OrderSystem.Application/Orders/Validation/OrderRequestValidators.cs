using OrderSystem.Application.Common.Validation;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Orders.Contracts;

namespace OrderSystem.Application.Orders.Validation;

public static class OrderRequestValidators
{
    public static ValidationResult<OrderListRequest> ValidateList(OrderListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Page < 1)
        {
            errors["page"] = ["Page must be at least 1."];
        }

        if (request.PageSize is < 1 or > 100)
        {
            errors["pageSize"] = ["Page size must be between 1 and 100."];
        }

        if (request.Status is { } status && !Enum.IsDefined(status))
        {
            errors["status"] = ["Order status is invalid."];
        }

        if (!Enum.IsDefined(request.SortDirection))
        {
            errors["sortDirection"] = ["Sort direction is invalid."];
        }

        return errors.Count == 0
            ? ValidationResult.Success(request)
            : ValidationResult.Failure<OrderListRequest>(errors);
    }

    public static ValidationResult<CreateOrderRequest> Validate(CreateOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Items is null || request.Items.Count == 0)
        {
            errors["items"] = ["At least one order item is required."];
        }
        else
        {
            ValidateItems(request.Items, errors);
        }

        if (errors.Count > 0)
        {
            return ValidationResult.Failure<CreateOrderRequest>(errors);
        }

        var normalizedItems = request.Items!;
        return ValidationResult.Success(new CreateOrderRequest(Array.AsReadOnly(normalizedItems.ToArray())));
    }

    private static void ValidateItems(
        IReadOnlyList<CreateOrderItemRequest> items,
        Dictionary<string, string[]> errors)
    {
        var itemIndexesByVariantId = new Dictionary<Guid, List<int>>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.ProductVariantId == Guid.Empty)
            {
                errors[$"items[{index}].productVariantId"] = ["Product variant ID is required."];
            }
            else
            {
                if (!itemIndexesByVariantId.TryGetValue(item.ProductVariantId, out var indexes))
                {
                    indexes = [];
                    itemIndexesByVariantId.Add(item.ProductVariantId, indexes);
                }

                indexes.Add(index);
            }

            if (item.Quantity <= 0)
            {
                errors[$"items[{index}].quantity"] = ["Quantity must be greater than zero."];
            }
        }

        foreach (var indexes in itemIndexesByVariantId.Values.Where(indexes => indexes.Count > 1))
        {
            foreach (var index in indexes)
            {
                errors[$"items[{index}].productVariantId"] = ["Each product variant may appear only once."];
            }
        }
    }
}

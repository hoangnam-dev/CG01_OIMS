using OrderSystem.Application.Common.Validation;
using OrderSystem.Application.Inventories.Contracts;

namespace OrderSystem.Application.Inventories.Validation;

public static class InventoryRequestValidators
{
    private const int MaximumReasonLength = 256;

    public static ValidationResult<AdjustInventoryRequest> Validate(AdjustInventoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.QuantityChange == 0)
        {
            errors["quantityChange"] = ["Quantity change cannot be zero."];
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["Reason is required."];
        }
        else if (reason.Length > MaximumReasonLength)
        {
            errors["reason"] = [$"Reason cannot exceed {MaximumReasonLength} characters."];
        }

        return errors.Count == 0
            ? ValidationResult.Success(request with { Reason = reason })
            : ValidationResult.Failure<AdjustInventoryRequest>(errors);
    }

    public static ValidationResult<InventoryTransactionListRequest> Validate(InventoryTransactionListRequest request)
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

        if (request.Type is { } type && !Enum.IsDefined(type))
        {
            errors["type"] = ["Transaction type is invalid."];
        }

        return errors.Count == 0
            ? ValidationResult.Success(request)
            : ValidationResult.Failure<InventoryTransactionListRequest>(errors);
    }
}

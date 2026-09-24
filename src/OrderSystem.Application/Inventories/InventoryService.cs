using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Application.Inventories.Validation;
using OrderSystem.Domain.Inventories;

namespace OrderSystem.Application.Inventories;

public sealed class InventoryService(IInventoryStore store, IClock clock, IIdGenerator? idGenerator = null)
{
    private readonly IIdGenerator ids = idGenerator ?? new Uuid7IdGenerator();

    public async Task<ApplicationResult<InventoryDto>> GetInventoryAsync(
        Guid productVariantId,
        CancellationToken cancellationToken)
    {
        if (productVariantId == Guid.Empty)
        {
            return InvalidProductVariantId<InventoryDto>();
        }

        var inventory = await store.GetByProductVariantIdAsync(
            productVariantId,
            cancellationToken);

        return inventory is null
            ? InventoryNotFound<InventoryDto>()
            : ApplicationResult.Success(inventory.ToDto());
    }

    public async Task<ApplicationResult<PagedResult<InventoryTransactionDto>>> ListTransactionsAsync(
        Guid productVariantId,
        InventoryTransactionListRequest request,
        CancellationToken cancellationToken)
    {
        if (productVariantId == Guid.Empty)
        {
            return InvalidProductVariantId<PagedResult<InventoryTransactionDto>>();
        }

        var validation = InventoryRequestValidators.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<PagedResult<InventoryTransactionDto>>(
                validation.Errors);
        }

        if (!await store.ExistsByProductVariantIdAsync(
                productVariantId,
                cancellationToken))
        {
            return InventoryNotFound<PagedResult<InventoryTransactionDto>>();
        }

        var page = await store.ListTransactionsAsync(
            productVariantId,
            validation.Value!,
            cancellationToken);

        return ApplicationResult.Success(page);
    }

    public async Task<ApplicationResult<InventoryDto>> AdjustInventoryAsync(
        Guid productVariantId,
        AdjustInventoryRequest request,
        CancellationToken cancellationToken)
    {
        if (productVariantId == Guid.Empty)
        {
            return InvalidProductVariantId<InventoryDto>();
        }

        var validation = InventoryRequestValidators.Validate(request);
        if (!validation.IsValid)
        {
            return ValidationFailure<InventoryDto>(validation.Errors);
        }

        var inventory = await store.GetByProductVariantIdAsync(
            productVariantId,
            cancellationToken);
        if (inventory is null)
        {
            return InventoryNotFound<InventoryDto>();
        }

        var validRequest = validation.Value!;
        var now = clock.UtcNow;
        try
        {
            inventory.AdjustOnHand(validRequest.QuantityChange, now);
        }
        catch (InvalidOperationException)
        {
            return InventoryInvariantViolation<InventoryDto>();
        }

        store.AddTransaction(new InventoryTransaction(
            ids.NewId(),
            productVariantId,
            InventoryTransactionType.Adjustment,
            validRequest.QuantityChange,
            0,
            null,
            null,
            validRequest.Reason,
            now));

        await store.SaveChangesAsync(cancellationToken);
        return ApplicationResult.Success(inventory.ToDto());
    }

    private static ApplicationResult<T> InvalidProductVariantId<T>() =>
        ValidationFailure<T>(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["productVariantId"] = ["Product Variant ID is required."]
        });

    private static ApplicationResult<T> ValidationFailure<T>(
        IReadOnlyDictionary<string, string[]> errors) =>
        ApplicationResult.Failure<T>(
            ApplicationErrors.ValidationFailed.Create(validationErrors: errors));

    private static ApplicationResult<T> InventoryNotFound<T>() =>
        ApplicationResult.Failure<T>(ApplicationErrors.Inventories.NotFound.Create());

    private static ApplicationResult<T> InventoryInvariantViolation<T>() =>
        ApplicationResult.Failure<T>(ApplicationErrors.Inventories.InvariantViolation.Create());
}

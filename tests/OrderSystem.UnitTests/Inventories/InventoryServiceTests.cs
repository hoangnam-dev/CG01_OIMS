using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Models;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Inventories;
using OrderSystem.Application.Inventories.Contracts;
using OrderSystem.Domain.Inventories;

namespace OrderSystem.UnitTests.Inventories;

public sealed class InventoryServiceTests
{
    private static readonly DateTimeOffset OriginalUpdatedAt =
        new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetInventoryAsync_WithEmptyProductVariantId_ReturnsValidationFailure()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.GetInventoryAsync(Guid.Empty, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("productVariantId", result.Error.ValidationErrors!.Keys);
        Assert.Equal(0, store.GetByProductVariantIdCalls);
    }

    [Fact]
    public async Task GetInventoryAsync_WithMissingInventory_ReturnsNotFound()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.GetInventoryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVENTORY_NOT_FOUND", result.Error!.Code);
        Assert.Equal(1, store.GetByProductVariantIdCalls);
    }

    [Fact]
    public async Task GetInventoryAsync_WithExistingInventory_ReturnsMappedInventory()
    {
        var inventory = CreateInventory(onHandQuantity: 10, reservedQuantity: 3);
        var store = new FakeInventoryStore
        {
            Inventory = inventory
        };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.GetInventoryAsync(
            inventory.ProductVariantId,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(inventory.ProductVariantId, result.Value!.ProductVariantId);
        Assert.Equal(10, result.Value.OnHandQuantity);
        Assert.Equal(3, result.Value.ReservedQuantity);
        Assert.Equal(7, result.Value.AvailableQuantity);
        Assert.Equal(OriginalUpdatedAt, result.Value.UpdatedAt);
    }

    [Fact]
    public async Task ListTransactionsAsync_WithInvalidPagination_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.ListTransactionsAsync(
            Guid.NewGuid(),
            new InventoryTransactionListRequest(Page: 0),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.ExistsCalls);
        Assert.Equal(0, store.ListTransactionsCalls);
    }

    [Fact]
    public async Task ListTransactionsAsync_WithEmptyProductVariantId_ReturnsValidationFailureWithoutStoreQuery()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.ListTransactionsAsync(
            Guid.Empty,
            new InventoryTransactionListRequest(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(0, store.ExistsCalls);
        Assert.Equal(0, store.ListTransactionsCalls);
    }

    [Fact]
    public async Task ListTransactionsAsync_WithMissingInventory_ReturnsNotFoundWithoutHistoryQuery()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.ListTransactionsAsync(
            Guid.NewGuid(),
            new InventoryTransactionListRequest(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVENTORY_NOT_FOUND", result.Error!.Code);
        Assert.Equal(1, store.ExistsCalls);
        Assert.Equal(0, store.ListTransactionsCalls);
    }

    [Fact]
    public async Task ListTransactionsAsync_WithExistingInventory_ReturnsPagedHistory()
    {
        var productVariantId = Guid.NewGuid();
        var transaction = CreateTransaction(productVariantId, 3, "Stock correction");
        var history = new PagedResult<InventoryTransactionDto>(
            [ToDto(transaction)],
            2,
            10,
            11,
            2);
        var store = new FakeInventoryStore
        {
            InventoryExists = true,
            History = history
        };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.ListTransactionsAsync(
            productVariantId,
            new InventoryTransactionListRequest(Page: 2, PageSize: 10),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(history, result.Value);
        Assert.Equal(1, store.ExistsCalls);
        Assert.Equal(1, store.ListTransactionsCalls);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WithEmptyProductVariantId_ReturnsValidationFailureWithoutStoreEffects()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            Guid.Empty,
            new AdjustInventoryRequest(1, "Stock correction"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("productVariantId", result.Error.ValidationErrors!.Keys);
        Assert.Equal(0, store.GetByProductVariantIdCalls);
        Assert.Empty(store.AddedTransactions);
        Assert.Equal(0, store.SaveChangesCalls);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WithZeroDelta_ReturnsValidationFailureWithoutEffects()
    {
        var inventory = CreateInventory(onHandQuantity: 10);
        var store = new FakeInventoryStore { Inventory = inventory };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            inventory.ProductVariantId,
            new AdjustInventoryRequest(0, "Stock correction"),
            CancellationToken.None);

        AssertValidationFailureWithoutEffects(result, store, inventory, 10);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AdjustInventoryAsync_WithBlankReason_ReturnsValidationFailureWithoutEffects(
        string? reason)
    {
        var inventory = CreateInventory(onHandQuantity: 10);
        var store = new FakeInventoryStore { Inventory = inventory };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            inventory.ProductVariantId,
            new AdjustInventoryRequest(1, reason),
            CancellationToken.None);

        AssertValidationFailureWithoutEffects(result, store, inventory, 10);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WithOverlongReason_ReturnsValidationFailureWithoutEffects()
    {
        var inventory = CreateInventory(onHandQuantity: 10);
        var store = new FakeInventoryStore { Inventory = inventory };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            inventory.ProductVariantId,
            new AdjustInventoryRequest(
                1,
                new string('a', InventoryTransaction.MaximumReasonLength + 1)),
            CancellationToken.None);

        AssertValidationFailureWithoutEffects(result, store, inventory, 10);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WithMissingInventory_ReturnsNotFound()
    {
        var store = new FakeInventoryStore();
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            Guid.NewGuid(),
            new AdjustInventoryRequest(1, "Stock correction"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVENTORY_NOT_FOUND", result.Error!.Code);
        Assert.Equal(1, store.GetByProductVariantIdCalls);
        Assert.Empty(store.AddedTransactions);
        Assert.Equal(0, store.SaveChangesCalls);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WhenResultWouldBeNegative_ReturnsInvariantViolationWithoutEffects()
    {
        var inventory = CreateInventory(onHandQuantity: 10);
        var store = new FakeInventoryStore { Inventory = inventory };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            inventory.ProductVariantId,
            new AdjustInventoryRequest(-11, "Stock correction"),
            CancellationToken.None);

        AssertInvariantFailureWithoutEffects(result, store, inventory, 10, 0);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WhenResultWouldBeBelowReserved_ReturnsInvariantViolationWithoutEffects()
    {
        var inventory = CreateInventory(onHandQuantity: 10, reservedQuantity: 4);
        var store = new FakeInventoryStore { Inventory = inventory };
        var service = new InventoryService(store, new FixedClock());

        var result = await service.AdjustInventoryAsync(
            inventory.ProductVariantId,
            new AdjustInventoryRequest(-7, "Stock correction"),
            CancellationToken.None);

        AssertInvariantFailureWithoutEffects(result, store, inventory, 10, 4);
    }

    [Fact]
    public async Task AdjustInventoryAsync_WithValidRequest_ChangesStateAddsAuditAndSaves()
    {
        const int quantityChange = -3;
        const string expectedTrimmedReason = "Stock correction";
        var inventory = CreateInventory(onHandQuantity: 10, reservedQuantity: 2);
        var store = new FakeInventoryStore { Inventory = inventory };
        var clock = new FixedClock();
        var service = new InventoryService(store, clock);

        var result = await service.AdjustInventoryAsync(
            inventory.ProductVariantId,
            new AdjustInventoryRequest(quantityChange, "  Stock correction  "),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, inventory.OnHandQuantity);
        Assert.Equal(2, inventory.ReservedQuantity);
        Assert.Equal(5, result.Value!.AvailableQuantity);
        Assert.Equal(clock.UtcNow, inventory.UpdatedAt);
        Assert.Equal(1, store.GetByProductVariantIdCalls);
        Assert.Equal(1, store.SaveChangesCalls);

        var audit = Assert.Single(store.AddedTransactions);
        Assert.Equal(InventoryTransactionType.Adjustment, audit.Type);
        Assert.Equal(quantityChange, audit.OnHandQuantityDelta);
        Assert.Equal(0, audit.ReservedQuantityDelta);
        Assert.Null(audit.ReferenceType);
        Assert.Null(audit.ReferenceId);
        Assert.Equal(expectedTrimmedReason, audit.Reason);
        Assert.Equal(clock.UtcNow, audit.CreatedAt);
    }

    private static void AssertValidationFailureWithoutEffects(
        ApplicationResult<InventoryDto> result,
        FakeInventoryStore store,
        Inventory inventory,
        int expectedOnHand)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Equal(expectedOnHand, inventory.OnHandQuantity);
        Assert.Empty(store.AddedTransactions);
        Assert.Equal(0, store.SaveChangesCalls);
        Assert.Equal(0, store.GetByProductVariantIdCalls);
    }

    private static void AssertInvariantFailureWithoutEffects(
        ApplicationResult<InventoryDto> result,
        FakeInventoryStore store,
        Inventory inventory,
        int expectedOnHand,
        int expectedReserved)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal("INVENTORY_INVARIANT_VIOLATION", result.Error!.Code);
        Assert.Equal(expectedOnHand, inventory.OnHandQuantity);
        Assert.Equal(expectedReserved, inventory.ReservedQuantity);
        Assert.Equal(1, store.GetByProductVariantIdCalls);
        Assert.Empty(store.AddedTransactions);
        Assert.Equal(0, store.SaveChangesCalls);
    }

    private static Inventory CreateInventory(int onHandQuantity, int reservedQuantity = 0)
    {
        var inventory = new Inventory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            onHandQuantity,
            OriginalUpdatedAt);

        if (reservedQuantity > 0)
        {
            typeof(Inventory)
                .GetProperty(nameof(Inventory.ReservedQuantity))!
                .SetValue(inventory, reservedQuantity);
        }

        return inventory;
    }

    private static InventoryTransaction CreateTransaction(
        Guid productVariantId,
        int quantityChange,
        string reason) =>
        new(
            Guid.NewGuid(),
            productVariantId,
            InventoryTransactionType.Adjustment,
            quantityChange,
            0,
            null,
            null,
            reason,
            new FixedClock().UtcNow);

    private static InventoryTransactionDto ToDto(InventoryTransaction transaction) =>
        new(
            transaction.Id,
            transaction.ProductVariantId,
            transaction.Type,
            transaction.OnHandQuantityDelta,
            transaction.ReservedQuantityDelta,
            transaction.ReferenceType,
            transaction.ReferenceId,
            transaction.Reason,
            transaction.CreatedAt);

    private sealed class FakeInventoryStore : IInventoryStore
    {
        public Inventory? Inventory { get; set; }

        public List<InventoryTransaction> AddedTransactions { get; } = [];

        public int SaveChangesCalls { get; private set; }

        public bool InventoryExists { get; set; }

        public PagedResult<InventoryTransactionDto> History { get; set; } =
            new([], 1, 20, 0, 0);

        public int GetByProductVariantIdCalls { get; private set; }

        public int ExistsCalls { get; private set; }

        public int ListTransactionsCalls { get; private set; }

        public Task<Inventory?> GetByProductVariantIdAsync(
            Guid productVariantId,
            CancellationToken cancellationToken)
        {
            GetByProductVariantIdCalls++;
            return Task.FromResult(Inventory);
        }

        public Task<bool> ExistsByProductVariantIdAsync(
            Guid productVariantId,
            CancellationToken cancellationToken)
        {
            ExistsCalls++;
            return Task.FromResult(InventoryExists);
        }

        public Task<PagedResult<InventoryTransactionDto>> ListTransactionsAsync(
            Guid productVariantId,
            InventoryTransactionListRequest request,
            CancellationToken cancellationToken)
        {
            ListTransactionsCalls++;
            return Task.FromResult(History);
        }

        public void AddTransaction(InventoryTransaction transaction) =>
            AddedTransactions.Add(transaction);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
    }
}

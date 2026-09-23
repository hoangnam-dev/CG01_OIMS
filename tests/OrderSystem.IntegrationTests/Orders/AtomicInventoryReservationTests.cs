using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Orders;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class AtomicInventoryReservationTests(PostgreSqlFixture postgres)
{
  private static readonly DateTimeOffset FixedNow = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
  [Fact]
  [Trait("Requirement", "DB-CON-001")]
  public async Task TryReserveAsync_StockOneWithTwoConcurrentTransactions_ReturnsOneReservedAndOneInsufficientStock()
  {
    // Arrange
    await using var factory = CreateFactory();
    var productVariantId = await SeedInventoryAsync(
      factory,
      onHandQuantity: 1
    );

    var hook = new ControllableOperationHook(
      OrderOperationCheckpoints.BeforeInventoryReservation,
      expectedParticipants: 2
    );

    var firstActor = ReserveFromIndependentScopeAsync(
      factory,
      hook,
      productVariantId,
      FixedNow
    );

    var secondActor = ReserveFromIndependentScopeAsync(
      factory,
      hook,
      productVariantId,
      FixedNow
    );

    var actors = Task.WhenAll(firstActor, secondActor);

    var firstSignal = await Task.WhenAny(
        hook.Reached,
        actors,
        Task.Delay(TimeSpan.FromSeconds(10)));

    if (firstSignal == actors)
    {
      await actors;
    }

    if (firstSignal != hook.Reached)
    {
      throw new TimeoutException(
          "Both actors did not reach the reservation checkpoint within 10 seconds.");
    }

    hook.Release();

    var results = await actors.WaitAsync(TimeSpan.FromSeconds(15));

    // Assert: response/result of each concurrent actor
    Assert.Single(results, result => result == InventoryReservationResult.Reserved);
    Assert.Single(results, result => result == InventoryReservationResult.InsufficientStock);

    // Assert: durable state, from a brand-new DbContext
    using var assertionScope = factory.Services.CreateScope();
    var assertionDbContext = assertionScope.ServiceProvider
                            .GetRequiredService<OrderSystemDbContext>();

    var inventory = await assertionDbContext.Inventories
                          .AsNoTracking()
                          .SingleAsync(item => item.ProductVariantId == productVariantId);

    Assert.Equal(1, inventory.OnHandQuantity);
    Assert.Equal(1, inventory.ReservedQuantity);
    Assert.Equal(0, inventory.AvailableQuantity);
  }

  private static async Task<InventoryReservationResult> ReserveFromIndependentScopeAsync(
    WebApplicationFactory<Program> factory,
    ControllableOperationHook hook,
    Guid productVariantId,
    DateTimeOffset now)
  {
    using var scope = factory.Services.CreateScope();

    var store = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

    await using var transaction = await store.BeginTransactionAsync(
        CancellationToken.None);

    await hook.ReachAsync(
        OrderOperationCheckpoints.BeforeInventoryReservation,
        CancellationToken.None);

    var result = await store.TryReserveAsync(
        productVariantId,
        quantity: 1,
        now,
        CancellationToken.None);

    if (result == InventoryReservationResult.Reserved)
    {
      await transaction.CommitAsync(CancellationToken.None);
    }
    else
    {
      await transaction.RollbackAsync(CancellationToken.None);
    }

    return result;
  }

  private static async Task<Guid> SeedInventoryAsync(
      WebApplicationFactory<Program> factory,
      int onHandQuantity)
  {
    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

    await dbContext.Database.MigrateAsync();

    var now = DateTimeOffset.UtcNow;
    var product = new Product(
        Guid.NewGuid(),
        $"Atomic reservation product {Guid.NewGuid():N}",
        "Integration test product",
        CatalogStatus.Active,
        now);

    var productVariant = new ProductVariant(
        Guid.NewGuid(),
        product.Id,
        $"RES-{Guid.NewGuid():N}"[..16],
        "Atomic reservation variant",
        10m,
        CatalogStatus.Active,
        now);

    var inventory = new Inventory(
        Guid.NewGuid(),
        productVariant.Id,
        onHandQuantity,
        now);

    dbContext.AddRange(product, productVariant, inventory);
    await dbContext.SaveChangesAsync();

    return productVariant.Id;
  }

  private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));
}
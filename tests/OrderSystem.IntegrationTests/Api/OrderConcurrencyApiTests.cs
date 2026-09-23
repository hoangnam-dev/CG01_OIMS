using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderConcurrencyApiTests(PostgreSqlFixture postgres)
{
  [Fact]
  [Trait("Requirement", "API-CON-001")]
  public async Task CreateOrder_StockOneWithTwoConcurrentRequests_OnlyOneSucceeds()
  {
    // Arrange
    var hook = new ControllableOperationHook(
      OrderOperationCheckpoints.BeforeInventoryReservation,
      expectedParticipants: 2
    );
    await using var factory = CreateFactory(hook);
    var productVariantId = await SeedInventoryAsync(
      factory,
      onHandQuantity: 1
    );
    using var firstClient = factory.CreateClient();
    using var secondClient = factory.CreateClient();
    await RegisterAndAuthenticateCustomerAsync(firstClient);
    await RegisterAndAuthenticateCustomerAsync(secondClient);

    using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "api/orders")
    {
      Content = JsonContent.Create(new
      {
        items = new[]
        {
          new {productVariantId, quantity = 1}
        }
      })
    };
    firstRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

    using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "api/orders")
    {
      Content = JsonContent.Create(new
      {
        items = new[]
        {
          new {productVariantId, quantity = 1}
        }
      })
    };
    secondRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

    // Act
    var responses = await SendAtCheckpointAsync(
      hook,
      OrderOperationCheckpoints.BeforeInventoryReservation,
      firstClient.SendAsync(firstRequest),
      secondClient.SendAsync(secondRequest));

    // Assert HTTP
    var createdResponses = responses
      .Where(response => response.StatusCode == HttpStatusCode.Created)
      .ToList();

    var conflictResponses = responses
      .Where(response => response.StatusCode == HttpStatusCode.Conflict)
      .ToList();
    Assert.Single(createdResponses);
    var conflictResponse = Assert.Single(conflictResponses);
    using (var conflictDocument = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync()))
    {
      Assert.Equal(
        "INSUFFICIENT_STOCK",
        conflictDocument.RootElement.GetProperty("code").GetString());
    }

    // Assert durable database state using a fresh scope / DbContext
    using var assertionScope = factory.Services.CreateScope();
    var assertionDbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

    var inventory = await assertionDbContext.Inventories
      .AsNoTracking()
      .SingleAsync(inventory => inventory.ProductVariantId == productVariantId);
      
    Assert.Equal(1, inventory.OnHandQuantity);
    Assert.Equal(1, inventory.ReservedQuantity);
    Assert.Equal(0, inventory.AvailableQuantity);

    var orderItems = await assertionDbContext.OrderItems
      .AsNoTracking()
      .Where(item => item.ProductVariantId == productVariantId)
      .ToListAsync();

    var orderItem = Assert.Single(orderItems);

    Assert.Equal(1, orderItem.Quantity);

    var order = await assertionDbContext.Orders
      .AsNoTracking()
      .SingleAsync(order => order.Id == orderItem.OrderId);

    Assert.Equal(OrderStatus.PendingPayment, order.Status);

    var reserveLedgers = await assertionDbContext.InventoryTransactions
    .AsNoTracking()
    .Where(transaction =>
        transaction.ProductVariantId == productVariantId &&
        transaction.Type == InventoryTransactionType.Reserve &&
        transaction.ReferenceType == InventoryReferenceType.Order)
    .ToListAsync();

    var reserveLedger = Assert.Single(reserveLedgers);

    Assert.Equal(0, reserveLedger.OnHandQuantityDelta);
    Assert.Equal(1, reserveLedger.ReservedQuantityDelta);
    Assert.True(reserveLedger.ReferenceId.HasValue);
    Assert.Equal(order.Id, reserveLedger.ReferenceId.Value);
  }

  [Fact]
  public async Task CreateOrder_TwoVariantsInOppositeInputOrder_CompletesWithoutDeadlock()
  {
    // Arrange
    var hook = new ControllableOperationHook(
      OrderOperationCheckpoints.BeforeInventoryReservation,
      expectedParticipants: 2);
    await using var factory = CreateFactory(hook);
    var firstVariantId = await SeedInventoryAsync(factory, onHandQuantity: 2);
    var secondVariantId = await SeedInventoryAsync(factory, onHandQuantity: 2);
    using var firstClient = factory.CreateClient();
    using var secondClient = factory.CreateClient();
    await RegisterAndAuthenticateCustomerAsync(firstClient);
    await RegisterAndAuthenticateCustomerAsync(secondClient);
    using var firstRequest = CreateOrderRequest(secondVariantId, firstVariantId);
    using var secondRequest = CreateOrderRequest(firstVariantId, secondVariantId);

    // Act
    var responses = await SendAtCheckpointAsync(
      hook,
      OrderOperationCheckpoints.BeforeInventoryReservation,
      firstClient.SendAsync(firstRequest),
      secondClient.SendAsync(secondRequest));

    // Assert HTTP
    Assert.Equal(2, responses.Count(response => response.StatusCode == HttpStatusCode.Created));

    // Assert durable state using a fresh DbContext.
    using var assertionScope = factory.Services.CreateScope();
    var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
    var variantIds = new[] { firstVariantId, secondVariantId };
    var inventories = await dbContext.Inventories
      .AsNoTracking()
      .Where(inventory => variantIds.Contains(inventory.ProductVariantId))
      .ToListAsync();

    Assert.Equal(2, inventories.Count);
    Assert.All(inventories, inventory =>
    {
      Assert.Equal(2, inventory.OnHandQuantity);
      Assert.Equal(2, inventory.ReservedQuantity);
      Assert.Equal(0, inventory.AvailableQuantity);
    });

    var orderItems = await dbContext.OrderItems
      .AsNoTracking()
      .Where(item => variantIds.Contains(item.ProductVariantId))
      .ToListAsync();

    Assert.Equal(4, orderItems.Count);
    Assert.Equal(2, orderItems.Select(item => item.OrderId).Distinct().Count());
    Assert.All(orderItems, item => Assert.Equal(1, item.Quantity));
    Assert.Equal(2, orderItems.Count(item => item.ProductVariantId == firstVariantId));
    Assert.Equal(2, orderItems.Count(item => item.ProductVariantId == secondVariantId));

    var reserveLedgers = await dbContext.InventoryTransactions
      .AsNoTracking()
      .Where(transaction =>
        variantIds.Contains(transaction.ProductVariantId) &&
        transaction.Type == InventoryTransactionType.Reserve &&
        transaction.ReferenceType == InventoryReferenceType.Order)
      .ToListAsync();

    Assert.Equal(4, reserveLedgers.Count);
    Assert.All(reserveLedgers, ledger =>
    {
      Assert.Equal(0, ledger.OnHandQuantityDelta);
      Assert.Equal(1, ledger.ReservedQuantityDelta);
    });
  }

  [Fact]
  public async Task CancelOrder_TwoConcurrentRequests_ReleasesReservationExactlyOnce()
  {
    // Arrange
    var hook = new ControllableOperationHook(
      OrderOperationCheckpoints.BeforeCancellationLock,
      expectedParticipants: 2);
    await using var factory = CreateFactory(hook);
    var seededOrder = await SeedReservedPendingOrderAsync(factory);
    using var firstClient = factory.CreateClient();
    using var secondClient = factory.CreateClient();
    await LoginAndSetBearerAsync(firstClient, seededOrder.Email, TestCredentials.ValidPassword);
    await LoginAndSetBearerAsync(secondClient, seededOrder.Email, TestCredentials.ValidPassword);
    using var firstRequest = new HttpRequestMessage(
      HttpMethod.Post,
      $"/api/orders/{seededOrder.OrderId}/cancel");
    using var secondRequest = new HttpRequestMessage(
      HttpMethod.Post,
      $"/api/orders/{seededOrder.OrderId}/cancel");

    // Act
    var responses = await SendAtCheckpointAsync(
      hook,
      OrderOperationCheckpoints.BeforeCancellationLock,
      firstClient.SendAsync(firstRequest),
      secondClient.SendAsync(secondRequest));

    // Assert HTTP
    Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
    var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    using (var conflictDocument = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
    {
      Assert.Equal(
        "ORDER_NOT_CANCELLABLE",
        conflictDocument.RootElement.GetProperty("code").GetString());
    }

    // Assert durable state using a fresh DbContext.
    using var assertionScope = factory.Services.CreateScope();
    var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
    var order = await dbContext.Orders
      .AsNoTracking()
      .SingleAsync(order => order.Id == seededOrder.OrderId);
    var inventory = await dbContext.Inventories
      .AsNoTracking()
      .SingleAsync(inventory => inventory.ProductVariantId == seededOrder.ProductVariantId);
    var releaseLedgers = await dbContext.InventoryTransactions
      .AsNoTracking()
      .Where(transaction =>
        transaction.ProductVariantId == seededOrder.ProductVariantId &&
        transaction.Type == InventoryTransactionType.Release &&
        transaction.ReferenceType == InventoryReferenceType.Order &&
        transaction.ReferenceId == seededOrder.OrderId)
      .ToListAsync();

    Assert.Equal(OrderStatus.Cancelled, order.Status);
    Assert.Equal(1, inventory.OnHandQuantity);
    Assert.Equal(0, inventory.ReservedQuantity);
    Assert.Equal(1, inventory.AvailableQuantity);

    var releaseLedger = Assert.Single(releaseLedgers);
    Assert.Equal(0, releaseLedger.OnHandQuantityDelta);
    Assert.Equal(-1, releaseLedger.ReservedQuantityDelta);
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

  private static async Task RegisterAndAuthenticateCustomerAsync(HttpClient client)
  {
    var email = $"concurrency-{Guid.NewGuid():N}@example.com";
    using var registerResponse = await client.PostAsJsonAsync(
      "api/auth/register",
      new
      {
        email,
        password = TestCredentials.ValidPassword
      }
    );
    registerResponse.EnsureSuccessStatusCode();

    await LoginAndSetBearerAsync(client, email, TestCredentials.ValidPassword);
  }

  private static HttpRequestMessage CreateOrderRequest(params Guid[] productVariantIds)
  {
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
    {
      Content = JsonContent.Create(new
      {
        items = productVariantIds.Select(productVariantId => new { productVariantId, quantity = 1 })
      })
    };
    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    return request;
  }

  private static async Task<HttpResponseMessage[]> SendAtCheckpointAsync(
    ControllableOperationHook hook,
    string checkpoint,
    Task<HttpResponseMessage> firstRequest,
    Task<HttpResponseMessage> secondRequest)
  {
    Task<HttpResponseMessage[]> requestsCompleted = Task.WhenAll(firstRequest, secondRequest);

    try
    {
      var timeout = Task.Delay(TimeSpan.FromSeconds(10));
      var firstSignal = await Task.WhenAny(hook.Reached, requestsCompleted, timeout);

      if (firstSignal == timeout)
      {
        throw new TimeoutException(
          $"The two requests did not reach {checkpoint} within 10 seconds.");
      }

      if (firstSignal == requestsCompleted)
      {
        var responses = await requestsCompleted;
        throw new Xunit.Sdk.XunitException(
          "Both HTTP requests completed before reaching their synchronization checkpoint. " +
          $"Statuses: {string.Join(", ", responses.Select(response => (int)response.StatusCode))}.");
      }

      hook.Release();
      return await requestsCompleted.WaitAsync(TimeSpan.FromSeconds(15));
    }
    finally
    {
      hook.Release();
    }
  }

  private static async Task<SeededOrder> SeedReservedPendingOrderAsync(
    WebApplicationFactory<Program> factory)
  {
    using var setupScope = factory.Services.CreateScope();
    var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
    var passwordHasher = setupScope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    await dbContext.Database.MigrateAsync();

    var now = DateTimeOffset.UtcNow;
    var ownerId = Guid.NewGuid();
    var orderId = Guid.NewGuid();
    var productId = Guid.NewGuid();
    var productVariantId = Guid.NewGuid();
    var email = $"cancel-race-{Guid.NewGuid():N}@example.com";
    var product = new Product(productId, "Cancellation race product", "Integration test product", CatalogStatus.Active, now);
    var productVariant = new ProductVariant(productVariantId, productId, $"CAN-{Guid.NewGuid():N}"[..16], "Cancellation race variant", 10m, CatalogStatus.Active, now);
    var order = new Order(orderId, ownerId, 10m, now.AddMinutes(15), now);

    dbContext.AddRange(
      new User(ownerId, email, email, passwordHasher.Hash(TestCredentials.ValidPassword), UserRole.Customer, now),
      product,
      productVariant,
      new Inventory(Guid.NewGuid(), productVariantId, initialOnHand: 1, updatedAt: now),
      order,
      new OrderItem(Guid.NewGuid(), orderId, productVariantId, quantity: 1, unitPrice: 10m),
      new InventoryTransaction(
        Guid.NewGuid(),
        productVariantId,
        InventoryTransactionType.Reserve,
        onHandQuantityDelta: 0,
        reservedQuantityDelta: 1,
        referenceType: InventoryReferenceType.Order,
        referenceId: orderId,
        reason: null,
        createdAt: now));
    await dbContext.SaveChangesAsync();

    using var reservationScope = factory.Services.CreateScope();
    var store = reservationScope.ServiceProvider.GetRequiredService<IOrderCommandStore>();
    await using var transaction = await store.BeginTransactionAsync(CancellationToken.None);
    var result = await store.TryReserveAsync(productVariantId, quantity: 1, now, CancellationToken.None);
    Assert.Equal(InventoryReservationResult.Reserved, result);
    await transaction.CommitAsync(CancellationToken.None);

    return new SeededOrder(orderId, productVariantId, email);
  }

  private static async Task LoginAndSetBearerAsync(HttpClient client, string email, string password)
  {
    using var loginResponse = await client.PostAsJsonAsync(
      "/api/auth/login",
      new { email, password });
    loginResponse.EnsureSuccessStatusCode();

    using var document = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
    var token = document.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
  }

  private WebApplicationFactory<Program> CreateFactory(ControllableOperationHook hook) =>
    new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
      builder.ConfigureAppConfiguration((_, configuration) =>
        configuration.AddOimsTestConfiguration(
          new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString)));
      builder.ConfigureServices(services =>
      {
        services.RemoveAll<IOperationHook>();
        services.AddSingleton<IOperationHook>(hook);
      });
    });

  private sealed record SeededOrder(Guid OrderId, Guid ProductVariantId, string Email);
}

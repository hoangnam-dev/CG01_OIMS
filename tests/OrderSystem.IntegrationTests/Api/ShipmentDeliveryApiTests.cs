using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Orders;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Shipments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ShipmentDeliveryApiTests(PostgreSqlFixture postgres)
{
    [Theory]
    [Trait("Requirement", "API-SHIP-009")]
    [InlineData("start-picking", false)]
    [InlineData("pack", false)]
    [InlineData("ship", false)]
    [InlineData("out-for-delivery", false)]
    [InlineData("mark-delivered", false)]
    [InlineData("delivery-failed", true)]
    [InlineData("start-return", false)]
    [InlineData("mark-returned", false)]
    [InlineData("restock", false)]
    public async Task ShipmentCommands_AuthenticatedCustomer_ReturnsForbidden(
    string command,
    bool requiresReason)
    {
        await using var factory = CreateFactory();
        var customerEmail = await SeedCustomerEmailAsync(factory);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customerEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{Guid.NewGuid()}/{command}",
            requiresReason
                ? JsonContent.Create(new { reason = "Recipient unavailable" })
                : null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-005")]
    public async Task StartDelivery_AdminMovesShippedShipmentWithoutMutatingInventory()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedShippedShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/out-for-delivery",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("OutForDelivery", responseData.GetProperty("status").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProductVariantId == seeded.ProductVariantId);

        var issueTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.OutForDeliveryStarted);

        Assert.Equal(ShipmentStatus.OutForDelivery, shipment.Status);
        Assert.Null(shipment.FailureReason);

        Assert.Equal(8, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        Assert.Single(issueTransactions);
        Assert.Equal(-2, issueTransactions[0].OnHandQuantityDelta);
        Assert.Equal(-2, issueTransactions[0].ReservedQuantityDelta);

        Assert.Equal(ShipmentStatus.Shipped, activity.FromStatus);
        Assert.Equal(ShipmentStatus.OutForDelivery, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-005")]
    public async Task MarkDeliveryFailed_AdminStoresReasonWithoutMutatingInventory()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedOutForDeliveryShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsJsonAsync(
            $"/api/shipments/{seeded.ShipmentId}/delivery-failed",
            new { reason = "  Recipient unavailable  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("DeliveryFailed", responseData.GetProperty("status").GetString());
        Assert.Equal("Recipient unavailable", responseData.GetProperty("failureReason").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProductVariantId == seeded.ProductVariantId);

        var shipmentTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.DeliveryFailed);

        Assert.Equal(ShipmentStatus.DeliveryFailed, shipment.Status);
        Assert.Equal("Recipient unavailable", shipment.FailureReason);

        Assert.Equal(8, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        var issue = Assert.Single(shipmentTransactions);
        Assert.Equal(InventoryTransactionType.Issue, issue.Type);
        Assert.Equal(-2, issue.OnHandQuantityDelta);
        Assert.Equal(-2, issue.ReservedQuantityDelta);

        Assert.Equal(ShipmentStatus.OutForDelivery, activity.FromStatus);
        Assert.Equal(ShipmentStatus.DeliveryFailed, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Equal("Recipient unavailable", activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-006")]
    public async Task MarkDelivered_AdminCompletesOrderWithoutMutatingInventoryAgain()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedOutForDeliveryShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/mark-delivered",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("Delivered", responseData.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            responseData.GetProperty("deliveredAt").GetString()));

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProductVariantId == seeded.ProductVariantId);

        var issueTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        var orderHistory = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.OrderId == seeded.OrderId &&
                candidate.FromStatus == OrderStatus.Processing &&
                candidate.ToStatus == OrderStatus.Completed &&
                candidate.ReasonCode == OrderStatusReasonCode.ShipmentDelivered);

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.FromStatus == ShipmentStatus.OutForDelivery &&
                candidate.ToStatus == ShipmentStatus.Delivered);

        Assert.Equal(ShipmentStatus.Delivered, shipment.Status);
        Assert.NotNull(shipment.DeliveredAt);
        Assert.Null(shipment.FailureReason);

        Assert.Equal(OrderStatus.Completed, order.Status);

        Assert.Equal(8, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        var issue = Assert.Single(issueTransactions);
        Assert.Equal(-2, issue.OnHandQuantityDelta);
        Assert.Equal(-2, issue.ReservedQuantityDelta);

        Assert.Equal(OrderStatusHistoryActorType.Admin, orderHistory.ActorType);
        Assert.Equal(seeded.AdminId, orderHistory.ActorUserId);
        Assert.Null(orderHistory.Reason);

        Assert.Equal("Delivered", activity.ActivityType.ToString());
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-007")]
    public async Task StartReturn_AdminMovesDeliveryFailedShipmentWithoutMutatingOrderOrInventory()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedDeliveryFailedShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/start-return",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("Returning", responseData.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, responseData.GetProperty("failureReason").ValueKind);

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.ProductVariantId == seeded.ProductVariantId);

        var issueTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        var orderHistories = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == seeded.OrderId)
            .ToListAsync();

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.ReturnStarted);

        Assert.Equal(ShipmentStatus.Returning, shipment.Status);
        Assert.Null(shipment.FailureReason);

        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Empty(orderHistories);

        Assert.Equal(8, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        var issue = Assert.Single(issueTransactions);
        Assert.Equal(-2, issue.OnHandQuantityDelta);
        Assert.Equal(-2, issue.ReservedQuantityDelta);

        Assert.Equal(ShipmentStatus.DeliveryFailed, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Returning, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-007")]
    public async Task MarkReturned_AdminFailsFulfillmentAndStartsRefundWithoutRestocking()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedReturningShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.Shipment.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.Shipment.ShipmentId}/mark-returned",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(
            seeded.Shipment.ShipmentId,
            responseData.GetProperty("id").GetGuid());
        Assert.Equal("Returned", responseData.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            responseData.GetProperty("returnedAt").GetString()));

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.ShipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.OrderId);

        var payment = await dbContext.Payments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.PaymentId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId == seeded.Shipment.ProductVariantId);

        var shipmentTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.Shipment.ShipmentId)
            .ToListAsync();

        var orderHistory = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.OrderId == seeded.Shipment.OrderId &&
                candidate.FromStatus == OrderStatus.Processing &&
                candidate.ToStatus == OrderStatus.FulfillmentFailed &&
                candidate.ReasonCode == OrderStatusReasonCode.ShipmentReturned);

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.Shipment.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.Returned);

        Assert.Equal(ShipmentStatus.Returned, shipment.Status);
        Assert.NotNull(shipment.ReturnedAt);
        Assert.Null(shipment.FailureReason);

        Assert.Equal(OrderStatus.FulfillmentFailed, order.Status);

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(
            $"fake-refund-{seeded.PaymentId:D}",
            payment.RefundIdempotencyKey);
        Assert.NotNull(payment.RefundRequestedAt);

        Assert.Equal(8, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        var issue = Assert.Single(shipmentTransactions);
        Assert.Equal(InventoryTransactionType.Issue, issue.Type);
        Assert.Equal(-2, issue.OnHandQuantityDelta);
        Assert.Equal(-2, issue.ReservedQuantityDelta);

        Assert.Equal(OrderStatusHistoryActorType.Admin, orderHistory.ActorType);
        Assert.Equal(seeded.Shipment.AdminId, orderHistory.ActorUserId);
        Assert.Null(orderHistory.Reason);

        Assert.Equal(ShipmentStatus.Returning, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Returned, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.Shipment.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-008")]
    public async Task Restock_AdminAddsOnHandAndReturnLedgerExactlyOnce()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedReturnedShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.Shipment.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.Shipment.ShipmentId}/restock",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(
            seeded.Shipment.ShipmentId,
            responseData.GetProperty("id").GetGuid());
        Assert.Equal("Returned", responseData.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            responseData.GetProperty("restockedAt").GetString()));

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.ShipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.OrderId);

        var payment = await dbContext.Payments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.PaymentId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId == seeded.Shipment.ProductVariantId);

        var shipmentTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.Shipment.ShipmentId)
            .ToListAsync();

        var orderHistories = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == seeded.Shipment.OrderId)
            .ToListAsync();

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.Shipment.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.Restocked);

        Assert.Equal(ShipmentStatus.Returned, shipment.Status);
        Assert.NotNull(shipment.RestockedAt);

        Assert.Equal(OrderStatus.FulfillmentFailed, order.Status);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);

        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        var issue = Assert.Single(
            shipmentTransactions,
            candidate => candidate.Type == InventoryTransactionType.Issue);
        Assert.Equal(-2, issue.OnHandQuantityDelta);
        Assert.Equal(-2, issue.ReservedQuantityDelta);

        var restock = Assert.Single(
            shipmentTransactions,
            candidate => candidate.Type == InventoryTransactionType.Return);
        Assert.Equal(2, restock.OnHandQuantityDelta);
        Assert.Equal(0, restock.ReservedQuantityDelta);

        Assert.Empty(orderHistories);

        Assert.Equal(ShipmentStatus.Returned, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Returned, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.Shipment.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-008")]
    public async Task Restock_AdminRepeatsCommand_ReturnsAlreadyRestockedWithoutSecondEffect()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedReturnedShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.Shipment.AdminEmail);

        using var firstResponse = await client.PostAsync(
            $"/api/shipments/{seeded.Shipment.ShipmentId}/restock",
            content: null);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var secondResponse = await client.PostAsync(
            $"/api/shipments/{seeded.Shipment.ShipmentId}/restock",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        using var problem = JsonDocument.Parse(
            await secondResponse.Content.ReadAsStringAsync());

        Assert.Equal(
            "SHIPMENT_ALREADY_RESTOCKED",
            problem.RootElement.GetProperty("code").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.ShipmentId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId == seeded.Shipment.ProductVariantId);

        var returnTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.Shipment.ShipmentId &&
                candidate.Type == InventoryTransactionType.Return)
            .ToListAsync();

        var restockActivities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ShipmentId == seeded.Shipment.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.Restocked)
            .ToListAsync();

        Assert.NotNull(shipment.RestockedAt);
        Assert.Equal(10, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);
        Assert.Single(returnTransactions);
        Assert.Single(restockActivities);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-003")]
    public async Task StartPicking_AdminMovesPendingShipmentWithoutMutatingInventory()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedPendingShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/start-picking",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("Picking", responseData.GetProperty("status").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var activities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate => candidate.ShipmentId == seeded.ShipmentId)
            .ToListAsync();

        var inventoryTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        var activity = Assert.Single(
            activities,
            candidate => candidate.ActivityType == ShipmentActivityType.PickingStarted);

        Assert.Equal(ShipmentStatus.Picking, shipment.Status);
        Assert.Null(shipment.ShippedAt);
        Assert.Empty(inventoryTransactions);

        Assert.Equal(ShipmentStatus.Pending, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Picking, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-003")]
    public async Task Pack_AdminMovesPickingShipmentWithoutMutatingInventory()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedPickingShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/pack",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("Packed", responseData.GetProperty("status").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var activities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate => candidate.ShipmentId == seeded.ShipmentId)
            .ToListAsync();

        var inventoryTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        var activity = Assert.Single(
            activities,
            candidate => candidate.ActivityType == ShipmentActivityType.Packed);

        Assert.Equal(ShipmentStatus.Packed, shipment.Status);
        Assert.Null(shipment.ShippedAt);
        Assert.Empty(inventoryTransactions);

        Assert.Equal(ShipmentStatus.Picking, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Packed, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-003")]
    public async Task StartPicking_RepeatedCommandReturnsConflictWithoutAdditionalActivityOrInventoryMutation()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedPendingShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var firstResponse = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/start-picking",
            content: null);

        using var repeatedResponse = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/start-picking",
            content: null);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeatedResponse.StatusCode);

        using var problem = JsonDocument.Parse(await repeatedResponse.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_SHIPMENT_STATUS", problem.RootElement.GetProperty("code").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var activities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.PickingStarted)
            .ToListAsync();

        var inventoryTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        Assert.Equal(ShipmentStatus.Picking, shipment.Status);
        Assert.Single(activities);
        Assert.Empty(inventoryTransactions);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-003")]
    public async Task Pack_FromPendingReturnsConflictWithoutActivityOrInventoryMutation()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedPendingShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/pack",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_SHIPMENT_STATUS", problem.RootElement.GetProperty("code").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var activities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate => candidate.ShipmentId == seeded.ShipmentId)
            .ToListAsync();

        var inventoryTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        Assert.Equal(ShipmentStatus.Pending, shipment.Status);
        Assert.Empty(activities);
        Assert.Empty(inventoryTransactions);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-003")]
    public async Task Pack_RepeatedCommandReturnsConflictWithoutAdditionalActivityOrInventoryMutation()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedPickingShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var firstResponse = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/pack",
            content: null);

        using var repeatedResponse = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/pack",
            content: null);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeatedResponse.StatusCode);

        using var problem = JsonDocument.Parse(await repeatedResponse.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_SHIPMENT_STATUS", problem.RootElement.GetProperty("code").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var activities = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .Where(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.Packed)
            .ToListAsync();

        var inventoryTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate =>
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId)
            .ToListAsync();

        Assert.Equal(ShipmentStatus.Packed, shipment.Status);
        Assert.Single(activities);
        Assert.Empty(inventoryTransactions);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-003")]
    public async Task Ship_AdminIssuesReservedInventoryExactlyOnceAndKeepsOrderProcessing()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedPackedShipmentAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/shipments/{seeded.ShipmentId}/ship",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        Assert.Equal(seeded.ShipmentId, responseData.GetProperty("id").GetGuid());
        Assert.Equal("Shipped", responseData.GetProperty("status").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);

        var inventory = await dbContext.Inventories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ProductVariantId == seeded.ProductVariantId);

        var issue = await dbContext.InventoryTransactions
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Type == InventoryTransactionType.Issue &&
                candidate.ReferenceType == InventoryReferenceType.Shipment &&
                candidate.ReferenceId == seeded.ShipmentId);

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == seeded.ShipmentId &&
                candidate.ActivityType == ShipmentActivityType.Shipped);

        Assert.Equal(ShipmentStatus.Shipped, shipment.Status);
        Assert.NotNull(shipment.ShippedAt);
        Assert.Equal(OrderStatus.Processing, order.Status);

        Assert.Equal(8, inventory.OnHandQuantity);
        Assert.Equal(0, inventory.ReservedQuantity);

        Assert.Equal(seeded.ProductVariantId, issue.ProductVariantId);
        Assert.Equal(-2, issue.OnHandQuantityDelta);
        Assert.Equal(-2, issue.ReservedQuantityDelta);

        Assert.Equal(ShipmentStatus.Packed, activity.FromStatus);
        Assert.Equal(ShipmentStatus.Shipped, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
        Assert.Null(activity.Reason);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-001")]
    public async Task CreateShipment_AdminCreatesPendingShipmentAndStartsProcessing()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedConfirmedOrderAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsync(
            $"/api/orders/{seeded.OrderId}/shipment",
            content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var responseData = document.RootElement.GetProperty("data");
        var shipmentId = responseData.GetProperty("id").GetGuid();

        Assert.NotEqual(Guid.Empty, shipmentId);
        Assert.Equal(seeded.OrderId, responseData.GetProperty("orderId").GetGuid());
        Assert.Equal("Pending", responseData.GetProperty("status").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == shipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);

        var history = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.OrderId == seeded.OrderId &&
                candidate.ReasonCode == OrderStatusReasonCode.ShipmentCreated);

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.ShipmentId == shipmentId &&
                candidate.ActivityType == ShipmentActivityType.Created);

        Assert.Equal(seeded.OrderId, shipment.OrderId);
        Assert.Equal(ShipmentStatus.Pending, shipment.Status);
        Assert.Equal(OrderStatus.Processing, order.Status);

        Assert.Equal(OrderStatus.Confirmed, history.FromStatus);
        Assert.Equal(OrderStatus.Processing, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.Admin, history.ActorType);
        Assert.Equal(seeded.AdminId, history.ActorUserId);

        Assert.Null(activity.FromStatus);
        Assert.Equal(ShipmentStatus.Pending, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(seeded.AdminId, activity.ActorUserId);
    }

    [Fact]
    [Trait("Requirement", "API-SHIP-002")]
    public async Task CreateShipment_ConcurrentRequests_CreateExactlyOneShipment()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedConfirmedOrderAsync(factory);

        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await AuthenticateAsync(firstClient, seeded.AdminEmail);
        await AuthenticateAsync(secondClient, seeded.AdminEmail);

        using var start = new Barrier(2);

        var firstTask = Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            return await firstClient.PostAsync($"/api/orders/{seeded.OrderId}/shipment", content: null);
        });

        var secondTask = Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            return await secondClient.PostAsync($"/api/orders/{seeded.OrderId}/shipment", content: null);
        });

        var responses = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipments = await dbContext.Shipments
            .AsNoTracking()
            .Where(shipment => shipment.OrderId == seeded.OrderId)
            .ToListAsync();

        var shipment = Assert.Single(shipments);
        Assert.Equal(ShipmentStatus.Pending, shipment.Status);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.OrderId);

        Assert.Equal(OrderStatus.Processing, order.Status);

        Assert.Single(
            await dbContext.OrderStatusHistories
                .AsNoTracking()
                .Where(history => history.OrderId == seeded.OrderId)
                .ToListAsync(),
            history => history.ReasonCode == OrderStatusReasonCode.ShipmentCreated);

        Assert.Single(
            await dbContext.ShipmentActivityHistories
                .AsNoTracking()
                .Where(activity => activity.ShipmentId == shipment.Id)
                .ToListAsync(),
            activity => activity.ActivityType == ShipmentActivityType.Created);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString))));

    private static async Task<SeededPendingShipment> SeedPendingShipmentAsync(
    WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.MigrateAsync();

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var adminEmail = $"shipment-picking-admin-{adminId:N}@example.com";

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 20m,
            reservationExpiresAt: createdAt.AddHours(1),
            createdAt: createdAt);

        order.Confirm(createdAt.AddMinutes(1));
        order.StartProcessing(createdAt.AddMinutes(2));

        var shipment = new Shipment(
            shipmentId,
            orderId,
            createdAt.AddMinutes(2));

        dbContext.AddRange(
            new User(
                ownerId,
                $"shipment-picking-owner-{ownerId:N}@example.com",
                $"shipment-picking-owner-{ownerId:N}@example.com",
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Customer,
                createdAt),
            new User(
                adminId,
                adminEmail,
                adminEmail,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                createdAt),
            order,
            shipment);

        await dbContext.SaveChangesAsync();

        return new SeededPendingShipment(shipmentId, adminId, adminEmail);
    }

    private sealed record SeededPendingShipment(
    Guid ShipmentId,
    Guid AdminId,
    string AdminEmail);


    private static async Task<SeededShipment> SeedPackedShipmentAsync(
    WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var orderStore = scope.ServiceProvider.GetRequiredService<IOrderCommandStore>();

        await dbContext.Database.MigrateAsync();

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        var adminEmail = $"shipment-ship-admin-{adminId:N}@example.com";

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 20m,
            reservationExpiresAt: createdAt.AddHours(1),
            createdAt: createdAt);

        order.Confirm(createdAt.AddMinutes(1));
        order.StartProcessing(createdAt.AddMinutes(2));

        var shipment = new Shipment(
            shipmentId,
            orderId,
            createdAt.AddMinutes(2));

        shipment.StartPicking(createdAt.AddMinutes(3));
        shipment.Pack(createdAt.AddMinutes(4));

        var product = new Product(
            Guid.NewGuid(),
            $"Shipment ship product {Guid.NewGuid():N}",
            "Shipment ship API integration test product",
            CatalogStatus.Active,
            createdAt);

        var variant = new ProductVariant(
            productVariantId,
            product.Id,
            $"SSA-{Guid.NewGuid():N}"[..16],
            "Shipment ship API variant",
            currentPrice: 10m,
            CatalogStatus.Active,
            createdAt);

        dbContext.AddRange(
            new User(
                ownerId,
                $"shipment-ship-owner-{ownerId:N}@example.com",
                $"shipment-ship-owner-{ownerId:N}@example.com",
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Customer,
                createdAt),
            new User(
                adminId,
                adminEmail,
                adminEmail,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                createdAt),
            product,
            variant,
            new Inventory(Guid.NewGuid(), productVariantId, 10, createdAt),
            order,
            shipment);

        await dbContext.SaveChangesAsync();

        await using var transaction = await orderStore.BeginTransactionAsync(CancellationToken.None);

        Assert.Equal(
            InventoryReservationResult.Reserved,
            await orderStore.TryReserveAsync(
                productVariantId,
                2,
                createdAt.AddMinutes(4),
                CancellationToken.None));

        orderStore.AddOrderItems(
        [
            new OrderItem(Guid.NewGuid(), orderId, productVariantId, 2, 10m)
        ]);

        orderStore.AddInventoryTransactions(
        [
            new InventoryTransaction(
            Guid.NewGuid(),
            productVariantId,
            InventoryTransactionType.Reserve,
            onHandQuantityDelta: 0,
            reservedQuantityDelta: 2,
            InventoryReferenceType.Order,
            orderId,
            reason: null,
            createdAt.AddMinutes(4))
        ]);

        await orderStore.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return new SeededShipment(
            shipmentId,
            orderId,
            productVariantId,
            adminId,
            adminEmail);
    }

    private static async Task<SeededPendingShipment> SeedPickingShipmentAsync(
    WebApplicationFactory<Program> factory)
    {
        var seeded = await SeedPendingShipmentAsync(factory);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        shipment.StartPicking(DateTimeOffset.UtcNow);

        await dbContext.SaveChangesAsync();

        return seeded;
    }

    private static async Task<SeededShipment> SeedShippedShipmentAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.MigrateAsync();

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var productVariantId = Guid.NewGuid();
        var adminEmail = $"shipment-delivery-admin-{adminId:N}@example.com";

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 20m,
            reservationExpiresAt: createdAt.AddHours(1),
            createdAt);

        order.Confirm(createdAt.AddMinutes(1));
        order.StartProcessing(createdAt.AddMinutes(2));

        var shipment = new Shipment(
            shipmentId,
            orderId,
            createdAt.AddMinutes(2));

        shipment.StartPicking(createdAt.AddMinutes(3));
        shipment.Pack(createdAt.AddMinutes(4));
        shipment.Ship(createdAt.AddMinutes(5));

        var product = new Product(
            Guid.NewGuid(),
            $"Shipment delivery product {Guid.NewGuid():N}",
            "Shipment delivery API integration test product",
            CatalogStatus.Active,
            createdAt);

        var variant = new ProductVariant(
            productVariantId,
            product.Id,
            $"SDA-{Guid.NewGuid():N}"[..16],
            "Shipment delivery API variant",
            currentPrice: 10m,
            CatalogStatus.Active,
            createdAt);

        dbContext.AddRange(
            new User(
                ownerId,
                $"shipment-delivery-owner-{ownerId:N}@example.com",
                $"shipment-delivery-owner-{ownerId:N}@example.com",
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Customer,
                createdAt),
            new User(
                adminId,
                adminEmail,
                adminEmail,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                createdAt),
            product,
            variant,
            new Inventory(Guid.NewGuid(), productVariantId, 8, createdAt.AddMinutes(5)),
            order,
            new OrderItem(Guid.NewGuid(), orderId, productVariantId, 2, 10m),
            shipment,
            new InventoryTransaction(
                Guid.NewGuid(),
                productVariantId,
                InventoryTransactionType.Issue,
                onHandQuantityDelta: -2,
                reservedQuantityDelta: -2,
                InventoryReferenceType.Shipment,
                shipmentId,
                reason: null,
                createdAt.AddMinutes(5)));

        await dbContext.SaveChangesAsync();

        return new SeededShipment(
            shipmentId,
            orderId,
            productVariantId,
            adminId,
            adminEmail);
    }

    private static async Task<SeededShipment> SeedOutForDeliveryShipmentAsync(WebApplicationFactory<Program> factory)
    {
        var seeded = await SeedShippedShipmentAsync(factory);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        shipment.StartDelivery(DateTimeOffset.UtcNow);

        await dbContext.SaveChangesAsync();

        return seeded;
    }

    private static async Task<SeededShipment> SeedDeliveryFailedShipmentAsync(WebApplicationFactory<Program> factory)
    {
        var seeded = await SeedOutForDeliveryShipmentAsync(factory);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .SingleAsync(candidate => candidate.Id == seeded.ShipmentId);

        shipment.MarkDeliveryFailed("Recipient unavailable", DateTimeOffset.UtcNow);

        await dbContext.SaveChangesAsync();

        return seeded;
    }

    private static async Task<SeededReturningShipment> SeedReturningShipmentAsync(
    WebApplicationFactory<Program> factory)
    {
        var seededShipment = await SeedDeliveryFailedShipmentAsync(factory);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var now = DateTimeOffset.UtcNow;

        var shipment = await dbContext.Shipments
            .SingleAsync(candidate => candidate.Id == seededShipment.ShipmentId);

        shipment.StartReturn(now);

        var paymentId = Guid.NewGuid();
        var payment = new Payment(
            paymentId,
            seededShipment.OrderId,
            amount: 20m,
            provider: "FakeProvider",
            providerPaymentId: $"shipment-return-payment-{paymentId:N}",
            gatewayIdempotencyKey: $"shipment-return-key-{paymentId:N}",
            createdAt: now.AddMinutes(-1),
            scenario: PaymentScenario.Success);

        payment.MarkSucceeded(payment.ProviderPaymentId, now);

        dbContext.Add(payment);
        await dbContext.SaveChangesAsync();

        return new SeededReturningShipment(seededShipment, paymentId);
    }

    private sealed record SeededReturningShipment(
        SeededShipment Shipment,
        Guid PaymentId);

    private static async Task<SeededReturnedShipment> SeedReturnedShipmentAsync(
    WebApplicationFactory<Program> factory)
    {
        var seeded = await SeedReturningShipmentAsync(factory);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var now = DateTimeOffset.UtcNow;

        var shipment = await dbContext.Shipments
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.ShipmentId);

        var order = await dbContext.Orders
            .SingleAsync(candidate => candidate.Id == seeded.Shipment.OrderId);

        var payment = await dbContext.Payments
            .SingleAsync(candidate => candidate.Id == seeded.PaymentId);

        shipment.MarkReturned(now);
        order.FailFulfillment(now);
        payment.MarkRefundPending($"fake-refund-{payment.Id:D}", now);

        await dbContext.SaveChangesAsync();

        return new SeededReturnedShipment(seeded.Shipment, seeded.PaymentId);
    }

    private sealed record SeededReturnedShipment(
        SeededShipment Shipment,
        Guid PaymentId);

    private static async Task<SeededConfirmedOrder> SeedConfirmedOrderAsync(
    WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.MigrateAsync();

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var adminEmail = $"shipment-create-admin-{adminId:N}@example.com";

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 20m,
            reservationExpiresAt: createdAt.AddHours(1),
            createdAt: createdAt);

        order.Confirm(createdAt.AddMinutes(1));

        dbContext.AddRange(
            new User(
                ownerId,
                $"shipment-create-owner-{ownerId:N}@example.com",
                $"shipment-create-owner-{ownerId:N}@example.com",
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Customer,
                createdAt),
            new User(
                adminId,
                adminEmail,
                adminEmail,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                createdAt),
            order);

        await dbContext.SaveChangesAsync();

        return new SeededConfirmedOrder(orderId, adminId, adminEmail);
    }

    private sealed record SeededConfirmedOrder(
    Guid OrderId,
    Guid AdminId,
    string AdminEmail);

    private static async Task<string> SeedCustomerEmailAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.MigrateAsync();

        var customerId = Guid.NewGuid();
        var email = $"shipment-command-customer-{customerId:N}@example.com";
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        dbContext.Users.Add(new User(
            customerId,
            email,
            email,
            passwordHasher.Hash(TestCredentials.ValidPassword),
            UserRole.Customer,
            createdAt));

        await dbContext.SaveChangesAsync();

        return email;
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = TestCredentials.ValidPassword });

        login.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            document.RootElement
                .GetProperty("data")
                .GetProperty("accessToken")
                .GetString());
    }

    private sealed record SeededShipment(
        Guid ShipmentId,
        Guid OrderId,
        Guid ProductVariantId,
        Guid AdminId,
        string AdminEmail);
}

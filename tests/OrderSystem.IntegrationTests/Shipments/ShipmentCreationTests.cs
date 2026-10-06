using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Shipments;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Shipments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Shipments;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class ShipmentCreationTests(PostgreSqlFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_WhenConfirmedOrder_CommitsShipmentProcessingOrderAndAdminHistory()
    {
        await using var factory = CreateFactory();
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var activityId = Guid.NewGuid();

        await SeedConfirmedOrderAsync(factory, ownerId, adminId, orderId);

        using (var commandScope = factory.Services.CreateScope())
        {
            var store = commandScope.ServiceProvider
                .GetRequiredService<IShipmentCommandStore>();

            var service = new ShipmentCommandService(
                store,
                new FakeCurrentUser(true, adminId, UserRole.Admin),
                new FakeClock(Now),
                new SequenceIdGenerator(shipmentId, historyId, activityId));

            var result = await service.CreateAsync(orderId, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(shipmentId, result.Value);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var shipment = await dbContext.Shipments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == shipmentId);

        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == orderId);

        var history = await dbContext.OrderStatusHistories
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.OrderId == orderId &&
                candidate.ReasonCode == OrderStatusReasonCode.ShipmentCreated);

        var activity = await dbContext.ShipmentActivityHistories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == activityId);

        var inventoryTransactions = await dbContext.InventoryTransactions
            .AsNoTracking()
            .Where(candidate => candidate.ReferenceId == orderId)
            .ToListAsync();

        Assert.Equal(orderId, shipment.OrderId);
        Assert.Equal(ShipmentStatus.Pending, shipment.Status);
        Assert.Equal(Now, shipment.CreatedAt);
        Assert.Equal(Now, shipment.UpdatedAt);

        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Equal(Now, order.UpdatedAt);

        Assert.Equal(OrderStatus.Confirmed, history.FromStatus);
        Assert.Equal(OrderStatus.Processing, history.ToStatus);
        Assert.Equal(OrderStatusHistoryActorType.Admin, history.ActorType);
        Assert.Equal(adminId, history.ActorUserId);
        Assert.Equal(Now, history.OccurredAt);

        Assert.Equal(shipmentId, activity.ShipmentId);
        Assert.Equal(ShipmentActivityType.Created, activity.ActivityType);
        Assert.Null(activity.FromStatus);
        Assert.Equal(ShipmentStatus.Pending, activity.ToStatus);
        Assert.Equal(ShipmentActivityActorType.Admin, activity.ActorType);
        Assert.Equal(adminId, activity.ActorUserId);
        Assert.Equal(Now, activity.OccurredAt);
        Assert.Null(activity.Reason);

        Assert.Empty(inventoryTransactions);
    }

    private static async Task SeedConfirmedOrderAsync(
        WebApplicationFactory<Program> factory,
        Guid ownerId,
        Guid adminId,
        Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        await dbContext.Database.MigrateAsync();

        var ownerEmail = $"shipment-owner-{ownerId:N}@example.com";
        var adminEmail = $"shipment-admin-{adminId:N}@example.com";

        var order = new Order(
            orderId,
            ownerId,
            totalAmount: 100m,
            reservationExpiresAt: Now.AddHours(1),
            createdAt: Now.AddMinutes(-2));

        order.Confirm(Now.AddMinutes(-1));

        dbContext.AddRange(
            new User(
                ownerId,
                ownerEmail,
                ownerEmail,
                "test-password-hash",
                UserRole.Customer,
                Now.AddMinutes(-2)),
            new User(
                adminId,
                adminEmail,
                adminEmail,
                "test-password-hash",
                UserRole.Admin,
                Now.AddMinutes(-2)),
            order);

        await dbContext.SaveChangesAsync();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString))));

    private sealed record FakeCurrentUser(
        bool IsAuthenticated,
        Guid? UserId,
        UserRole? Role) : ICurrentUser;

    private sealed class SequenceIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> _ids = new(ids);

        public Guid NewId() => _ids.Dequeue();
    }
}
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
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class NotificationApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "S10-T09")]
    [Trait("TestId", "API-NOTIF-001")]
    public async Task ListNotifications_Customer_ReturnsOnlyOwnHistoryNewestFirstWithPagination()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedNotificationsAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.CustomerEmail);

        using var firstPageResponse = await client.GetAsync("/api/notifications?page=1&pageSize=2");
        using var secondPageResponse = await client.GetAsync("/api/notifications?page=2&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);

        using var firstPage = JsonDocument.Parse(await firstPageResponse.Content.ReadAsStringAsync());
        var firstPageItems = firstPage.RootElement.GetProperty("data");
        Assert.Equal(2, firstPageItems.GetArrayLength());
        Assert.Equal(seeded.NewestNotificationId, firstPageItems[0].GetProperty("id").GetGuid());
        Assert.Equal(seeded.SecondNewestNotificationId, firstPageItems[1].GetProperty("id").GetGuid());
        Assert.Equal(seeded.CustomerOrderId, firstPageItems[0].GetProperty("orderId").GetGuid());
        Assert.Equal("OrderCreated", firstPageItems[0].GetProperty("type").GetString());
        Assert.True(firstPageItems[0].TryGetProperty("createdAt", out _));
        Assert.False(firstPageItems[0].TryGetProperty("messageId", out _));

        var pagination = firstPage.RootElement.GetProperty("metadata").GetProperty("pagination");
        Assert.Equal(1, pagination.GetProperty("page").GetInt32());
        Assert.Equal(2, pagination.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, pagination.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, pagination.GetProperty("totalPages").GetInt32());

        using var secondPage = JsonDocument.Parse(await secondPageResponse.Content.ReadAsStringAsync());
        var secondPageItems = secondPage.RootElement.GetProperty("data");
        Assert.Single(secondPageItems.EnumerateArray());
        Assert.Equal(seeded.OldestNotificationId, secondPageItems[0].GetProperty("id").GetGuid());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
                new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));

    private static async Task<SeededNotifications> SeedNotificationsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var now = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);
        var customer = new TestUser(Guid.NewGuid(), $"notification-customer-{Guid.NewGuid():N}@example.com");
        var otherCustomer = new TestUser(Guid.NewGuid(), $"notification-other-{Guid.NewGuid():N}@example.com");
        var customerOrder = new Order(Guid.NewGuid(), customer.Id, 10m, now.AddDays(-199), now.AddDays(-200));
        var otherOrder = new Order(Guid.NewGuid(), otherCustomer.Id, 20m, now.AddDays(-199), now.AddDays(-200));
        var notificationIdPrefix = Guid.NewGuid().ToByteArray();
        var secondNewestId = CreateNotificationId(notificationIdPrefix, 1);
        var newestId = CreateNotificationId(notificationIdPrefix, 2);
        var oldestId = Guid.NewGuid();

        dbContext.Users.AddRange(
            new User(customer.Id, customer.Email, customer.Email, hasher.Hash(TestCredentials.ValidPassword), UserRole.Customer, now.AddDays(-200)),
            new User(otherCustomer.Id, otherCustomer.Email, otherCustomer.Email, hasher.Hash(TestCredentials.ValidPassword), UserRole.Customer, now.AddDays(-200)));
        dbContext.Orders.AddRange(customerOrder, otherOrder);
        dbContext.Notifications.AddRange(
            new Notification(oldestId, customer.Id, customerOrder.Id, Guid.NewGuid(), now.AddDays(-100)),
            new Notification(secondNewestId, customer.Id, customerOrder.Id, Guid.NewGuid(), now),
            new Notification(newestId, customer.Id, customerOrder.Id, Guid.NewGuid(), now),
            new Notification(Guid.NewGuid(), otherCustomer.Id, otherOrder.Id, Guid.NewGuid(), now.AddDays(1)));
        await dbContext.SaveChangesAsync();

        return new(
            customer.Email,
            customerOrder.Id,
            newestId,
            secondNewestId,
            oldestId);
    }

    private static Guid CreateNotificationId(byte[] prefix, byte suffix)
    {
        var bytes = (byte[])prefix.Clone();
        bytes[^1] = suffix;
        return new Guid(bytes);
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = TestCredentials.ValidPassword });
        login.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

    private sealed record TestUser(Guid Id, string Email);

    private sealed record SeededNotifications(
        string CustomerEmail,
        Guid CustomerOrderId,
        Guid NewestNotificationId,
        Guid SecondNewestNotificationId,
        Guid OldestNotificationId);
}

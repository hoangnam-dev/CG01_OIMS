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
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderStatusHistoryApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "API-ORD-011")]
    public async Task GetOrderStatusHistory_Admin_ReturnsNewestFirstPagedHistory()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedHistoryAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.GetAsync($"/api/orders/{seeded.OrderId}/status-history?page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = document.RootElement.GetProperty("data");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(seeded.NewestHistoryId, entries[0].GetProperty("id").GetGuid());
        Assert.Equal(seeded.SecondNewestHistoryId, entries[1].GetProperty("id").GetGuid());
        Assert.Equal(OrderStatus.Processing.ToString(), entries[0].GetProperty("fromStatus").GetString());
        Assert.Equal(OrderStatus.Completed.ToString(), entries[0].GetProperty("toStatus").GetString());
        Assert.Equal(OrderStatusHistoryActorType.Customer.ToString(), entries[0].GetProperty("actorType").GetString());
        Assert.Equal(OrderCancellationReasonCode.CustomerRequested.ToString(), entries[0].GetProperty("reasonCode").GetString());

        var pagination = document.RootElement.GetProperty("metadata").GetProperty("pagination");
        Assert.Equal(1, pagination.GetProperty("page").GetInt32());
        Assert.Equal(2, pagination.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, pagination.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, pagination.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    [Trait("Requirement", "API-AUTHZ-005")]
    public async Task GetOrderStatusHistory_Customer_ReturnsForbiddenWithoutAuditData()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedHistoryAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.CustomerEmail);

        using var response = await client.GetAsync($"/api/orders/{seeded.OrderId}/status-history");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("data", out _));
        Assert.False(document.RootElement.TryGetProperty("reason", out _));
    }

    [Fact]
    [Trait("Requirement", "API-ORD-011")]
    public async Task GetOrderStatusHistory_AdminForMissingOrder_ReturnsNotFound()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedHistoryAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}/status-history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ORDER_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetOrderStatusHistory_AnonymousCaller_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/orders/{Guid.NewGuid()}/status-history");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetOrderStatusHistory_AdminWithInvalidPagination_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory();
        var seeded = await SeedHistoryAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.AdminEmail);

        using var response = await client.GetAsync(
            $"/api/orders/{seeded.OrderId}/status-history?page=0&pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());

        var errors = document.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("page", out _));
        Assert.True(errors.TryGetProperty("pageSize", out _));
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration(
                new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));

    private static async Task<SeededHistory> SeedHistoryAsync(WebApplicationFactory<Program> factory)
    {
        await MigrateDatabaseAsync(factory);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var now = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var customer = new TestUser(Guid.NewGuid(), $"history-customer-{Guid.NewGuid():N}@example.com");
        var admin = new TestUser(Guid.NewGuid(), $"history-admin-{Guid.NewGuid():N}@example.com");
        var order = new Order(Guid.NewGuid(), customer.Id, 10m, now.AddMinutes(15), now);

        dbContext.Users.AddRange(
            new User(customer.Id, customer.Email, customer.Email, hasher.Hash(TestCredentials.ValidPassword), UserRole.Customer, now),
            new User(admin.Id, admin.Email, admin.Email, hasher.Hash(TestCredentials.ValidPassword), UserRole.Admin, now));
        dbContext.Orders.Add(order);

        var historyIdPrefix = Guid.NewGuid().ToByteArray();
        var oldestHistoryId = CreateHistoryId(historyIdPrefix, 1);
        var secondNewestHistoryId = CreateHistoryId(historyIdPrefix, 2);
        var newestHistoryId = CreateHistoryId(historyIdPrefix, 3);
        dbContext.OrderStatusHistories.AddRange(
            new OrderStatusHistory(
                oldestHistoryId, order.Id, OrderStatus.PendingPayment, OrderStatus.Confirmed,
                OrderStatusHistoryActorType.System, null, now.AddMinutes(-1), null, OrderCancellationReasonCode.Other),
            new OrderStatusHistory(
                secondNewestHistoryId, order.Id, OrderStatus.Confirmed, OrderStatus.Processing,
                OrderStatusHistoryActorType.Admin, admin.Id, now, "Operations advanced the order.", OrderCancellationReasonCode.Other),
            new OrderStatusHistory(
                newestHistoryId, order.Id, OrderStatus.Processing, OrderStatus.Completed,
                OrderStatusHistoryActorType.Customer, customer.Id, now, null, OrderCancellationReasonCode.CustomerRequested));
        await dbContext.SaveChangesAsync();

        return new(order.Id, customer.Email, admin.Email, newestHistoryId, secondNewestHistoryId);
    }

    private static Guid CreateHistoryId(byte[] prefix, byte suffix)
    {
        var bytes = (byte[])prefix.Clone();
        bytes[^1] = suffix;
        return new Guid(bytes);
    }

    private static async Task MigrateDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestCredentials.ValidPassword });
        login.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

    private sealed record TestUser(Guid Id, string Email);

    private sealed record SeededHistory(
        Guid OrderId,
        string CustomerEmail,
        string AdminEmail,
        Guid NewestHistoryId,
        Guid SecondNewestHistoryId);
}

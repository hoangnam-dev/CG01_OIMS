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
public sealed class PaymentInitiationApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task InitiatePayment_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{Guid.NewGuid()}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-003")]
    public async Task InitiatePayment_CustomerWithPendingPaymentOrder_ReturnsCreatedPaymentAndReplay()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        var idempotencyKey = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        using var initialResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Created, initialResponse.StatusCode);

        using var initialDocument = JsonDocument.Parse(await initialResponse.Content.ReadAsStringAsync());

        var initialData = initialDocument.RootElement.GetProperty("data");
        var paymentId = initialData.GetProperty("id").GetGuid();

        Assert.Equal(orderId, initialData.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", initialData.GetProperty("status").GetString());
        Assert.Equal(125_000m, initialData.GetProperty("amount").GetDecimal());
        Assert.Equal("Fake", initialData.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, initialDocument.RootElement.GetProperty("metadata").ValueKind);

        using var replayResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.True(replayResponse.Headers.TryGetValues("Idempotency-Replayed", out var replayValues));
        Assert.Contains("true", replayValues);

        using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());

        Assert.Equal(paymentId, replayDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid());
    }

    [Fact]
    [Trait("Requirement", "API-PAY-QUERY-001")]
    public async Task GetPayment_CustomerOwner_ReturnsCurrentAllowlistedPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var initiationResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        initiationResponse.EnsureSuccessStatusCode();

        using var initiationDocument = JsonDocument.Parse(
            await initiationResponse.Content.ReadAsStringAsync());

        var paymentId = initiationDocument.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetGuid();

        using var response = await client.GetAsync($"/api/payments/{paymentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var data = document.RootElement.GetProperty("data");

        Assert.Equal(paymentId, data.GetProperty("id").GetGuid());
        Assert.Equal(orderId, data.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", data.GetProperty("status").GetString());
        Assert.Equal("Fake", data.GetProperty("provider").GetString());
        Assert.False(data.TryGetProperty("gatewayIdempotencyKey", out _));
        Assert.False(data.TryGetProperty("refundIdempotencyKey", out _));
        Assert.False(data.TryGetProperty("refundAttemptCount", out _));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("metadata").ValueKind);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-QUERY-002")]
    public async Task GetPaymentForOrder_CustomerOwner_ReturnsCurrentPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var initiationResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        initiationResponse.EnsureSuccessStatusCode();

        using var initiationDocument = JsonDocument.Parse(
            await initiationResponse.Content.ReadAsStringAsync());

        var paymentId = initiationDocument.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetGuid();

        using var response = await client.GetAsync($"/api/orders/{orderId}/payment");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var data = document.RootElement.GetProperty("data");

        Assert.Equal(paymentId, data.GetProperty("id").GetGuid());
        Assert.Equal(orderId, data.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", data.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("metadata").ValueKind);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-ENV-001")]
    public async Task InitiatePayment_WhenPaymentModuleIsDisabled_ReturnsNotFound()
    {
        await using var factory = CreatePaymentDisabledFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{Guid.NewGuid()}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-ENV-002")]
    public async Task InitiatePayment_WhenPaymentModuleIsExplicitlyEnabledInProduction_ReturnsUnauthorized()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration(
                    (_, configuration) =>
                        configuration.AddOimsTestConfiguration(
                            new KeyValuePair<string, string?>("Payment:Enabled", bool.TrueString)));
            });

        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{Guid.NewGuid()}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-ENV-003")]
    public async Task InitiatePayment_WhenPaymentModuleIsEnabledInProduction_CreatesSimulatedPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory("Production");
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(orderId, document.RootElement.GetProperty("data").GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", document.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    private WebApplicationFactory<Program> CreateFactory(string environment = "Development") =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration(
                    (_, configuration) =>
                        configuration.AddOimsTestConfiguration(
                            new KeyValuePair<string, string?>(
                                "Database:ConnectionString",
                                postgres.ConnectionString)));
            });

    private static async Task<TestUser> CreateCustomerAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.MigrateAsync();

        var email = $"payment-customer-{Guid.NewGuid():N}@example.com";
        var customer = new TestUser(Guid.NewGuid(), email);

        db.Users.Add(new User(
            customer.Id,
            email,
            email,
            hasher.Hash(TestCredentials.ValidPassword),
            UserRole.Customer,
            now));

        await db.SaveChangesAsync();

        return customer;
    }

    private static WebApplicationFactory<Program> CreatePaymentDisabledFactory() =>
    new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Payment:Enabled", "false")));
        });

    private static async Task<Guid> SeedPendingPaymentOrderAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        DateTimeOffset now)
    {
        var order = new Order(
            Guid.NewGuid(),
            userId,
            totalAmount: 125_000m,
            reservationExpiresAt: now.AddMinutes(10),
            createdAt: now);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return order.Id;
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
}

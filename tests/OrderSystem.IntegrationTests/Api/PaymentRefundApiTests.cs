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
using OrderSystem.Application.Payments;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class PaymentRefundApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "PAY-REF-004")]
    public async Task RetryManualRefund_AdminWithManualReviewPayment_ReturnsRefundedPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var admin = await CreateAdminAsync(factory, now);
        var paymentId = await SeedRefundPendingPaymentAsync(factory, admin.Id, now, requiresManualReview: true);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);

        using var response = await client.PostAsync(
            $"/api/payments/{paymentId}/refund",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            paymentId,
            document.RootElement
                .GetProperty("data")
                .GetProperty("id")
                .GetGuid());

        Assert.Equal(
            PaymentStatus.Refunded.ToString(),
            document.RootElement
                .GetProperty("data")
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    [Trait("Requirement", "API-PAY-007")]
    public async Task RetryManualRefund_AdminWithUnknownPayment_ReturnsPaymentNotFound()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var admin = await CreateAdminAsync(factory, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);

        using var response = await client.PostAsync(
            $"/api/payments/{Guid.NewGuid()}/refund",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            "PAYMENT_NOT_FOUND",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Requirement", "API-PAY-008")]
    public async Task RetryManualRefund_AdminWithAlreadyRefundedPayment_ReturnsCurrentPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var admin = await CreateAdminAsync(factory, now);
        var paymentId = await SeedRefundPendingPaymentAsync(
            factory,
            admin.Id,
            now,
            requiresManualReview: false);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var payment = await db.Payments.SingleAsync(item => item.Id == paymentId);

            payment.MarkRefunded(
                $"fake-refund-{paymentId:D}",
                now.AddSeconds(1));

            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);

        using var response = await client.PostAsync(
            $"/api/payments/{paymentId}/refund",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            PaymentStatus.Refunded.ToString(),
            document.RootElement
                .GetProperty("data")
                .GetProperty("status")
                .GetString());

        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();

        var persistedPayment = await assertionDb.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal(0, persistedPayment.RefundAttemptCount);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-009")]
    public async Task RetryManualRefund_AdminWhenProviderResponseIsLost_ReturnsRefundPending()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var admin = await CreateAdminAsync(factory, now);
        var paymentId = await SeedRefundPendingPaymentAsync(
            factory,
            admin.Id,
            now,
            requiresManualReview: true,
            scenario: PaymentScenario.SuccessButResponseLost);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, admin.Email);

        using var response = await client.PostAsync(
            $"/api/payments/{paymentId}/refund",
            content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            PaymentStatus.RefundPending.ToString(),
            document.RootElement
                .GetProperty("data")
                .GetProperty("status")
                .GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var persistedPayment = await db.Payments
            .AsNoTracking()
            .SingleAsync(item => item.Id == paymentId);

        Assert.Equal(PaymentStatus.RefundPending, persistedPayment.Status);
        Assert.Equal(1, persistedPayment.RefundAttemptCount);
        Assert.NotNull(persistedPayment.ManualReviewRequiredAt);
        Assert.Null(persistedPayment.NextRefundAttemptAt);
        Assert.Null(persistedPayment.ProviderRefundId);
        Assert.Null(persistedPayment.RefundedAt);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration(
                    (_, configuration) =>
                        configuration.AddOimsTestConfiguration(
                            new KeyValuePair<string, string?>(
                                "Database:ConnectionString",
                                postgres.ConnectionString)));
            });

    private static async Task<TestUser> CreateAdminAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        await db.Database.MigrateAsync();

        var email = $"refund-admin-{Guid.NewGuid():N}@example.com";
        var admin = new TestUser(Guid.NewGuid(), email);

        db.Users.Add(
            new User(
                admin.Id,
                email,
                email,
                hasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                now));

        await db.SaveChangesAsync();

        return admin;
    }

    private static async Task<Guid> SeedRefundPendingPaymentAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        DateTimeOffset now,
        bool requiresManualReview,
        PaymentScenario scenario = PaymentScenario.Success)
    {
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var createdAt = now.AddMinutes(-3);
        var providerPaymentId = $"fake-pay-{paymentId:D}";
        var refundKey = $"fake-refund-{paymentId:D}";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        var order = new Order(
            orderId,
            userId,
            totalAmount: 125_000m,
            reservationExpiresAt: now.AddMinutes(10),
            createdAt);

        order.Cancel(now.AddMinutes(-2));

        var payment = new Payment(
            paymentId,
            orderId,
            amount: 125_000m,
            PaymentProviderCodes.Fake,
            providerPaymentId,
            gatewayIdempotencyKey: $"fake-gateway-{paymentId:D}",
            createdAt,
            scenario);

        payment.MarkSucceeded(providerPaymentId, now.AddMinutes(-1));
        payment.MarkRefundPending(refundKey, now);
        if (requiresManualReview)
        {
            payment.MarkRefundManualReviewRequired(now);
        }

        db.AddRange(order, payment);
        await db.SaveChangesAsync();

        return paymentId;
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = TestCredentials.ValidPassword });

        login.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                document.RootElement
                    .GetProperty("data")
                    .GetProperty("accessToken")
                    .GetString());
    }

    private sealed record TestUser(Guid Id, string Email);
}

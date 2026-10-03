using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Payments;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class FakePaymentWebhookProcessingApiTests(
    PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Post_AuthenticatedEventForUnknownPayment_ReturnsNotFound()
    {
        var now = new DateTimeOffset(
            2026,
            10,
            2,
            12,
            0,
            0,
            TimeSpan.Zero);
        var webhookSecret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Convert.ToBase64String(webhookSecret);
        var timestamp = now.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var body = JsonSerializer.Serialize(new
        {
            providerEventId = $"fake-event-{Guid.NewGuid():N}",
            providerPaymentId = $"fake-pay-{Guid.NewGuid():N}",
            eventType = "payment.succeeded",
            status = "Succeeded",
            occurredAt = now
        });
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexStringLower(
            HMACSHA256.HashData(webhookSecret, signedPayload));

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Database:ConnectionString",
                            postgres.ConnectionString),
                        new KeyValuePair<string, string?>(
                            "Payment:FakeWebhookSecret",
                            encodedSecret)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(new FakeClock(now));
                });
            });

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments/webhooks/fake")
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "X-Fake-Webhook-Timestamp",
            timestamp);
        request.Headers.TryAddWithoutValidation(
            "X-Fake-Webhook-Signature",
            signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PAYMENT_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_EventTypeDoesNotMatchStatus_ReturnsBadRequest()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var webhookSecret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Convert.ToBase64String(webhookSecret);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var body = JsonSerializer.Serialize(new
        {
            providerEventId = $"fake-event-{Guid.NewGuid():N}",
            providerPaymentId = $"fake-pay-{Guid.NewGuid():N}",
            eventType = "payment.failed",
            status = "Succeeded",
            occurredAt = now
        });
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(webhookSecret, signedPayload));

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString),
                        new KeyValuePair<string, string?>("Payment:FakeWebhookSecret", encodedSecret)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(new FakeClock(now));
                });
            });

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/fake")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Signature", signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_AuthenticatedSucceededEvent_AppliesResultAndPersistsReceipt()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{Guid.NewGuid():N}";
        var providerEventId = $"fake-event-{Guid.NewGuid():N}";
        var webhookSecret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Convert.ToBase64String(webhookSecret);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var body = JsonSerializer.Serialize(new
        {
            providerEventId,
            providerPaymentId,
            eventType = "payment.succeeded",
            status = "Succeeded",
            occurredAt = now
        });
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(webhookSecret, signedPayload));
        var expectedPayloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString),
                        new KeyValuePair<string, string?>("Payment:FakeWebhookSecret", encodedSecret)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(new FakeClock(now));
                });
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var email = $"webhook-{userId:N}@example.com";
            dbContext.AddRange(
                new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
                new Order(orderId, userId, 125_000m, now.AddMinutes(15), now),
                new Payment(
                    paymentId,
                    orderId,
                    125_000m,
                    PaymentProviderCodes.Fake,
                    providerPaymentId,
                    $"gateway-{Guid.NewGuid():N}",
                    now));
            await dbContext.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/fake")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Signature", signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var assertionScope = factory.Services.CreateScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var payment = await assertionDb.Payments.AsNoTracking().SingleAsync(item => item.Id == paymentId);
        var order = await assertionDb.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        var receipt = await assertionDb.ProviderPaymentEvents.AsNoTracking()
            .SingleAsync(item => item.Provider == PaymentProviderCodes.Fake && item.ProviderEventId == providerEventId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(expectedPayloadHash, receipt.PayloadHash);
    }

    [Fact]
    public async Task Post_ReusedEventIdentityWithDifferentPayload_ReturnsConflictProblem()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{Guid.NewGuid():N}";
        var providerEventId = $"fake-event-{Guid.NewGuid():N}";
        var webhookSecret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Convert.ToBase64String(webhookSecret);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var originalBody = JsonSerializer.Serialize(new
        {
            providerEventId,
            providerPaymentId,
            eventType = "payment.succeeded",
            status = "Succeeded",
            occurredAt = now
        });
        var conflictingBody = originalBody + " ";

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString),
                        new KeyValuePair<string, string?>("Payment:FakeWebhookSecret", encodedSecret)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(new FakeClock(now));
                });
            });

        using (var setupScope = factory.Services.CreateScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            await dbContext.Database.MigrateAsync();
            var email = $"webhook-conflict-{userId:N}@example.com";
            dbContext.AddRange(
                new User(userId, email, email, "test-password-hash", UserRole.Customer, now),
                new Order(orderId, userId, 125_000m, now.AddMinutes(15), now),
                new Payment(
                    Guid.NewGuid(),
                    orderId,
                    125_000m,
                    PaymentProviderCodes.Fake,
                    providerPaymentId,
                    $"gateway-{Guid.NewGuid():N}",
                    now));
            await dbContext.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var firstRequest = CreateSignedRequest(originalBody, timestamp, webhookSecret);
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var conflictingRequest = CreateSignedRequest(conflictingBody, timestamp, webhookSecret);
        using var conflictingResponse = await client.SendAsync(conflictingRequest);

        Assert.Equal(HttpStatusCode.Conflict, conflictingResponse.StatusCode);
        var responseBody = await conflictingResponse.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(responseBody));
        using var document = JsonDocument.Parse(responseBody);
        Assert.Equal("PAYMENT_EVENT_CONFLICT", document.RootElement.GetProperty("code").GetString());
    }

    private static HttpRequestMessage CreateSignedRequest(string body, string timestamp, byte[] secret)
    {
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(secret, signedPayload));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/fake")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Signature", signature);
        return request;
    }
}

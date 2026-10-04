using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

public sealed class FakePaymentWebhookApiTests
{
    [Fact]
    public async Task Post_InvalidSignatureAndMalformedJson_ReturnsUnauthorized()
    {
        var webhookSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(new KeyValuePair<string, string?>("Payment:FakeWebhookSecret", webhookSecret)));
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/fake")
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "X-Fake-Webhook-Timestamp",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Signature", new string('0', 64));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHORIZED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_BodyLargerThan16KiB_ReturnsPayloadTooLarge()
    {
        var webhookSecret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Convert.ToBase64String(webhookSecret);
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>(
                            "Payment:FakeWebhookSecret",
                            encodedSecret)));
            });
        using var client = factory.CreateClient();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var body = new string('a', (16 * 1024) + 1);
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexStringLower(
            HMACSHA256.HashData(webhookSecret, signedPayload));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/payments/webhooks/fake")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "X-Fake-Webhook-Timestamp",
            timestamp);
        request.Headers.TryAddWithoutValidation(
            "X-Fake-Webhook-Signature",
            signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Post_ProductionEnvironmentWithPaymentModuleDisabled_ReturnsNotFound()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Payment:Enabled", bool.FalseString)));
            });
        using var client = factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/payments/webhooks/fake", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_AuthenticatedMalformedJson_ReturnsValidationProblem()
    {
        var webhookSecret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Convert.ToBase64String(webhookSecret);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        const string body = "{not-json";
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(webhookSecret, signedPayload));
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Payment:FakeWebhookSecret", encodedSecret)));
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhooks/fake")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Fake-Webhook-Signature", signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
    }
}

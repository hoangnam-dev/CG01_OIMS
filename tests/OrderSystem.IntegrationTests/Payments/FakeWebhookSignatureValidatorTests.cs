using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrderSystem.Api.Payments;

namespace OrderSystem.IntegrationTests.Payments;

public sealed class FakeWebhookSignatureValidatorTests
{
    [Fact]
    public void IsValid_CorrectSignature_ReturnsTrue()
    {
        var now = DateTimeOffset.Parse("2026-09-15T03:32:00Z", CultureInfo.InvariantCulture);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes("""
            {"providerEventId":"fake-event-001","status":"Succeeded"}
            """);
        var secret = RandomNumberGenerator.GetBytes(32);
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body)}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(secret, signedPayload));
        var validator = new FakeWebhookSignatureValidator(secret, TimeSpan.FromMinutes(5));

        var result = validator.IsValid(timestamp, signature, body, now);

        Assert.True(result);
    }

    [Fact]
    public void Constructor_SecretShorterThan32Bytes_ThrowsArgumentException()
    {
        var secret = new byte[31];

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = new FakeWebhookSignatureValidator(secret, TimeSpan.FromMinutes(5));
        });

        Assert.Equal("secret", exception.ParamName);
    }

    [Fact]
    public void IsValid_TimestampOlderThanFiveMinutes_ReturnsFalse()
    {
        var now = DateTimeOffset.Parse("2026-09-15T03:32:00Z", CultureInfo.InvariantCulture);
        var timestamp = now.AddMinutes(-5).AddSeconds(-1).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes("{}");
        var secret = RandomNumberGenerator.GetBytes(32);
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body)}");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(secret, signedPayload));
        var validator = new FakeWebhookSignatureValidator(secret, TimeSpan.FromMinutes(5));

        var result = validator.IsValid(timestamp, signature, body, now);

        Assert.False(result);
    }
}

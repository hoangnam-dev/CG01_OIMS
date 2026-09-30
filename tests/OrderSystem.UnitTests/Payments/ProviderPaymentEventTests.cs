using OrderSystem.Domain.Payments;

namespace OrderSystem.UnitTests.Payments;

public sealed class ProviderPaymentEventTests
{
    [Fact]
    public void Constructor_WithValidValues_CreatesImmutableProviderEvent()
    {
        var id = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var receivedAt = DateTimeOffset.UtcNow;
        var processedAt = receivedAt.AddSeconds(1);
        var payloadHash = new string('a', 64);

        var providerEvent = new ProviderPaymentEvent(
            id,
            paymentId,
            "Fake",
            "fake-event-001",
            "fake-pay-001",
            "payment.succeeded",
            payloadHash,
            occurredAt,
            receivedAt,
            processedAt);

        Assert.Equal(id, providerEvent.Id);
        Assert.Equal(paymentId, providerEvent.PaymentId);
        Assert.Equal("Fake", providerEvent.Provider);
        Assert.Equal("fake-event-001", providerEvent.ProviderEventId);
        Assert.Equal("fake-pay-001", providerEvent.ProviderPaymentId);
        Assert.Equal("payment.succeeded", providerEvent.EventType);
        Assert.Equal(payloadHash, providerEvent.PayloadHash);
        Assert.Equal(occurredAt, providerEvent.OccurredAt);
        Assert.Equal(receivedAt, providerEvent.ReceivedAt);
        Assert.Equal(processedAt, providerEvent.ProcessedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankProvider_ThrowsForProvider(string provider)
    {
        var action = () => new ProviderPaymentEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            provider,
            "fake-event-001",
            "fake-pay-001",
            "payment.succeeded",
            new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("provider", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithProviderEventIdLongerThan128Characters_ThrowsForProviderEventId()
    {
        var action = () => new ProviderPaymentEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Fake",
            new string('E', ProviderPaymentEvent.MaximumIdentifierLength + 1),
            "fake-pay-001",
            "payment.succeeded",
            new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("providerEventId", exception.ParamName);
    }
}

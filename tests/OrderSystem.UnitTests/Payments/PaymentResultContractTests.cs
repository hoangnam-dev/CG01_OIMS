using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.UnitTests.Payments;

public sealed class PaymentResultContractTests
{
    [Fact]
    public void ProviderPaymentOutcome_DefinesOnlyAuthoritativeTerminalOutcomes()
    {
        Assert.Equal(["Succeeded", "Failed"], Enum.GetNames<ProviderPaymentOutcome>());
    }

    [Fact]
    public void PaymentResultSource_DefinesAllAuthoritativeDeliveryChannels()
    {
        Assert.Equal(typeof(Enum), typeof(PaymentResultSource).BaseType);
        Assert.Equal(
            ["SynchronousResponse", "Webhook", "Reconciliation"],
            typeof(PaymentResultSource).GetEnumNames());
    }

    [Fact]
    public void ApplyPaymentResultCommand_PreservesAuthoritativeFailureEvidence()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        var providerEvent = new ProviderPaymentEventData(
            "Fake",
            "fake-event-123",
            "payment.failed",
            new string('b', 64));

        var command = new ApplyPaymentResultCommand(
            "fake-pay-123",
            ProviderPaymentOutcome.Failed,
            "DECLINED",
            PaymentResultSource.Webhook,
            providerEvent,
            occurredAt);

        Assert.Equal("fake-pay-123", command.ProviderPaymentId);
        Assert.Equal(ProviderPaymentOutcome.Failed, command.Outcome);
        Assert.Equal("DECLINED", command.FailureCode);
        Assert.Equal(PaymentResultSource.Webhook, command.Source);
        Assert.Same(providerEvent, command.ProviderEvent);
        Assert.Equal(occurredAt, command.OccurredAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ApplyPaymentResultCommand_FailedWithoutFailureCode_ThrowsArgumentException(string? failureCode)
    {
        Action createCommand = () =>
        {
            _ = new ApplyPaymentResultCommand(
                "fake-pay-123",
                ProviderPaymentOutcome.Failed,
                failureCode,
                PaymentResultSource.Reconciliation,
                providerEvent: null,
                new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        };

        Assert.ThrowsAny<ArgumentException>(createCommand);
    }

    [Fact]
    public void ApplyPaymentResultCommand_SucceededWithFailureCode_ThrowsArgumentException()
    {
        Action createCommand = () =>
        {
            _ = new ApplyPaymentResultCommand(
                "fake-pay-123",
                ProviderPaymentOutcome.Succeeded,
                "DECLINED",
                PaymentResultSource.Reconciliation,
                providerEvent: null,
                new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        };

        Assert.ThrowsAny<ArgumentException>(createCommand);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-001")]
    public void ApplyPaymentResultCommand_Webhook_PreservesProviderEventData()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);
        var payloadHash = new string('a', 64);
        var providerEvent = new ProviderPaymentEventData(
            "Fake",
            "fake-event-duplicate-001",
            "payment.succeeded",
            payloadHash);

        var command = new ApplyPaymentResultCommand(
            "fake-pay-123",
            ProviderPaymentOutcome.Succeeded,
            failureCode: null,
            PaymentResultSource.Webhook,
            providerEvent,
            occurredAt);

        Assert.Equal(PaymentResultSource.Webhook, command.Source);

        var actualProviderEvent = Assert.IsType<ProviderPaymentEventData>(command.ProviderEvent);

        Assert.Same(providerEvent, actualProviderEvent);
        Assert.Equal("Fake", actualProviderEvent.Provider);
        Assert.Equal("fake-event-duplicate-001", actualProviderEvent.ProviderEventId);
        Assert.Equal("payment.succeeded", actualProviderEvent.EventType);
        Assert.Equal(payloadHash, actualProviderEvent.PayloadHash);
        Assert.Equal(occurredAt, command.OccurredAt);
    }

    [Fact]
    [Trait("Requirement", "PAY-WEB-001")]
    public void ApplyPaymentResultCommand_WebhookWithoutProviderEvent_ThrowsArgumentException()
    {
        Action createCommand = () =>
        {
            _ = new ApplyPaymentResultCommand(
                "fake-pay-123",
                ProviderPaymentOutcome.Succeeded,
                failureCode: null,
                PaymentResultSource.Webhook,
                providerEvent: null,
                occurredAt: new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero));
        };

        var exception = Assert.ThrowsAny<ArgumentException>(createCommand);

        Assert.Equal("providerEvent", exception.ParamName);
    }

    [Theory]
    [InlineData(PaymentResultSource.SynchronousResponse)]
    [InlineData(PaymentResultSource.Reconciliation)]
    public void ApplyPaymentResultCommand_NonWebhookWithProviderEvent_ThrowsArgumentException(
    PaymentResultSource source)
    {
        var providerEvent = new ProviderPaymentEventData(
            "Fake",
            "fake-event-invalid-source-001",
            "payment.succeeded",
            new string('a', 64));

        Action createCommand = () =>
        {
            _ = new ApplyPaymentResultCommand(
                "fake-pay-123",
                ProviderPaymentOutcome.Succeeded,
                failureCode: null,
                source,
                providerEvent,
                occurredAt: new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero));
        };

        var exception = Assert.ThrowsAny<ArgumentException>(createCommand);

        Assert.Equal("providerEvent", exception.ParamName);
    }

    [Theory]
    [InlineData(PaymentResultApplicationStatus.Accepted)]
    [InlineData(PaymentResultApplicationStatus.Duplicate)]
    [InlineData(PaymentResultApplicationStatus.PaymentNotFound)]
    [InlineData(PaymentResultApplicationStatus.EventConflict)]
    public void PaymentResultApplicationOutcome_PreservesStatus(
    PaymentResultApplicationStatus status)
    {
        var outcome = new PaymentResultApplicationOutcome(status);

        Assert.Equal(status, outcome.Status);
    }
}
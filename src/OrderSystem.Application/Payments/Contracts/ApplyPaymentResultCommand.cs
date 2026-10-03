namespace OrderSystem.Application.Payments.Contracts;

public sealed record ApplyPaymentResultCommand
{
    public ApplyPaymentResultCommand(
        string providerPaymentId,
        ProviderPaymentOutcome outcome,
        string? failureCode,
        PaymentResultSource source,
        ProviderPaymentEventData? providerEvent,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);

        if (source == PaymentResultSource.Webhook)
        {
            ArgumentNullException.ThrowIfNull(providerEvent);
        }
        else if (providerEvent is not null)
        {
            throw new ArgumentException("Provider event must be null for a non-webhook source", nameof(providerEvent));
        }
        if (outcome == ProviderPaymentOutcome.Failed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        }
        if (outcome == ProviderPaymentOutcome.Succeeded && failureCode is not null)
        {
            throw new ArgumentException("Failure code must be null for a succeeded outcome", nameof(failureCode));
        }

        ProviderPaymentId = providerPaymentId;
        Outcome = outcome;
        FailureCode = failureCode;
        Source = source;
        ProviderEvent = providerEvent;
        OccurredAt = occurredAt;
    }

    public string ProviderPaymentId { get; }

    public ProviderPaymentOutcome Outcome { get; }

    public string? FailureCode { get; }

    public PaymentResultSource Source { get; }

    public ProviderPaymentEventData? ProviderEvent { get; }

    public DateTimeOffset OccurredAt { get; }
}
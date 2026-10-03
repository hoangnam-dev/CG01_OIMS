using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Payments;

public sealed class Payment
{
    private const int MaximumProviderLength = 32;
    private const int MaximumProviderIdentifierLength = 128;
    private const int MaximumFailureCodeLength = 64;

    private Payment()
    {
    }

    public Payment(
        Guid id,
        Guid orderId,
        decimal amount,
        string provider,
        string providerPaymentId,
        string gatewayIdempotencyKey,
        DateTimeOffset createdAt,
        PaymentScenario? scenario = null
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        OrderId = DomainGuard.RequiredGuid(orderId);
        Amount = DomainGuard.NotNegative(amount);
        Provider = RequireProvider(provider);
        ProviderPaymentId = RequireProviderIdentifier(providerPaymentId, nameof(providerPaymentId));
        GatewayIdempotencyKey = RequireProviderIdentifier(gatewayIdempotencyKey, nameof(gatewayIdempotencyKey));
        Scenario = scenario is null ? null : DomainGuard.DefinedEnum(scenario.Value, nameof(scenario));
        Status = PaymentStatus.Pending;
        RefundAttemptCount = 0;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public PaymentStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string ProviderPaymentId { get; private set; } = string.Empty;
    public string GatewayIdempotencyKey { get; private set; } = string.Empty;
    public PaymentScenario? Scenario { get; private set; }
    public string? RefundIdempotencyKey { get; private set; }
    public string? ProviderRefundId { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset? LastStatusCheckedAt { get; private set; }
    public DateTimeOffset? RefundRequestedAt { get; private set; }
    public int RefundAttemptCount { get; private set; }
    public DateTimeOffset? NextRefundAttemptAt { get; private set; }
    public DateTimeOffset? ManualReviewRequiredAt { get; private set; }
    public DateTimeOffset? RefundedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private static string RequireProvider(string provider)
    {
        var normalizedProvider = DomainGuard.RequiredText(provider);

        if (normalizedProvider.Length > MaximumProviderLength)
        {
            throw new ArgumentException($"Provider cannot exceed {MaximumProviderLength} characters.", nameof(provider));
        }

        return normalizedProvider;
    }

    private static string RequireProviderIdentifier(string? value, string paramName)
    {
        var normalizedIdentifier = DomainGuard.RequiredText(value, paramName);

        if (normalizedIdentifier.Length > MaximumProviderIdentifierLength)
        {
            throw new ArgumentException($"Provider identifier cannot exceed {MaximumProviderIdentifierLength} characters.", paramName);
        }

        return normalizedIdentifier;
    }

    private static string RequireFailureCode(string failureCode)
    {
        var normalizedFailureCode = DomainGuard.RequiredText(failureCode);

        if (normalizedFailureCode.Length > MaximumFailureCodeLength)
        {
            throw new ArgumentException($"Failure code cannot exceed {MaximumFailureCodeLength} characters.", nameof(failureCode));
        }

        return normalizedFailureCode;
    }

    private void EnsureTimestampDoesNotRegress(DateTimeOffset timestamp, string paramName)
    {
        if (timestamp < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(paramName, "Payment transition timestamp cannot be earlier than the last update");
        }
    }

    public void RecordStatusCheck(DateTimeOffset checkedAt)
    {
        if (LastStatusCheckedAt is not null && checkedAt < LastStatusCheckedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(checkedAt), "Payment status check timestamp cannot be earlier than the previous check");
        }
        LastStatusCheckedAt = checkedAt;
    }

    public void MarkProcessing(DateTimeOffset updatedAt)
    {
        if (Status != PaymentStatus.Pending)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt, nameof(updatedAt));

        Status = PaymentStatus.Processing;
        UpdatedAt = updatedAt;
    }

    public void MarkSucceeded(string providerPaymentId, DateTimeOffset updatedAt)
    {
        var normalizedProviderPaymentId = RequireProviderIdentifier(providerPaymentId, nameof(providerPaymentId));

        if (!string.Equals(ProviderPaymentId, normalizedProviderPaymentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Provider Payment ID does not match this Payment.", nameof(providerPaymentId));
        }

        if (Status is not PaymentStatus.Pending and not PaymentStatus.Processing)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt, nameof(updatedAt));

        Status = PaymentStatus.Succeeded;
        UpdatedAt = updatedAt;
    }

    public void MarkFailed(string providerPaymentId, string failureCode, DateTimeOffset updatedAt)
    {
        var normalizedProviderPaymentId = RequireProviderIdentifier(providerPaymentId, nameof(providerPaymentId));

        if (!string.Equals(ProviderPaymentId, normalizedProviderPaymentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Provider Payment ID does not match this Payment.", nameof(providerPaymentId));
        }

        var normalizedFailureCode = RequireFailureCode(failureCode);

        if (Status is not PaymentStatus.Pending and not PaymentStatus.Processing)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt, nameof(updatedAt));

        FailureCode = normalizedFailureCode;
        Status = PaymentStatus.Failed;
        UpdatedAt = updatedAt;
    }

    public void MarkRefundPending(string refundIdempotencyKey, DateTimeOffset requestedAt)
    {
        var normalizedRefundIdempotencyKey = RequireProviderIdentifier(refundIdempotencyKey, nameof(refundIdempotencyKey));

        if (Status == PaymentStatus.RefundPending)
        {
            if (!string.Equals(RefundIdempotencyKey, normalizedRefundIdempotencyKey, StringComparison.Ordinal))
            {
                throw new ArgumentException("Refund idempotency key does not match this Payment.", nameof(refundIdempotencyKey));
            }
            return;
        }

        if (Status != PaymentStatus.Succeeded)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(requestedAt, nameof(requestedAt));

        RefundIdempotencyKey = normalizedRefundIdempotencyKey;
        RefundRequestedAt = requestedAt;
        RefundAttemptCount = 0;
        NextRefundAttemptAt = requestedAt;
        Status = PaymentStatus.RefundPending;
        UpdatedAt = requestedAt;
    }

    public void MarkRefunded(string providerRefundId, DateTimeOffset refundedAt)
    {
        var normalizedProviderRefundId = RequireProviderIdentifier(providerRefundId, nameof(providerRefundId));

        if (Status == PaymentStatus.Refunded)
        {
            if (!string.Equals(ProviderRefundId, normalizedProviderRefundId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Provider refund ID does not match this Payment.", nameof(providerRefundId));
            }

            return;
        }

        if (Status != PaymentStatus.RefundPending)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(refundedAt, nameof(refundedAt));

        ProviderRefundId = normalizedProviderRefundId;
        RefundedAt = refundedAt;
        NextRefundAttemptAt = null;
        Status = PaymentStatus.Refunded;
        UpdatedAt = refundedAt;
    }
}
namespace OrderSystem.Application.Payments.Contracts;

public enum PaymentRefundManualClaimStatus
{
    Claimed = 1,
    PaymentNotFound = 2,
    NotRetryable = 3,
    AlreadyRefunded = 4
}

public sealed record PaymentRefundManualClaimResult(PaymentRefundManualClaimStatus Status, PaymentRefundCandidate? Candidate)
{
    public static PaymentRefundManualClaimResult Claimed(PaymentRefundCandidate candidate) =>
        new(PaymentRefundManualClaimStatus.Claimed, candidate);

    public static PaymentRefundManualClaimResult PaymentNotFound { get; } =
        new(PaymentRefundManualClaimStatus.PaymentNotFound, null);

    public static PaymentRefundManualClaimResult NotRetryable { get; } =
        new(PaymentRefundManualClaimStatus.NotRetryable, null);

    public static PaymentRefundManualClaimResult AlreadyRefunded(PaymentRefundCandidate candidate) =>
        new(PaymentRefundManualClaimStatus.AlreadyRefunded, candidate);
}
namespace OrderSystem.Application.Payments.Contracts;

public sealed record PaymentInitiationClaim(
    Guid Id,
    Guid UserId,
    Guid IdempotencyKey,
    byte[] RequestHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset DeleteAfter
);

public enum PaymentInitiationClaimOutcome
{
    Claimed = 1,
    CompletedReplay = 2,
    Expired = 3,
    Processing = 4,
    HashConflict = 5
}

public sealed record PaymentInitiationClaimResult(
    PaymentInitiationClaimOutcome Outcome,
    Guid? IdempotencyRequestId = null,
    Guid? PaymentId = null
);
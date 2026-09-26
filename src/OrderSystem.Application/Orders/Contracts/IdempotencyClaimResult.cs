namespace OrderSystem.Application.Orders;

public enum IdempotencyClaimOutcome
{
    Claimed,
    CompletedReplay,
    Expired,
    Processing,
    HashConflict
}

public sealed record CreateOrderIdempotencyClaim(
    Guid Id,
    Guid UserId,
    Guid IdempotencyKey,
    byte[] RequestHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset DeleteAfter);

public sealed record IdempotencyStoredResponse(
    Guid ResourceId,
    short HttpStatusCode,
    string ResponseBodyJson);

public sealed record IdempotencyClaimResult(
    IdempotencyClaimOutcome Outcome,
    IdempotencyStoredResponse? StoredResponse = null);

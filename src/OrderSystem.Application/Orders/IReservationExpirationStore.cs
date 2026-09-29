namespace OrderSystem.Application.Orders;

public enum ReservationExpirationOutcome
{
    Expired,
    Skipped
}

public sealed record ReservationExpirationSummary(
    int Examined,
    int Expired,
    int Skipped,
    int Failed);

public interface IReservationExpirationStore
{
    Task<IReadOnlyList<Guid>> ListCandidatesAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken
    );

    Task<ReservationExpirationOutcome> TryExpireAsync(
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
}

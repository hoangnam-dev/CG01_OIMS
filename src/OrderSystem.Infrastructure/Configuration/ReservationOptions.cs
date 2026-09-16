namespace OrderSystem.Infrastructure.Configuration;

public sealed class ReservationOptions
{
    public const string SectionName = "Reservation";

    public TimeSpan Duration { get; init; }

    public TimeSpan ExpirationScanInterval { get; init; }

    public int BatchSize { get; init; }
}

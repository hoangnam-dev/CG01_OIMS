namespace OrderSystem.Api.Authentication;

public sealed class AuthenticationWebOptions
{
    public const string SectionName = "Authentication";

    public const string RefreshRateLimitPolicy = "AuthRefresh";

    public int RefreshPermitLimit { get; init; }

    public TimeSpan RefreshWindow { get; init; }

    public TimeSpan RefreshTokenCleanupInterval { get; init; }

    public int RefreshTokenCleanupBatchSize { get; init; }

    public TimeSpan ExpiredRefreshTokenRetention { get; init; }
}

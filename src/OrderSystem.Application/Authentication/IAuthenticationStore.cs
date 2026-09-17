using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Authentication;

public interface IAuthenticationStore
{
    Task<bool> TryCreateUserAsync(User user, CancellationToken cancellationToken);

    Task<User?> FindUserByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task AddRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken);

    Task<RefreshRotationResult> RotateRefreshTokenAsync(
        string presentedTokenHash,
        Guid replacementTokenId,
        string replacementTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RevokeRefreshTokenSessionAsync(
        string presentedTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public enum RefreshRotationStatus
{
    Success,
    Invalid,
    Reused
}

public sealed record RefreshRotationResult(
    RefreshRotationStatus Status,
    User? User,
    DateTimeOffset? RefreshTokenExpiresAt)
{
    public static RefreshRotationResult Success(User user, DateTimeOffset expiresAt) =>
        new(RefreshRotationStatus.Success, user, expiresAt);

    public static RefreshRotationResult Invalid() =>
        new(RefreshRotationStatus.Invalid, null, null);

    public static RefreshRotationResult Reused() =>
        new(RefreshRotationStatus.Reused, null, null);
}

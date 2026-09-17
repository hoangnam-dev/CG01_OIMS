namespace OrderSystem.Domain.Users;

public sealed class RefreshToken
{
    public const int Sha256HexLength = 64;

    private RefreshToken()
    {
    }

    public RefreshToken(
        Guid id,
        Guid userId,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Refresh Token ID cannot be empty.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        if (tokenHash is null ||
            tokenHash.Length != Sha256HexLength ||
            tokenHash.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new ArgumentException(
                "Token hash must be a lowercase SHA-256 hexadecimal value.",
                nameof(tokenHash));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), expiresAt, "Expiry must be after creation.");
        }

        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

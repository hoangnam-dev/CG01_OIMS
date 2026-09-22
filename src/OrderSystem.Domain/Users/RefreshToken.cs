using OrderSystem.Domain.Common;

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
        Id = DomainGuard.RequiredGuid(id);
        UserId = DomainGuard.RequiredGuid(userId);

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

    public void Rotate(Guid replacementId, DateTimeOffset revokedAt)
    {
        if (replacementId == Guid.Empty || replacementId == Id)
        {
            throw new ArgumentException("Replacement token ID must identify another token.", nameof(replacementId));
        }

        Revoke(revokedAt);
        ReplacedByTokenId = replacementId;
    }

    public void Revoke(DateTimeOffset revokedAt)
    {
        if (revokedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(revokedAt), revokedAt, "Revocation cannot precede creation.");
        }

        RevokedAt ??= revokedAt;
    }
}

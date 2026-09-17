using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Users;

public sealed class RefreshTokenTests
{
    private const string TokenHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Constructor_ValidToken_PreservesUnrevokedPersistenceState()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = createdAt.AddDays(30);

        var token = new RefreshToken(id, userId, TokenHash, expiresAt, createdAt);

        Assert.Equal(id, token.Id);
        Assert.Equal(userId, token.UserId);
        Assert.Equal(TokenHash, token.TokenHash);
        Assert.Equal(expiresAt, token.ExpiresAt);
        Assert.Null(token.RevokedAt);
        Assert.Null(token.ReplacedByTokenId);
        Assert.Equal(createdAt, token.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("not-a-sha256-hash")]
    public void Constructor_NonCanonicalSha256Hash_Throws(string tokenHash)
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new RefreshToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            tokenHash,
            createdAt.AddDays(30),
            createdAt));
    }

    [Fact]
    public void Constructor_ExpiryNotAfterCreation_Throws()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RefreshToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TokenHash,
            createdAt,
            createdAt));
    }

    [Fact]
    public void Constructor_EmptyTokenId_Throws()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new RefreshToken(
            Guid.Empty,
            Guid.NewGuid(),
            TokenHash,
            createdAt.AddDays(30),
            createdAt));
    }

    [Fact]
    public void Constructor_EmptyUserId_Throws()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new RefreshToken(
            Guid.NewGuid(),
            Guid.Empty,
            TokenHash,
            createdAt.AddDays(30),
            createdAt));
    }

    [Fact]
    public void Rotate_ValidReplacement_RevokesAndLinksToken()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var token = new RefreshToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TokenHash,
            createdAt.AddDays(30),
            createdAt);
        var replacementId = Guid.NewGuid();
        var revokedAt = createdAt.AddMinutes(5);

        token.Rotate(replacementId, revokedAt);

        Assert.Equal(revokedAt, token.RevokedAt);
        Assert.Equal(replacementId, token.ReplacedByTokenId);
    }

    [Fact]
    public void Revoke_AlreadyRevoked_DoesNotChangeOriginalTimestamp()
    {
        var createdAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var token = new RefreshToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TokenHash,
            createdAt.AddDays(30),
            createdAt);
        var firstRevocation = createdAt.AddMinutes(5);

        token.Revoke(firstRevocation);
        token.Revoke(createdAt.AddMinutes(10));

        Assert.Equal(firstRevocation, token.RevokedAt);
    }
}

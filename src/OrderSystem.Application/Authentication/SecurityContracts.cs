using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Authentication;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string passwordHash);
}

public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(User user, DateTimeOffset issuedAt);
}

public interface IRefreshTokenProtector
{
    GeneratedRefreshToken Generate();

    string Hash(string token);
}

public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record GeneratedRefreshToken(string Value, string Hash);

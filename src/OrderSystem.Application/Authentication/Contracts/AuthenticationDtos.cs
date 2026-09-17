namespace OrderSystem.Application.Authentication.Contracts;

public sealed record RegisterRequest(string? Email, string? Password);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record UserDto(
    Guid Id,
    string Email,
    string Role,
    DateTimeOffset CreatedAt);

public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn);

public sealed record AuthenticationSession(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public TokenResponse ToResponse() =>
        new(AccessToken, TokenType, ExpiresIn);
}

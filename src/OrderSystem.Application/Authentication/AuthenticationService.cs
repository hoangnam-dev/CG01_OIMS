using System.Net.Mail;
using OrderSystem.Application.Authentication.Contracts;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Results;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Authentication;

public sealed class AuthenticationService(
    IAuthenticationStore store,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer accessTokenIssuer,
    IRefreshTokenProtector refreshTokenProtector,
    IClock clock)
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    public async Task<ApplicationResult<UserDto>> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateCredentials(request.Email, request.Password);
        if (validation is not null)
        {
            return ApplicationResult.Failure<UserDto>(validation);
        }

        var email = request.Email!.Trim();
        var now = clock.UtcNow;
        var user = new User(
            Guid.NewGuid(),
            email,
            NormalizeEmail(email),
            passwordHasher.Hash(request.Password!),
            UserRole.Customer,
            now);

        if (!await store.TryCreateUserAsync(user, cancellationToken))
        {
            return ApplicationResult.Failure<UserDto>(new(
                ApplicationErrorKind.Conflict,
                "REGISTRATION_CONFLICT",
                "The registration identity is unavailable."));
        }

        return ApplicationResult.Success(ToUserDto(user));
    }

    public async Task<ApplicationResult<AuthenticationSession>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateCredentials(request.Email, request.Password);
        if (validation is not null)
        {
            return ApplicationResult.Failure<AuthenticationSession>(validation);
        }

        var user = await store.FindUserByNormalizedEmailAsync(
            NormalizeEmail(request.Email!),
            cancellationToken);
        if (user is null || !passwordHasher.Verify(request.Password!, user.PasswordHash))
        {
            return InvalidCredentials();
        }

        var now = clock.UtcNow;
        var generatedRefreshToken = refreshTokenProtector.Generate();
        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            generatedRefreshToken.Hash,
            now.Add(RefreshTokenLifetime),
            now);
        await store.AddRefreshTokenAsync(refreshToken, cancellationToken);

        return ApplicationResult.Success(CreateTokenResponse(
            now,
            accessTokenIssuer.Issue(user, now),
            generatedRefreshToken.Value,
            refreshToken.ExpiresAt));
    }

    public async Task<ApplicationResult<AuthenticationSession>> RefreshAsync(
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return ApplicationResult.Failure<AuthenticationSession>(new(
                ApplicationErrorKind.Unauthorized,
                "INVALID_REFRESH_TOKEN",
                "The refresh token is invalid."));
        }

        var now = clock.UtcNow;
        var replacement = refreshTokenProtector.Generate();
        var rotation = await store.RotateRefreshTokenAsync(
            refreshTokenProtector.Hash(refreshToken),
            Guid.NewGuid(),
            replacement.Hash,
            now,
            cancellationToken);
        if (rotation.Status != RefreshRotationStatus.Success ||
            rotation.User is null ||
            rotation.RefreshTokenExpiresAt is null)
        {
            return ApplicationResult.Failure<AuthenticationSession>(new(
                ApplicationErrorKind.Unauthorized,
                "INVALID_REFRESH_TOKEN",
                "The refresh token is invalid."));
        }

        return ApplicationResult.Success(CreateTokenResponse(
            now,
            accessTokenIssuer.Issue(rotation.User, now),
            replacement.Value,
            rotation.RefreshTokenExpiresAt.Value));
    }

    public Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(refreshToken)
            ? Task.CompletedTask
            : store.RevokeRefreshTokenSessionAsync(
                refreshTokenProtector.Hash(refreshToken),
                clock.UtcNow,
                cancellationToken);

    private static ApplicationError? ValidateCredentials(string? email, string? password)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalizedInput = email?.Trim();
        if (string.IsNullOrEmpty(normalizedInput) ||
            !MailAddress.TryCreate(normalizedInput, out var parsed) ||
            !string.Equals(parsed.Address, normalizedInput, StringComparison.OrdinalIgnoreCase))
        {
            errors["email"] = ["A valid email address is required."];
        }

        var passwordLength = password?.EnumerateRunes().Count() ?? 0;
        if (passwordLength is < 8 or > 13)
        {
            errors["password"] = ["Password must contain between 8 and 13 characters."];
        }

        return errors.Count == 0
            ? null
            : new(
                ApplicationErrorKind.Validation,
                "VALIDATION_FAILED",
                "One or more validation errors occurred.",
                errors);
    }

    private static ApplicationResult<AuthenticationSession> InvalidCredentials() =>
        ApplicationResult.Failure<AuthenticationSession>(new(
            ApplicationErrorKind.Unauthorized,
            "UNAUTHORIZED",
            "The email or password is invalid."));

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static UserDto ToUserDto(User user) =>
        new(user.Id, user.Email, user.Role.ToString(), user.CreatedAt);

    private static AuthenticationSession CreateTokenResponse(
        DateTimeOffset issuedAt,
        IssuedAccessToken accessToken,
        string refreshToken,
        DateTimeOffset refreshTokenExpiresAt) =>
        new(
            accessToken.Value,
            "Bearer",
            checked((int)(accessToken.ExpiresAt - issuedAt).TotalSeconds),
            refreshToken,
            refreshTokenExpiresAt);
}

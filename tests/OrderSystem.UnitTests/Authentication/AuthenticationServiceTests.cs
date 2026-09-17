using OrderSystem.Application.Authentication;
using OrderSystem.Application.Authentication.Contracts;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Authentication;

public sealed class AuthenticationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Register_ValidCredentials_NormalizesEmailHashesPasswordAndCreatesCustomer()
    {
        var store = new FakeAuthenticationStore();
        var passwordHasher = new FakePasswordHasher();
        var service = CreateService(store, passwordHasher);

        var result = await service.RegisterAsync(
            new RegisterRequest("  Customer@Example.COM ", TestCredentials.ValidPassword),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(store.CreatedUser);
        Assert.Equal("Customer@Example.COM", store.CreatedUser.Email);
        Assert.Equal("customer@example.com", store.CreatedUser.NormalizedEmail);
        Assert.Equal($"hashed:{TestCredentials.ValidPassword}", store.CreatedUser.PasswordHash);
        Assert.Equal(UserRole.Customer, store.CreatedUser.Role);
        Assert.Equal("Customer", result.Value!.Role);
    }

    [Theory]
    [InlineData("1234567")]
    [InlineData("12345678901234")]
    public async Task Register_PasswordOutsideEightToThirteenCharacters_ReturnsValidationFailure(string password)
    {
        var service = CreateService(new FakeAuthenticationStore(), new FakePasswordHasher());

        var result = await service.RegisterAsync(
            new RegisterRequest("customer@example.com", password),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("password", result.Error.ValidationErrors!.Keys);
    }

    [Fact]
    public async Task Register_UnicodePassword_CountsCharactersRatherThanUtf16CodeUnits()
    {
        var store = new FakeAuthenticationStore();
        var service = CreateService(store, new FakePasswordHasher());

        var result = await service.RegisterAsync(
            new RegisterRequest("customer@example.com", "😀😀😀😀😀😀😀😀"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Login_UnknownEmailAndWrongPassword_ReturnSameGenericFailure()
    {
        var store = new FakeAuthenticationStore();
        var passwordHasher = new FakePasswordHasher();
        var service = CreateService(store, passwordHasher);

        var unknown = await service.LoginAsync(
            new LoginRequest("unknown@example.com", TestCredentials.ValidPassword),
            CancellationToken.None);
        store.UserToFind = CreateUser();
        passwordHasher.VerificationResult = false;
        var wrongPassword = await service.LoginAsync(
            new LoginRequest("customer@example.com", TestCredentials.AlternatePassword),
            CancellationToken.None);

        Assert.Equal("UNAUTHORIZED", unknown.Error!.Code);
        Assert.Equal(unknown.Error.Message, wrongPassword.Error!.Message);
    }

    [Fact]
    public async Task Login_ValidCredentials_IssuesAccessAndPersistsOnlyRefreshTokenHash()
    {
        var store = new FakeAuthenticationStore { UserToFind = CreateUser() };
        var passwordHasher = new FakePasswordHasher { VerificationResult = true };
        var service = CreateService(store, passwordHasher);

        var result = await service.LoginAsync(
            new LoginRequest("CUSTOMER@example.com", TestCredentials.ValidPassword),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value!.AccessToken);
        Assert.Equal("raw-refresh-token", result.Value.RefreshToken);
        Assert.NotNull(store.AddedRefreshToken);
        Assert.Equal(FakeRefreshTokenProtector.TokenHash, store.AddedRefreshToken.TokenHash);
        Assert.DoesNotContain("raw-refresh-token", store.AddedRefreshToken.TokenHash, StringComparison.Ordinal);
        Assert.Equal(Now.AddDays(30), store.AddedRefreshToken.ExpiresAt);
    }

    [Fact]
    public async Task Refresh_InvalidToken_ReturnsGenericInvalidRefreshToken()
    {
        var store = new FakeAuthenticationStore
        {
            RotationResult = RefreshRotationResult.Invalid()
        };
        var service = CreateService(store, new FakePasswordHasher());

        var result = await service.RefreshAsync(
            "invalid-token",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_REFRESH_TOKEN", result.Error!.Code);
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsRotatedTokenWithOriginalAbsoluteExpiry()
    {
        var user = CreateUser();
        var absoluteExpiry = Now.AddDays(12);
        var store = new FakeAuthenticationStore
        {
            RotationResult = RefreshRotationResult.Success(user, absoluteExpiry)
        };
        var service = CreateService(store, new FakePasswordHasher());

        var result = await service.RefreshAsync(
            "presented-refresh-token",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("raw-refresh-token", result.Value!.RefreshToken);
        Assert.Equal(absoluteExpiry, result.Value.RefreshTokenExpiresAt);
        Assert.Equal(FakeRefreshTokenProtector.TokenHash, store.ReplacementHash);
        Assert.Equal("hash:presented-refresh-token", store.PresentedHash);
    }

    private static AuthenticationService CreateService(
        FakeAuthenticationStore store,
        FakePasswordHasher passwordHasher) =>
        new(
            store,
            passwordHasher,
            new FakeAccessTokenIssuer(),
            new FakeRefreshTokenProtector(),
            new StubClock());

    private static User CreateUser() => new(
        Guid.NewGuid(),
        "customer@example.com",
        "customer@example.com",
        $"hashed:{TestCredentials.ValidPassword}",
        UserRole.Customer,
        Now.AddDays(-1));

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public bool VerificationResult { get; set; }

        public string Hash(string password) => $"hashed:{password}";

        public bool Verify(string password, string passwordHash) => VerificationResult;
    }

    private sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
    {
        public IssuedAccessToken Issue(User user, DateTimeOffset issuedAt) =>
            new("access-token", issuedAt.AddMinutes(15));
    }

    private sealed class FakeRefreshTokenProtector : IRefreshTokenProtector
    {
        public const string TokenHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public GeneratedRefreshToken Generate() => new("raw-refresh-token", TokenHash);

        public string Hash(string token) => $"hash:{token}";
    }

    private sealed class FakeAuthenticationStore : IAuthenticationStore
    {
        public User? CreatedUser { get; private set; }

        public User? UserToFind { get; set; }

        public RefreshToken? AddedRefreshToken { get; private set; }

        public RefreshRotationResult RotationResult { get; set; } = RefreshRotationResult.Invalid();

        public string? PresentedHash { get; private set; }

        public string? ReplacementHash { get; private set; }

        public Task<bool> TryCreateUserAsync(User user, CancellationToken cancellationToken)
        {
            CreatedUser = user;
            return Task.FromResult(true);
        }

        public Task<User?> FindUserByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
            Task.FromResult(UserToFind);

        public Task AddRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken)
        {
            AddedRefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task<RefreshRotationResult> RotateRefreshTokenAsync(
            string presentedTokenHash,
            Guid replacementTokenId,
            string replacementTokenHash,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            PresentedHash = presentedTokenHash;
            ReplacementHash = replacementTokenHash;
            return Task.FromResult(RotationResult);
        }

        public Task RevokeRefreshTokenSessionAsync(
            string presentedTokenHash,
            DateTimeOffset now,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

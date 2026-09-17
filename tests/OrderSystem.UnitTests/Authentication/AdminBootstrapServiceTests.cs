using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Authentication;

public sealed class AdminBootstrapServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnsureAdmin_MissingIdentity_CreatesAdminWithNormalizedEmailAndPasswordHash()
    {
        var store = new BootstrapStore();
        var service = new AdminBootstrapService(store, new PasswordHasher(), new Clock());

        var result = await service.EnsureAdminAsync(
            " Admin@Example.COM ",
            TestCredentials.ValidPassword,
            CancellationToken.None);

        Assert.Equal(AdminBootstrapResult.Created, result);
        Assert.NotNull(store.CreatedUser);
        Assert.Equal("Admin@Example.COM", store.CreatedUser.Email);
        Assert.Equal("admin@example.com", store.CreatedUser.NormalizedEmail);
        Assert.Equal($"hashed:{TestCredentials.ValidPassword}", store.CreatedUser.PasswordHash);
        Assert.Equal(UserRole.Admin, store.CreatedUser.Role);
        Assert.Equal(Now, store.CreatedUser.CreatedAt);
    }

    [Fact]
    public async Task EnsureAdmin_ExistingAdmin_IsIdempotentAndDoesNotReplacePassword()
    {
        var existing = CreateUser(UserRole.Admin, "original-hash");
        var store = new BootstrapStore { UserToFind = existing };
        var hasher = new PasswordHasher();
        var service = new AdminBootstrapService(store, hasher, new Clock());

        var result = await service.EnsureAdminAsync(
            existing.Email,
            TestCredentials.AlternatePassword,
            CancellationToken.None);

        Assert.Equal(AdminBootstrapResult.AlreadyExists, result);
        Assert.Null(store.CreatedUser);
        Assert.Equal(0, hasher.HashCalls);
        Assert.Equal("original-hash", existing.PasswordHash);
    }

    [Fact]
    public async Task EnsureAdmin_ExistingCustomer_ThrowsWithoutPromotionOrPasswordChange()
    {
        var existing = CreateUser(UserRole.Customer, "customer-hash");
        var store = new BootstrapStore { UserToFind = existing };
        var service = new AdminBootstrapService(store, new PasswordHasher(), new Clock());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureAdminAsync(
                existing.Email,
                TestCredentials.ValidPassword,
                CancellationToken.None));

        Assert.Contains("Customer", exception.Message, StringComparison.Ordinal);
        Assert.Equal(UserRole.Customer, existing.Role);
        Assert.Equal("customer-hash", existing.PasswordHash);
        Assert.Null(store.CreatedUser);
    }

    [Fact]
    public async Task EnsureAdmin_ConcurrentCreatorWins_ReReadsAndAcceptsPersistedAdmin()
    {
        var existingAdmin = CreateUser(UserRole.Admin, "other-process-hash");
        var store = new BootstrapStore
        {
            CreateResult = false,
            UserAfterCreateConflict = existingAdmin
        };
        var service = new AdminBootstrapService(store, new PasswordHasher(), new Clock());

        var result = await service.EnsureAdminAsync(
            existingAdmin.Email,
            TestCredentials.ValidPassword,
            CancellationToken.None);

        Assert.Equal(AdminBootstrapResult.AlreadyExists, result);
        Assert.Equal(2, store.FindCalls);
    }

    private static User CreateUser(UserRole role, string passwordHash) => new(
        Guid.NewGuid(),
        "admin@example.com",
        "admin@example.com",
        passwordHash,
        role,
        Now.AddDays(-1));

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class PasswordHasher : IPasswordHasher
    {
        public int HashCalls { get; private set; }

        public string Hash(string password)
        {
            HashCalls++;
            return $"hashed:{password}";
        }

        public bool Verify(string password, string passwordHash) => false;
    }

    private sealed class BootstrapStore : IAuthenticationStore
    {
        public User? UserToFind { get; init; }

        public User? UserAfterCreateConflict { get; init; }

        public bool CreateResult { get; init; } = true;

        public User? CreatedUser { get; private set; }

        public int FindCalls { get; private set; }

        public Task<bool> TryCreateUserAsync(User user, CancellationToken cancellationToken)
        {
            CreatedUser = user;
            return Task.FromResult(CreateResult);
        }

        public Task<User?> FindUserByNormalizedEmailAsync(
            string normalizedEmail,
            CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(FindCalls == 1 ? UserToFind : UserAfterCreateConflict);
        }

        public Task AddRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RefreshRotationResult> RotateRefreshTokenAsync(
            string presentedTokenHash,
            Guid replacementTokenId,
            string replacementTokenHash,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeRefreshTokenSessionAsync(
            string presentedTokenHash,
            DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

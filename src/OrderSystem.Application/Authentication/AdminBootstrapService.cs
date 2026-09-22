using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Authentication;

public sealed class AdminBootstrapService(
    IAuthenticationStore store,
    IPasswordHasher passwordHasher,
    IClock clock,
    IIdGenerator? idGenerator = null)
{
    private readonly IIdGenerator ids = idGenerator ?? new Uuid7IdGenerator();

    public async Task<AdminBootstrapResult> EnsureAdminAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var displayEmail = email.Trim();
        var normalizedEmail = displayEmail.ToLowerInvariant();
        var existing = await store.FindUserByNormalizedEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return ResolveExisting(existing);
        }

        var admin = new User(
            ids.NewId(),
            displayEmail,
            normalizedEmail,
            passwordHasher.Hash(password),
            UserRole.Admin,
            clock.UtcNow);
        if (await store.TryCreateUserAsync(admin, cancellationToken))
        {
            return AdminBootstrapResult.Created;
        }

        existing = await store.FindUserByNormalizedEmailAsync(normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Admin bootstrap identity could not be created.");
        return ResolveExisting(existing);
    }

    private static AdminBootstrapResult ResolveExisting(User user) =>
        user.Role == UserRole.Admin
            ? AdminBootstrapResult.AlreadyExists
            : throw new InvalidOperationException(
                "Admin bootstrap identity belongs to an existing Customer account.");
}

public enum AdminBootstrapResult
{
    Created,
    AlreadyExists
}

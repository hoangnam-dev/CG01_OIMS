using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Authentication;

internal sealed class EfAuthenticationStore(OrderSystemDbContext dbContext) : IAuthenticationStore
{
    public async Task<bool> TryCreateUserAsync(User user, CancellationToken cancellationToken)
    {
        dbContext.Users.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "uq_users_normalized_email"
            })
        {
            dbContext.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    public Task<User?> FindUserByNormalizedEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken) =>
        dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task AddRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken)
    {
        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<RefreshRotationResult> RotateRefreshTokenAsync(
        string presentedTokenHash,
        Guid replacementTokenId,
        string replacementTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var presented = await dbContext.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {presentedTokenHash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (presented is null || presented.ExpiresAt <= now)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RefreshRotationResult.Invalid();
        }

        if (presented.RevokedAt is not null)
        {
            if (presented.ReplacedByTokenId is not null)
            {
                await RevokeDescendantsAsync(presented.ReplacedByTokenId.Value, now, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return RefreshRotationResult.Reused();
            }

            await transaction.RollbackAsync(cancellationToken);
            return RefreshRotationResult.Invalid();
        }

        var replacement = new RefreshToken(
            replacementTokenId,
            presented.UserId,
            replacementTokenHash,
            presented.ExpiresAt,
            now);
        dbContext.RefreshTokens.Add(replacement);
        await dbContext.SaveChangesAsync(cancellationToken);
        presented.Rotate(replacement.Id, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == presented.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RefreshRotationResult.Success(user, presented.ExpiresAt);
    }

    public async Task RevokeRefreshTokenSessionAsync(
        string presentedTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var presented = await dbContext.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {presentedTokenHash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (presented is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        presented.Revoke(now);
        if (presented.ReplacedByTokenId is not null)
        {
            await RevokeDescendantsAsync(presented.ReplacedByTokenId.Value, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RevokeDescendantsAsync(
        Guid descendantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid? currentId = descendantId;
        while (currentId is not null)
        {
            var descendant = await dbContext.RefreshTokens
                .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE id = {currentId.Value} FOR UPDATE")
                .SingleAsync(cancellationToken);
            descendant.Revoke(now);
            currentId = descendant.ReplacedByTokenId;
        }
    }
}

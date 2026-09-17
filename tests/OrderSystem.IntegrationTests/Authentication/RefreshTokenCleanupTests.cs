using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Authentication;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class RefreshTokenCleanupTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task DeleteExpiredBatch_RemovesEligibleRowsInBoundedFkSafeBatches()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Database:ConnectionString", postgres.ConnectionString))));
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        dbContext.Users.Add(new User(
            userId,
            $"cleanup-{userId:N}@example.com",
            $"cleanup-{userId:N}@example.com",
            TestCredentials.CreateHashPlaceholder(),
            UserRole.Customer,
            now.AddDays(-40)));
        var chainReplacement = CreateToken(userId, 'b', now.AddDays(-3), now.AddDays(-33));
        var standaloneExpired = CreateToken(userId, 'c', now.AddDays(-3), now.AddDays(-33));
        var unexpired = CreateToken(userId, 'd', now.AddDays(1), now.AddDays(-29));
        dbContext.RefreshTokens.AddRange(chainReplacement, standaloneExpired, unexpired);
        await dbContext.SaveChangesAsync();
        var chainRoot = CreateToken(userId, 'a', now.AddDays(-3), now.AddDays(-33));
        chainRoot.Rotate(chainReplacement.Id, now.AddDays(-10));
        dbContext.RefreshTokens.Add(chainRoot);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var cleanup = scope.ServiceProvider.GetRequiredService<IRefreshTokenCleanupStore>();
        var cutoff = now.AddDays(-1);

        var deletedCounts = new[]
        {
            await cleanup.DeleteExpiredBatchAsync(cutoff, 1, CancellationToken.None),
            await cleanup.DeleteExpiredBatchAsync(cutoff, 1, CancellationToken.None),
            await cleanup.DeleteExpiredBatchAsync(cutoff, 1, CancellationToken.None),
            await cleanup.DeleteExpiredBatchAsync(cutoff, 1, CancellationToken.None)
        };

        Assert.Equal([1, 1, 1, 0], deletedCounts);
        var remaining = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId)
            .ToListAsync();
        Assert.Single(remaining);
        Assert.Equal(unexpired.Id, remaining[0].Id);
    }

    private static RefreshToken CreateToken(
        Guid userId,
        char hashCharacter,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt) =>
        new(
            Guid.NewGuid(),
            userId,
            new string(hashCharacter, RefreshToken.Sha256HexLength),
            expiresAt,
            createdAt);
}

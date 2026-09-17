using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class AdminBootstrapTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Startup_BootstrapDisabled_DoesNotCreateConfiguredIdentity()
    {
        await MigrateDatabase();
        var email = $"disabled-{Guid.NewGuid():N}@example.com";
        await using var factory = CreateFactory(
            "Development",
            enabled: false,
            email,
            TestCredentials.ValidPassword);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
        Assert.False(await UserExists(factory, email));
    }

    [Fact]
    public async Task Startup_BootstrapEnabled_CreatesLoginReadyAdminAndIsIdempotentAcrossRestarts()
    {
        await MigrateDatabase();
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var password = TestCredentials.ValidPassword;

        await using (var firstFactory = CreateFactory("Development", enabled: true, email, password))
        {
            using var client = firstFactory.CreateClient();
            using var response = await client.GetAsync("/health/live");
            response.EnsureSuccessStatusCode();

            using var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
            login.EnsureSuccessStatusCode();
        }

        await using (var secondFactory = CreateFactory("Development", enabled: true, email, password))
        {
            using var client = secondFactory.CreateClient();
            using var response = await client.GetAsync("/health/live");
            response.EnsureSuccessStatusCode();

            using var scope = secondFactory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
            var users = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.NormalizedEmail == email)
                .ToListAsync();
            var admin = Assert.Single(users);
            Assert.Equal(UserRole.Admin, admin.Role);
            Assert.True(scope.ServiceProvider.GetRequiredService<IPasswordHasher>()
                .Verify(password, admin.PasswordHash));
        }
    }

    [Fact]
    public async Task Startup_BootstrapIdentityBelongsToCustomer_FailsWithoutPromotion()
    {
        await MigrateDatabase();
        var email = $"customer-{Guid.NewGuid():N}@example.com";
        await SeedCustomer(email);
        await using var factory = CreateFactory(
            "Test",
            enabled: true,
            email,
            TestCredentials.ValidPassword);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.CreateClient().GetAsync("/health/live"));

        Assert.Contains("Customer", exception.ToString(), StringComparison.Ordinal);
        await using var dbContext = CreateDbContext();
        var persisted = await dbContext.Users.AsNoTracking()
            .SingleAsync(user => user.NormalizedEmail == email);
        Assert.Equal(UserRole.Customer, persisted.Role);
    }

    [Fact]
    public async Task Startup_BootstrapEnabledInProduction_IsRejectedBeforeServingRequests()
    {
        await using var factory = CreateFactory(
            "Production",
            enabled: true,
            $"production-{Guid.NewGuid():N}@example.com",
            TestCredentials.ValidPassword);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.CreateClient().GetAsync("/health/live"));

        Assert.Contains("AdminBootstrap", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Development or Test", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string environment,
        bool enabled,
        string email,
        string password) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new("Database:ConnectionString", postgres.ConnectionString),
                    new("AdminBootstrap:Enabled", enabled.ToString()),
                    new("AdminBootstrap:Email", email),
                    new("AdminBootstrap:Password", password)));
        });

    private async Task MigrateDatabase()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    private async Task SeedCustomer(string email)
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(new User(
            Guid.NewGuid(),
            email,
            email,
            BCrypt.Net.BCrypt.HashPassword(TestCredentials.AlternatePassword, 12),
            UserRole.Customer,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    private OrderSystemDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrderSystemDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;
        return new OrderSystemDbContext(options);
    }

    private static async Task<bool> UserExists(WebApplicationFactory<Program> factory, string normalizedEmail)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>()
            .Users
            .AsNoTracking()
            .AnyAsync(user => user.NormalizedEmail == normalizedEmail);
    }
}

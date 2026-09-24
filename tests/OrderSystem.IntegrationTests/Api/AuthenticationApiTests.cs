using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class AuthenticationApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "API-AUTH-001")]
    public async Task Register_ValidCredentials_CreatesCustomerWithBcryptHash()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var email = $"Customer-{Guid.NewGuid():N}@Example.com";

        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $" {email} ",
            password = TestCredentials.ValidPassword,
            role = "Admin"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Customer", document.RootElement.GetProperty("data").GetProperty("role").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var normalizedEmail = email.ToLowerInvariant();
        var user = await dbContext.Users.AsNoTracking().SingleAsync(row => row.NormalizedEmail == normalizedEmail);
        Assert.Equal("Customer", user.Role.ToString());
        Assert.StartsWith("$2", user.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain(TestCredentials.ValidPassword, user.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_DuplicateNormalizedEmail_ReturnsConflict()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var local = Guid.NewGuid().ToString("N", null);

        using var first = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"{local}@example.com",
            password = TestCredentials.ValidPassword
        });
        using var duplicate = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $" {local.ToUpperInvariant()}@EXAMPLE.COM ",
            password = TestCredentials.AlternatePassword
        });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("REGISTRATION_CONFLICT", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Requirement", "API-AUTH-002")]
    public async Task Login_UnknownEmailAndWrongPassword_ReturnSameGenericUnauthorizedProblem()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);

        using var unknown = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = $"missing-{Guid.NewGuid():N}@example.com",
            password = TestCredentials.ValidPassword
        });
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = TestCredentials.AlternatePassword
        });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var unknownProblem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        using var wrongProblem = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync());
        Assert.Equal("UNAUTHORIZED", unknownProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            unknownProblem.RootElement.GetProperty("message").GetString(),
            wrongProblem.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Password_HasNoCompositionRequirementButRemainsCaseSensitive()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var email = $"password-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.LowercasePassword);

        using var wrongCase = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = TestCredentials.LowercasePassword.ToUpperInvariant()
        });
        var correctCase = await Login(client, email, TestCredentials.LowercasePassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongCase.StatusCode);
        Assert.NotEmpty(correctCase.Data.GetProperty("accessToken").GetString()!);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsJwtAndStoresOnlyRefreshHash()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var email = $"tokens-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);

        var login = await Login(client, email, TestCredentials.ValidPassword);
        var tokens = login.Data;

        Assert.Equal(
            ["accessToken", "expiresIn", "tokenType"],
            tokens.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());
        var encodedAccessToken = tokens.GetProperty("accessToken").GetString()!;
        Assert.Equal(2, encodedAccessToken.Count(character => character == '.'));
        Assert.Equal(900, tokens.GetProperty("expiresIn").GetInt32());
        Assert.False(tokens.TryGetProperty("user", out _));
        Assert.False(tokens.TryGetProperty("accessTokenExpiresAt", out _));
        Assert.False(tokens.TryGetProperty("refreshToken", out _));
        Assert.False(tokens.TryGetProperty("refreshTokenExpiresAt", out _));
        Assert.Contains("HttpOnly", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Path=/api/auth", login.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", login.CacheControl);
        Assert.Equal("no-cache", login.Pragma);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var userId = await dbContext.Users
            .Where(user => user.NormalizedEmail == email)
            .Select(user => user.Id)
            .SingleAsync();
        var accessToken = new JwtSecurityTokenHandler().ReadJwtToken(encodedAccessToken);
        Assert.Equal("HS256", accessToken.Header.Alg);
        Assert.Equal("OrderSystem.Api", accessToken.Issuer);
        Assert.Equal("OrderSystem.Client", Assert.Single(accessToken.Audiences));
        Assert.Equal(userId.ToString(), accessToken.Claims.Single(claim => claim.Type == "sub").Value);
        Assert.Equal(email, accessToken.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal("Customer", accessToken.Claims.Single(claim => claim.Type == ClaimTypes.Role).Value);
        Assert.NotEmpty(accessToken.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Jti).Value);
        Assert.Equal(TimeSpan.FromMinutes(15), accessToken.ValidTo - accessToken.ValidFrom);
        var persisted = await dbContext.RefreshTokens.AsNoTracking().SingleAsync(token => token.UserId == userId);
        Assert.Equal(64, persisted.TokenHash.Length);
        Assert.NotEqual(login.RefreshToken, persisted.TokenHash);
    }

    [Fact]
    public async Task Login_ReturnsConfiguredAccessTokenLifetimeInSeconds()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory(new FakeClock(now));
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var email = $"lifetime-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);

        var tokens = (await Login(client, email, TestCredentials.ValidPassword)).Data;

        Assert.Equal(900, tokens.GetProperty("expiresIn").GetInt32());
    }

    [Fact]
    [Trait("Requirement", "API-AUTH-003")]
    public async Task Refresh_RotatesTokenAndReuseRevokesOnlyItsDescendantSession()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var firstClient = CreateHttpsClient(factory);
        using var independentClient = CreateHttpsClient(factory);
        var email = $"rotate-{Guid.NewGuid():N}@example.com";
        await Register(firstClient, email, TestCredentials.ValidPassword);
        var firstSession = await Login(firstClient, email, TestCredentials.ValidPassword);
        await Login(independentClient, email, TestCredentials.ValidPassword);

        using var rotatedResponse = await firstClient.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        using var rotatedDocument = JsonDocument.Parse(await rotatedResponse.Content.ReadAsStringAsync());
        var rotatedTokens = rotatedDocument.RootElement.GetProperty("data");
        Assert.Equal(
            ["accessToken", "expiresIn", "tokenType"],
            rotatedTokens.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(900, rotatedTokens.GetProperty("expiresIn").GetInt32());
        var replacementCookie = GetRefreshCookie(rotatedResponse);
        Assert.NotEqual(firstSession.RefreshToken, replacementCookie);

        using var attackerClient = CreateHttpsClient(factory);
        using var reuseRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", firstSession.RefreshToken);
        using var reuseResponse = await attackerClient.SendAsync(reuseRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);
        using var reuseProblem = JsonDocument.Parse(await reuseResponse.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_REFRESH_TOKEN", reuseProblem.RootElement.GetProperty("code").GetString());

        using var descendantResponse = await firstClient.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, descendantResponse.StatusCode);

        using var independentResponse = await independentClient.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, independentResponse.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-AUTH-003")]
    public async Task Refresh_InvalidAndExpiredTokens_ReturnGenericUnauthorized()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        await MigrateDatabase(factory);
        using var client = CreateHttpsClient(factory);
        var email = $"refresh-expiry-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);
        await Login(client, email, TestCredentials.ValidPassword);

        using var invalidClient = CreateHttpsClient(factory);
        using var invalidRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", "not-a-refresh-token");
        using var invalid = await invalidClient.SendAsync(invalidRequest);
        clock.UtcNow = clock.UtcNow.AddDays(31);
        using var expired = await client.PostAsync("/api/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var invalidProblem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        using var expiredProblem = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_REFRESH_TOKEN", invalidProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal("INVALID_REFRESH_TOKEN", expiredProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refresh_BodyTokenWithoutCookie_IsRejected()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = CreateHttpsClient(factory);

        using var response = await client.PostAsJsonAsync("/api/auth/refresh", new
        {
            refreshToken = "body-token-must-not-be-used"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ConcurrentSameToken_AllowsExactlyOneRotation()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var loginClient = CreateHttpsClient(factory);
        var email = $"concurrent-{Guid.NewGuid():N}@example.com";
        await Register(loginClient, email, TestCredentials.ValidPassword);
        var login = await Login(loginClient, email, TestCredentials.ValidPassword);
        using var firstClient = CreateHttpsClient(factory);
        using var secondClient = CreateHttpsClient(factory);
        using var firstRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", login.RefreshToken);
        using var secondRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", login.RefreshToken);

        var responses = await Task.WhenAll(
            firstClient.SendAsync(firstRequest),
            secondClient.SendAsync(secondRequest));

        try
        {
            var statuses = string.Join(", ", responses.Select(response =>
                $"{(int)response.StatusCode} {response.StatusCode}"));

            Assert.True(
                responses.Count(response => response.StatusCode == HttpStatusCode.OK) == 1,
                $"Exactly one concurrent refresh must succeed. Actual statuses: {statuses}");
            Assert.True(
                responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized) == 1,
                $"Exactly one concurrent refresh must be rejected. Actual statuses: {statuses}");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Logout_RevokesSessionClearsCookieAndIsIdempotent()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = CreateHttpsClient(factory);
        var email = $"logout-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);
        var login = await Login(client, email, TestCredentials.ValidPassword);

        using var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var deletionCookie = logout.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("__Secure-oims-refresh=", deletionCookie, StringComparison.Ordinal);
        Assert.Contains("expires=", deletionCookie, StringComparison.OrdinalIgnoreCase);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persisted = await dbContext.RefreshTokens
            .AsNoTracking()
            .SingleAsync(token => token.TokenHash.Length == 64 && token.UserId == dbContext.Users
                .Where(user => user.NormalizedEmail == email)
                .Select(user => user.Id)
                .Single());
        Assert.NotNull(persisted.RevokedAt);

        using var staleClient = CreateHttpsClient(factory);
        using var staleRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", login.RefreshToken);
        using var staleRefresh = await staleClient.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, staleRefresh.StatusCode);

        using var secondLogout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, secondLogout.StatusCode);
    }

    [Fact]
    public async Task Refresh_ExceedingConfiguredIpLimit_ReturnsTooManyRequests()
    {
        await using var factory = CreateFactory(refreshPermitLimit: 2);
        await MigrateDatabase(factory);
        using var client = CreateHttpsClient(factory);

        using var firstRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", "invalid-one");
        using var first = await client.SendAsync(firstRequest);
        using var secondRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", "invalid-two");
        using var second = await client.SendAsync(secondRequest);
        using var thirdRequest = CreateCookieRequest(HttpMethod.Post, "/api/auth/refresh", "invalid-three");
        using var third = await client.SendAsync(thirdRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        using var problem = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        Assert.Equal("RATE_LIMITED", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Requirement", "API-AUTHZ-001")]
    public async Task ProductMutation_AnonymousIsUnauthorizedAndCustomerIsForbidden()
    {
        await using var factory = CreateFactory();
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var payload = new { name = $"Denied-{Guid.NewGuid():N}", description = "Denied" };

        using var anonymous = await client.PostAsJsonAsync("/api/products", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("application/problem+json", anonymous.Content.Headers.ContentType?.MediaType);
        using (var problem = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()))
        {
            Assert.Equal("UNAUTHORIZED", problem.RootElement.GetProperty("code").GetString());
        }

        var email = $"customer-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);
        var login = await Login(client, email, TestCredentials.ValidPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            login.Data.GetProperty("accessToken").GetString());

        using var customer = await client.PostAsJsonAsync("/api/products", payload);
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
        Assert.Equal("application/problem+json", customer.Content.Headers.ContentType?.MediaType);
        using var forbiddenProblem = JsonDocument.Parse(await customer.Content.ReadAsStringAsync());
        Assert.Equal("FORBIDDEN", forbiddenProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProductMutation_ExpiredAccessToken_IsRejectedBeforeMutation()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow.AddHours(-1));
        await using var factory = CreateFactory(clock);
        await MigrateDatabase(factory);
        using var client = factory.CreateClient();
        var email = $"expired-{Guid.NewGuid():N}@example.com";
        await Register(client, email, TestCredentials.ValidPassword);
        var login = await Login(client, email, TestCredentials.ValidPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            login.Data.GetProperty("accessToken").GetString());
        var name = $"Expired-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync("/api/products", new
        {
            name,
            description = "Must not persist"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        Assert.False(await dbContext.Products.AsNoTracking().AnyAsync(product => product.Name == name));
    }

    private WebApplicationFactory<Program> CreateFactory(
        IClock? clock = null,
        int? refreshPermitLimit = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = postgres.ConnectionString
                };
                if (refreshPermitLimit is not null)
                {
                    settings["Authentication:RefreshPermitLimit"] = refreshPermitLimit.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                configuration.AddOimsTestConfiguration(settings.ToArray());
            });
            if (clock is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton(clock);
                });
            }
        });

    private static async Task MigrateDatabase(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>().Database.MigrateAsync();
    }

    private static async Task Register(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<LoginResult> Login(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new(
            document.RootElement.GetProperty("data").Clone(),
            response.Headers.GetValues("Set-Cookie").Single(),
            GetRefreshCookie(response),
            response.Headers.CacheControl?.ToString(),
            string.Join(",", response.Headers.Pragma.Select(value => value.Name)));
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static HttpRequestMessage CreateCookieRequest(HttpMethod method, string path, string refreshToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"__Secure-oims-refresh={refreshToken}");
        return request;
    }

    private static string GetRefreshCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("__Secure-oims-refresh=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0].Split('=', 2)[1];
    }

    private sealed record LoginResult(
        JsonElement Data,
        string SetCookie,
        string RefreshToken,
        string? CacheControl,
        string Pragma);
}

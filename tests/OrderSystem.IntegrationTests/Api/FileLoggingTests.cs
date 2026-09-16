using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Api.Diagnostics;
using OrderSystem.Infrastructure.Logging;
using Serilog;
using Serilog.Context;

namespace OrderSystem.IntegrationTests.Api;

[Collection(LoggingCollectionDefinition.Name)]
public sealed class FileLoggingTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"oims-logs-{Guid.NewGuid():N}");

    [Fact]
    [Trait("Requirement", "NFR-007")]
    public async Task RequestLog_WritesCorrelationToRollingFile_AndDeletesExpiredFile()
    {
        Directory.CreateDirectory(_directory);
        var expiredFile = Path.Combine(_directory, $"oims-{DateTime.UtcNow.AddDays(-31):yyyyMMdd}.log");
        await File.WriteAllTextAsync(expiredFile, "expired");
        File.SetLastWriteTimeUtc(expiredFile, DateTime.UtcNow.AddDays(-31));
        var logPath = Path.Combine(_directory, "oims-.log");
        var correlationId = Guid.NewGuid().ToString();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:FilePath"] = logPath,
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
                ["Serilog:MinimumLevel:Override:OrderSystem.Api.Noisy"] = "Error",
                ["Serilog:Enrich:0"] = "FromLogContext"
            })
            .Build();
        using (var logger = new LoggerConfiguration()
            .ConfigureOimsLogging(configuration, "OrderSystem.Api")
            .CreateLogger())
        {
            using (LogContext.PushProperty("CorrelationId", correlationId))
            {
                logger
                    .ForContext("SourceContext", "OrderSystem.Api.Noisy")
                    .Warning("This warning must be filtered by appsettings");
                logger
                    .ForContext("SourceContext", "OrderSystem.Api.Noisy")
                    .Error("Configured error");
            }
        }

        var currentFile = Directory.GetFiles(_directory, "oims-*.log")
            .Single(path => !string.Equals(path, expiredFile, StringComparison.OrdinalIgnoreCase));
        var contents = await File.ReadAllTextAsync(currentFile);
        Assert.Contains(correlationId, contents, StringComparison.Ordinal);
        Assert.Contains("Configured error", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("This warning must be filtered by appsettings", contents, StringComparison.Ordinal);
        Assert.False(File.Exists(expiredFile));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    [Trait("Requirement", "NFR-007")]
    public async Task AuthenticatedRequest_WritesTrustedUserIdToRequestLog()
    {
        Directory.CreateDirectory(_directory);
        var logPath = Path.Combine(_directory, "authenticated-request-.log");
        var userId = Guid.NewGuid().ToString();

        await using (var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Serilog:FilePath"] = logPath
                    }));
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new UserPrincipalStartupFilter(
                        ClaimTypes.NameIdentifier,
                        userId,
                        isAuthenticated: true)));
            }))
        using (var client = factory.CreateClient())
        using (var response = await client.GetAsync("/api/foundation/success"))
        {
            response.EnsureSuccessStatusCode();
        }

        var logFile = Directory.GetFiles(_directory, "authenticated-request-*.log").Single();
        await using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync();
        Assert.Contains($"[{userId}]", contents, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "NFR-007")]
    public async Task AuthenticatedRequest_WithJwtSubjectClaim_WritesUserIdToRequestLog()
    {
        Directory.CreateDirectory(_directory);
        var logPath = Path.Combine(_directory, "jwt-subject-request-.log");
        var userId = Guid.NewGuid().ToString();

        await using (var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Serilog:FilePath"] = logPath
                    }));
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new UserPrincipalStartupFilter(
                        "sub",
                        userId,
                        isAuthenticated: true)));
            }))
        using (var client = factory.CreateClient())
        using (var response = await client.GetAsync("/api/foundation/success"))
        {
            response.EnsureSuccessStatusCode();
        }

        var logFile = Directory.GetFiles(_directory, "jwt-subject-request-*.log").Single();
        await using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync();
        Assert.Contains($"[{userId}]", contents, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "NFR-007")]
    public async Task AnonymousRequest_DoesNotTrustUserIdClaim()
    {
        Directory.CreateDirectory(_directory);
        var logPath = Path.Combine(_directory, "anonymous-request-.log");
        var forgedUserId = Guid.NewGuid().ToString();

        await using (var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Serilog:FilePath"] = logPath
                    }));
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new UserPrincipalStartupFilter(
                        ClaimTypes.NameIdentifier,
                        forgedUserId,
                        isAuthenticated: false)));
            }))
        using (var client = factory.CreateClient())
        using (var response = await client.GetAsync("/api/foundation/success"))
        {
            response.EnsureSuccessStatusCode();
        }

        var logFile = Directory.GetFiles(_directory, "anonymous-request-*.log").Single();
        await using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync();
        Assert.Contains($"[{UserContextLoggingMiddleware.AnonymousUserId}]", contents, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{forgedUserId}]", contents, StringComparison.Ordinal);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class UserPrincipalStartupFilter(string claimType, string userId, bool isAuthenticated) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, nextMiddleware) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(claimType, userId)],
                        authenticationType: isAuthenticated ? "IntegrationTest" : null));
                    await nextMiddleware(context);
                });
                next(application);
            };
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoggingCollectionDefinition
{
    public const string Name = "Serilog integration";
}

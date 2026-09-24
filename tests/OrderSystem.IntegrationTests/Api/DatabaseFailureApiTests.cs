using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

public sealed class DatabaseFailureApiTests
{
    [Fact]
    public async Task NpgsqlFailure_ReturnsSanitizedDependencyProblemWithCorrelationId()
    {
        var correlationId = Guid.NewGuid().ToString();
        await using var factory = CreateFactory(new NpgsqlException("Host=db Password=secret"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/foundation/success");
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("DEPENDENCY_UNAVAILABLE", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(correlationId, document.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task DeadlockFailure_ReturnsSanitizedInternalProblem()
    {
        await using var factory = CreateFactory(new PostgresException("SELECT password FROM users", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/foundation/success");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("INTERNAL_ERROR", document.RootElement.GetProperty("code").GetString());
    }

    private static WebApplicationFactory<Program> CreateFactory(Exception exception) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddOimsTestConfiguration());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOperationHook>();
                services.AddSingleton<IOperationHook>(new ThrowingHook(exception));
            });
        });

    private sealed class ThrowingHook(Exception exception) : IOperationHook
    {
        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken) => Task.FromException(exception);
    }
}

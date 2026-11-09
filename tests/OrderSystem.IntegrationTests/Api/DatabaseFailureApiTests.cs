using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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

[Collection(LoggingCollectionDefinition.Name)]
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

    [Fact]
    [Trait("Requirement", "S11-T05")]
    public async Task NpgsqlFailure_LogPreservesCorrelationWithoutConnectionSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"oims-database-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "database-.log");
        var correlationId = Guid.NewGuid().ToString("D");
        var secret = $"test-secret-{Guid.NewGuid():N}";
        try
        {
            await using (var factory = CreateFactory(new NpgsqlException($"Host=db;Password={secret}"), logPath))
            using (var client = factory.CreateClient())
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/foundation/success"))
            {
                request.Headers.Add("X-Correlation-ID", correlationId);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            }

            var file = Assert.Single(Directory.GetFiles(directory, "database-*.log"));
            var contents = await File.ReadAllTextAsync(file);
            Assert.Contains(correlationId, contents, StringComparison.Ordinal);
            Assert.Contains("DEPENDENCY_UNAVAILABLE", contents, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Requirement", "S11-T05")]
    public async Task ExceptionHandler_RecordsOnlyBoundedFailureClassifications()
    {
        var measurements = new ConcurrentQueue<(long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OrderSystem.Api.Failures" && instrument.Name == "oims.api.failures")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => measurements.Enqueue((value, tags.ToArray())));
        listener.Start();

        var cases = new (Exception Exception, HttpStatusCode Status)[]
        {
            (new NpgsqlException("Host=db Password=test-secret"), HttpStatusCode.ServiceUnavailable),
            (new PostgresException("deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected), HttpStatusCode.InternalServerError),
            (new InvalidOperationException("unexpected failure"), HttpStatusCode.InternalServerError)
        };
        foreach (var (exception, status) in cases)
        {
            await using var factory = CreateFactory(exception);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/foundation/success");
            Assert.Equal(status, response.StatusCode);
        }

        var classifications = measurements
            .Select(measurement =>
            {
                Assert.Equal(1, measurement.Value);
                var tag = Assert.Single(measurement.Tags);
                Assert.Equal("classification", tag.Key);
                return Assert.IsType<string>(tag.Value);
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["database_contention", "dependency_unavailable", "unexpected"], classifications);
    }

    private static WebApplicationFactory<Program> CreateFactory(Exception exception, string? logPath = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>("Serilog:FilePath", logPath)));
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

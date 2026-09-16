using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Common.Diagnostics;

namespace OrderSystem.IntegrationTests.Api;

public sealed class FoundationApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public FoundationApiContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-005")]
    public async Task BodySuccess_ReturnsEnvelopeWithoutDuplicatingHttpStatus()
    {
        using var response = await _client.GetAsync("/api/foundation/success");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", document.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("metadata").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("status", out _));
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-006")]
    public async Task PaginatedSuccess_ReturnsArrayAndPaginationMetadata()
    {
        using var response = await _client.GetAsync("/api/foundation/paginated");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("data").ValueKind);
        var pagination = document.RootElement.GetProperty("metadata").GetProperty("pagination");
        Assert.Equal(1, pagination.GetProperty("page").GetInt32());
        Assert.Equal(20, pagination.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, pagination.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, pagination.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-007")]
    public async Task NoContentSuccess_HasEmptyBody()
    {
        using var response = await _client.DeleteAsync("/api/foundation/no-content");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-001")]
    [Trait("Requirement", "API-CONTRACT-008")]
    public async Task InvalidRequest_ReturnsUnwrappedValidationProblemDetails()
    {
        using var response = await _client.PostAsJsonAsync("/api/foundation/validate", new { name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("errors", out _));
        Assert.False(document.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-002")]
    [Trait("Requirement", "API-CONTRACT-008")]
    public async Task UnexpectedFailure_ReturnsSanitizedProblemDetails()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOperationHook>();
                services.AddSingleton<IOperationHook, ThrowingOperationHook>();
            }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/foundation/success");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INTERNAL_ERROR", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingOperationHook : IOperationHook
    {
        public Task ReachAsync(string checkpoint, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostic failure with password=must-not-leak");
    }
}

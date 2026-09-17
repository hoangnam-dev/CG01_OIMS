using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OrderSystem.IntegrationTests.Api;

public sealed class DiagnosticsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DiagnosticsTests(WebApplicationFactory<Program> factory) => _client = factory
        .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = AuthenticationApiTests.TestSigningKey
            })))
        .CreateClient();

    [Fact]
    [Trait("Requirement", "API-CONTRACT-003")]
    public async Task CorrelationId_WhenSupplied_ReturnsSameUuid()
    {
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/foundation/success");
        request.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-003")]
    public async Task CorrelationId_WhenAbsent_ReturnsGeneratedUuid()
    {
        using var response = await _client.GetAsync("/api/foundation/success");

        response.EnsureSuccessStatusCode();
        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Correlation-ID").Single(), out _));
    }

    [Fact]
    [Trait("Requirement", "API-CONTRACT-003")]
    public async Task CorrelationId_WhenInvalid_ReturnsValidationProblem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/foundation/success");
        request.Headers.Add("X-Correlation-ID", "not-a-uuid");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ReturnsAHealthResponse(string path)
    {
        using var response = await _client.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_IsDiscoverable()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var successOperation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/foundation/success")
            .GetProperty("get");
        Assert.Contains(
            successOperation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "X-Correlation-ID");
        Assert.True(successOperation
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .TryGetProperty("schema", out _));

        var validationResponse = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/foundation/validate")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("400");
        Assert.True(validationResponse
            .GetProperty("content")
            .GetProperty("application/problem+json")
            .TryGetProperty("schema", out _));
    }

    [Fact]
    public async Task OpenApiDocument_DescribesBearerAuthenticationOnlyForProtectedOperations()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var bearer = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        var paths = document.RootElement.GetProperty("paths");
        var protectedSecurity = paths.GetProperty("/api/products").GetProperty("post").GetProperty("security");
        Assert.Contains(
            protectedSecurity.EnumerateArray(),
            requirement => requirement.TryGetProperty("Bearer", out _));
        Assert.False(paths.GetProperty("/api/auth/login").GetProperty("post").TryGetProperty("security", out _));
    }

    [Fact]
    public async Task SwaggerUi_ReferencesConfiguredOpenApiDocument()
    {
        using var uiResponse = await _client.GetAsync("/swagger/index.html");
        using var configurationResponse = await _client.GetAsync("/swagger/index.js");

        uiResponse.EnsureSuccessStatusCode();
        configurationResponse.EnsureSuccessStatusCode();
        var configuration = await configurationResponse.Content.ReadAsStringAsync();
        Assert.Contains("/openapi/v1.json", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("/swagger/v1/swagger.json", configuration, StringComparison.Ordinal);
    }
}

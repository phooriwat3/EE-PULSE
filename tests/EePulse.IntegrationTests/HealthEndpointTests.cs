using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EePulse.Contracts;
using EePulse.Contracts.Health;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EePulse.IntegrationTests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LivenessReturnsVersionedUtcContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/health/live", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(ApiVersions.Current, body.SchemaVersion);
        Assert.Equal("ee-pulse-api", body.Service);
        Assert.Equal("live", body.Status);
        Assert.Equal(TimeSpan.Zero, body.CheckedAt.Offset);
    }

    [Fact]
    public async Task ReadinessAndInventoryFailSafelyWithoutPostgresConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var readiness = await _client.GetAsync("/health/ready", cancellationToken);
        var body = await readiness.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("not-ready", body.Status);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices");
        request.Headers.Add("X-EE-Pulse-Role", "Viewer");
        var inventory = await _client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, inventory.StatusCode);
    }

    [Fact]
    public async Task CorrelationIdIsPreservedOnResponse()
    {
        const string correlationId = "integration-test-correlation";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", correlationId);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task OpenApiEndpointGeneratesVersionedDocument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/openapi/v1.json", cancellationToken);
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/health/live", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/health/ready", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/devices", out var devices));
        Assert.True(devices.GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        Assert.True(devices.GetProperty("post").GetProperty("responses").TryGetProperty("201", out var created));
        Assert.True(created.GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/devices/import/preview", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/agent-groups", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/probes", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/maintenance-windows", out _));

        var bearer = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Contains("OIDC", bearer.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("X-EE-Pulse-Role", bearer.GetProperty("description").GetString(), StringComparison.Ordinal);

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject()
                     .Where(candidate => candidate.Name.StartsWith("/api/v1/", StringComparison.Ordinal)))
        {
            foreach (var operation in path.Value.EnumerateObject().Where(candidate =>
                         candidate.Name is "get" or "post" or "put" or "delete" or "patch"))
            {
                var security = operation.Value.GetProperty("security");
                Assert.Contains(security.EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
                var responses = operation.Value.GetProperty("responses");
                Assert.True(responses.TryGetProperty("401", out _), $"{operation.Name.ToUpperInvariant()} {path.Name} lacks 401.");
                Assert.True(responses.TryGetProperty("403", out _), $"{operation.Name.ToUpperInvariant()} {path.Name} lacks 403.");
            }
        }

        Assert.False(document.RootElement.GetProperty("paths").GetProperty("/health/live").GetProperty("get")
            .TryGetProperty("security", out _));
    }
}

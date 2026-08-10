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

    [Theory]
    [InlineData("/health/live", "live")]
    [InlineData("/health/ready", "ready")]
    public async Task HealthEndpointReturnsVersionedUtcContract(string path, string expectedStatus)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(ApiVersions.Current, body.SchemaVersion);
        Assert.Equal("ee-pulse-api", body.Service);
        Assert.Equal(expectedStatus, body.Status);
        Assert.Equal(TimeSpan.Zero, body.CheckedAt.Offset);
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
    }
}

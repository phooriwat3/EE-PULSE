using EePulse.Contracts.Agents;
using System.Security.Cryptography;
using System.Text.Json;

namespace EePulse.Api.Agents;

public sealed class AgentRequestSecurityMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v1/agents") && !path.StartsWithSegments("/api/v1/agent-enrollment-tokens") && !path.StartsWithSegments("/api/v1/agent-groups"))
        { await next(context); return; }
        if (!environment.IsDevelopment() && !context.Request.IsHttps)
        { await WriteProblem(context, 403, AgentProblemCodes.AgentAuthenticationRequired, "HTTPS is required for Agent operations."); return; }
        if (IsLimitedBody(context.Request.Method, path))
        {
            const long maximum = 32 * 1024;
            if (context.Request.ContentLength > maximum) { await WriteProblem(context, 413, AgentProblemCodes.RequestInvalid, "Request body exceeds 32 KiB."); return; }
            var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximum;
            context.Request.EnableBuffering((int)maximum, maximum);
            var buffer = new byte[8192]; long total = 0;
            try
            {
                while (true) { var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted); if (read == 0) break; total += read; if (total > maximum) { context.Request.Body.Position = 0; await WriteProblem(context, 413, AgentProblemCodes.RequestInvalid, "Request body exceeds 32 KiB."); return; } }
                context.Request.Body.Position = 0;
                try
                {
                    using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    context.Request.Body.Position = 0;
                    if (HasNonZuluTimestamp(json.RootElement, "sentAt") || HasNonZuluTimestamp(json.RootElement, "appliedAt"))
                    { await WriteProblem(context, 400, AgentProblemCodes.TimestampNotUtc, "Timestamps must use RFC 3339 UTC Z form."); return; }
                    if (json.RootElement.ValueKind == JsonValueKind.Object &&
                        (json.RootElement.TryGetProperty("command", out _) || json.RootElement.TryGetProperty("url", out _)))
                    { await WriteProblem(context, 400, AgentProblemCodes.RequestInvalid, "Request contains an unsupported member."); return; }
                }
                catch (JsonException)
                {
                    context.Request.Body.Position = 0;
                    await WriteProblem(context, 400, AgentProblemCodes.RequestInvalid, "Request body is malformed.");
                    return;
                }
            }
            catch (Exception exception) when (exception is BadHttpRequestException or IOException) { await WriteProblem(context, 413, AgentProblemCodes.RequestInvalid, "Request body exceeds 32 KiB."); return; }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        try { await next(context); }
        catch (Exception exception) when ((exception is BadHttpRequestException or JsonException) && !context.Response.HasStarted)
        { await WriteProblem(context, 400, AgentProblemCodes.RequestInvalid, "Request body is malformed or contains unsupported members."); }
    }
    private static bool IsLimitedBody(string method, PathString path) => method == HttpMethods.Post &&
        (path == "/api/v1/agents/enroll" || path.Value?.EndsWith("/heartbeat", StringComparison.Ordinal) == true || path.Value?.EndsWith("/configuration/acknowledgements", StringComparison.Ordinal) == true);
    private static async Task WriteProblem(HttpContext context, int status, string code, string detail)
    { context.Response.StatusCode = status; context.Response.ContentType = "application/problem+json"; await context.Response.WriteAsync(JsonSerializer.Serialize(new { type = $"https://ee-pulse.invalid/problems/{code}", title = code, status, detail, instance = context.Request.Path.Value, code, retryable = false, correlationId = context.TraceIdentifier })); }
    private static bool HasNonZuluTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.String) return true;
        var timestamp = value.GetString();
        return timestamp is null || !timestamp.EndsWith('Z') ||
            !DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ||
            parsed.Offset != TimeSpan.Zero;
    }
}

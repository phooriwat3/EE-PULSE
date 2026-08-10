using Serilog.Context;

namespace EePulse.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaximumLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetOrCreateCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        var candidate = context.Request.Headers[HeaderName].FirstOrDefault()?.Trim();

        if (!string.IsNullOrWhiteSpace(candidate) &&
            candidate.Length <= MaximumLength &&
            candidate.All(character => !char.IsControl(character)))
        {
            return candidate;
        }

        return Guid.NewGuid().ToString("N");
    }
}

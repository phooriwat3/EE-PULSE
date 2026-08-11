using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using EePulse.Application.Time;
using EePulse.Contracts.Agents;

namespace EePulse.Api.Agents;

public sealed class AgentRateLimiter(IUtcClock clock)
{
    private readonly ConcurrentDictionary<string,Queue<DateTimeOffset>> _events=new(StringComparer.Ordinal);
    public bool Allow(string key,int limit,TimeSpan window,out int retryAfter)
    {
        var queue=_events.GetOrAdd(key,_=>new Queue<DateTimeOffset>());lock(queue){var now=clock.UtcNow;while(queue.TryPeek(out var old)&&now-old>=window)queue.Dequeue();if(queue.Count>=limit){retryAfter=Math.Max(1,(int)Math.Ceiling((queue.Peek()+window-now).TotalSeconds));return false;}queue.Enqueue(now);retryAfter=0;return true;}
    }
}

public sealed record AgentRateLimitDefaults(int EnrollmentSourcePerMinute=5,int EnrollmentTokenPerHour=20,int AgentMutationsPerMinute=60);

public sealed class AgentRateLimitMiddleware(RequestDelegate next,AgentRateLimiter limiter,AgentRateLimitDefaults limits)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path=context.Request.Path.Value??string.Empty;
        if(context.Request.Method==HttpMethods.Post&&path=="/api/v1/agents/enroll")
        {
            var source=context.Connection.RemoteIpAddress?.ToString()??"unknown";
            if(!limiter.Allow($"enroll-source:{source}",limits.EnrollmentSourcePerMinute,TimeSpan.FromMinutes(1),out var retry)){await Reject(context,retry);return;}
            try{using var json=await JsonDocument.ParseAsync(context.Request.Body,cancellationToken:context.RequestAborted);context.Request.Body.Position=0;if(json.RootElement.TryGetProperty("enrollmentToken",out var token)&&AgentSecret.TryParseAndDigest("EE-Pulse-Agent-Enrollment-v1",token.GetString()??string.Empty,out var id,out var digest)){CryptographicOperations.ZeroMemory(digest);if(!limiter.Allow($"enroll-token:{id:N}",limits.EnrollmentTokenPerHour,TimeSpan.FromHours(1),out retry)){await Reject(context,retry);return;}}}catch(JsonException){context.Request.Body.Position=0;}
        }
        else if(context.Request.Method==HttpMethods.Post&&(path.EndsWith("/heartbeat",StringComparison.Ordinal)||path.EndsWith("/configuration/acknowledgements",StringComparison.Ordinal)||path.EndsWith("/credentials/rotate",StringComparison.Ordinal)))
        {
            var id=context.User.FindFirst("agent_id")?.Value;if(id is not null&&!limiter.Allow($"agent-mutation:{id}",limits.AgentMutationsPerMinute,TimeSpan.FromMinutes(1),out var retry)){await Reject(context,retry);return;}
        }
        await next(context);
    }
    private static async Task Reject(HttpContext context,int retry)
    {context.Response.StatusCode=429;context.Response.ContentType="application/problem+json";context.Response.Headers.RetryAfter=retry.ToString(System.Globalization.CultureInfo.InvariantCulture);await context.Response.WriteAsync(JsonSerializer.Serialize(new{type=$"https://ee-pulse.invalid/problems/{AgentProblemCodes.RateLimitExceeded}",title=AgentProblemCodes.RateLimitExceeded,status=429,detail="Request rate limit exceeded.",instance=context.Request.Path.Value,code=AgentProblemCodes.RateLimitExceeded,retryable=true,correlationId=context.TraceIdentifier,retryAfterSeconds=retry}));}
}

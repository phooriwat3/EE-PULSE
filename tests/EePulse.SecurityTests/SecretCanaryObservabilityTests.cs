using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EePulse.SecurityTests;

public sealed class SecretCanaryObservabilityTests
{
    [Fact]
    public async Task RejectedAgentSecretsAreAbsentFromLogsAndTelemetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var bodyCanary = $"enrollment-canary-{Guid.NewGuid():N}";
        var credentialCanary = $"credential-canary-{Guid.NewGuid():N}";
        using var telemetry = new ActivityCapture();
        using var logs = new LogCaptureProvider();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseEnvironment("Development"));
        factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        using var client = factory.CreateClient();

        using (var enrollment = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/enroll")
        {
            Content = new StringContent(
                $"{{\"schemaVersion\":1,\"command\":\"{bodyCanary}\"}}",
                Encoding.UTF8,
                "application/json"),
        })
        {
            var response = await client.SendAsync(enrollment, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(bodyCanary, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        using (var heartbeat = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/agents/{Guid.NewGuid()}/heartbeat")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", credentialCanary) },
        })
        {
            var response = await client.SendAsync(heartbeat, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain(credentialCanary, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        var capturedLogs = logs.JoinedEntries;
        var capturedTelemetry = telemetry.JoinedEntries;
        Assert.DoesNotContain(bodyCanary, capturedLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialCanary, capturedLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(bodyCanary, capturedTelemetry, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialCanary, capturedTelemetry, StringComparison.Ordinal);
    }

    private sealed class LogCaptureProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<string> entries = new();
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        public string JoinedEntries => string.Join('\n', entries);

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, entries, () => scopes);

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        private sealed class CaptureLogger(
            string category,
            ConcurrentQueue<string> entries,
            Func<IExternalScopeProvider> getScopes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => getScopes().Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var entry = new StringBuilder()
                    .Append(category).Append('|')
                    .Append(logLevel).Append('|')
                    .Append(eventId).Append('|')
                    .Append(formatter(state, exception)).Append('|')
                    .Append(state).Append('|')
                    .Append(exception);
                getScopes().ForEachScope((scope, builder) => builder.Append('|').Append(scope), entry);
                entries.Enqueue(entry.ToString());
            }
        }
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly ConcurrentQueue<string> entries = new();
        private readonly ActivityListener listener;

        public ActivityCapture()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => entries.Enqueue(Serialize(activity)),
            };
            ActivitySource.AddActivityListener(listener);
        }

        public string JoinedEntries => string.Join('\n', entries);

        public void Dispose() => listener.Dispose();

        private static string Serialize(Activity activity)
        {
            var entry = new StringBuilder()
                .Append(activity.Source.Name).Append('|')
                .Append(activity.OperationName).Append('|')
                .Append(activity.DisplayName).Append('|')
                .Append(activity.TraceStateString);
            foreach (var tag in activity.TagObjects)
            {
                entry.Append('|').Append(tag.Key).Append('=').Append(tag.Value);
            }

            foreach (var baggage in activity.Baggage)
            {
                entry.Append('|').Append(baggage.Key).Append('=').Append(baggage.Value);
            }

            foreach (var activityEvent in activity.Events)
            {
                entry.Append('|').Append(activityEvent.Name);
                foreach (var tag in activityEvent.Tags)
                {
                    entry.Append('|').Append(tag.Key).Append('=').Append(tag.Value);
                }
            }

            return entry.ToString();
        }
    }
}

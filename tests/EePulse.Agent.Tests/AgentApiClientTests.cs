using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Transport;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Tests;

public sealed class AgentApiClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AgentJsonContract.AddConverters(options);
        return options;
    }

    [Fact]
    public async Task EnrollmentUsesAnonymousBootstrapAndPersistsProtectedStateSeam()
    {
        var token = ConfigurationApplicationTests.RuntimeSecret();
        var credential = ConfigurationApplicationTests.RuntimeSecret();
        var agentId = Guid.NewGuid();
        var store = new MemoryIdentityStore();
        var handler = new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            var enrollment = request.Content!.ReadFromJsonAsync<AgentEnrollmentRequest>(JsonOptions).GetAwaiter().GetResult();
            Assert.Equal(token, enrollment!.EnrollmentToken);
            return JsonResponse(new AgentEnrollmentResponse(
                1,
                agentId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                credential,
                DateTimeOffset.UtcNow.AddDays(90),
                DateTimeOffset.UtcNow.AddDays(75),
                DateTimeOffset.UtcNow,
                20,
                60,
                1,
                $"/api/v1/agents/{agentId:D}/configuration"));
        });
        var client = Client(handler, store);

        var identity = await client.EnrollAsync(
            token,
            Guid.NewGuid(),
            "agent-test",
            "1.0.0",
            ["192.0.2.0/24"],
            TestContext.Current.CancellationToken);

        Assert.Equal(credential, identity.ActiveCredential.Secret);
        Assert.Same(identity, store.Value);
    }

    [Fact]
    public async Task InvalidEnrollmentCeilingDoesNotCallServerOrPersistIdentity()
    {
        var store = new MemoryIdentityStore();
        var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP must not be called."));
        var client = Client(handler, store);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.EnrollAsync(
                ConfigurationApplicationTests.RuntimeSecret(),
                Guid.NewGuid(),
                "agent-test",
                "1.0.0",
                ["0.0.0.0/0"],
                TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.CallCount);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task MatchingStrongETagUsesConditionalGetAndRetainsConfiguration()
    {
        const string tag = "\"v1-1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"";
        var identity = ConfigurationApplicationTests.Identity();
        var handler = new StubHandler(request =>
        {
            Assert.Equal(tag, request.Headers.IfNoneMatch.Single().ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var store = new MemoryIdentityStore { Value = identity };
        var client = Client(handler, store);

        var result = await client.PullConfigurationAsync(identity, tag, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConfigurationETagMustMatchCanonicalPayload()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions);
        var canonicalText = Encoding.UTF8.GetString(canonical);
        Assert.Contains("\"generatedAt\":\"", canonicalText, StringComparison.Ordinal);
        Assert.Contains("Z\"", canonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", canonicalText, StringComparison.Ordinal);
        var tag = $"\"v1-1-{Convert.ToHexStringLower(SHA256.HashData(canonical))}\"";
        var handler = new StubHandler(_ =>
        {
            var response = JsonResponse(configuration);
            response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
            return response;
        });
        var client = Client(handler, new MemoryIdentityStore { Value = identity });

        var result = await client.PullConfigurationAsync(identity, null, TestContext.Current.CancellationToken);

        Assert.Equal(configuration.AgentId, result!.Value.Configuration.AgentId);
        Assert.Equal(configuration.ConfigurationVersion, result.Value.Configuration.ConfigurationVersion);
        Assert.Equal(configuration.AllowedNetworks, result.Value.Configuration.AllowedNetworks);
        Assert.Equal(configuration.Probes, result.Value.Configuration.Probes);
        Assert.Equal(tag, result.Value.StrongETag);
    }

    [Fact]
    public async Task PayloadDigestMismatchIsRejected()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var handler = new StubHandler(_ =>
        {
            var response = JsonResponse(configuration);
            response.Headers.ETag = EntityTagHeaderValue.Parse(
                "\"v1-1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"");
            return response;
        });
        var client = Client(handler, new MemoryIdentityStore { Value = identity });

        var exception = await Assert.ThrowsAsync<AgentApiException>(async () =>
            await client.PullConfigurationAsync(identity, null, TestContext.Current.CancellationToken));

        Assert.Equal("configuration-etag-mismatch", exception.Code);
    }

    [Fact]
    public async Task PendingCredentialPromotesOnlyAfterSuccessfulUse()
    {
        var pending = new AgentCredential(
            Guid.NewGuid(),
            ConfigurationApplicationTests.RuntimeSecret(),
            DateTimeOffset.UtcNow.AddDays(90),
            DateTimeOffset.UtcNow.AddDays(75));
        var identity = ConfigurationApplicationTests.Identity(pending);
        var store = new MemoryIdentityStore { Value = identity };
        var handler = new StubHandler(request =>
        {
            Assert.Equal(pending.Secret, request.Headers.Authorization!.Parameter);
            return JsonResponse(new AgentHeartbeatResponse(
                AgentContract.SchemaVersion,
                Guid.Parse("b8f05435-c623-4f47-82c1-0ad22d34a453"),
                identity.AgentId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                20,
                1,
                false,
                false,
                false,
                null));
        });
        var client = Client(handler, store);

        await client.SendHeartbeatAsync(identity, Heartbeat(identity), TestContext.Current.CancellationToken);

        Assert.Null(store.Value!.PendingCredential);
        Assert.Equal(pending.CredentialId, store.Value.ActiveCredential.CredentialId);
    }

    [Fact]
    public async Task LostRotationResponseRetainsOldCredentialForRecovery()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var store = new MemoryIdentityStore { Value = identity };
        var handler = new StubHandler(_ => throw new HttpRequestException("Synthetic lost rotation response."));
        var client = Client(handler, store);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.RotateCredentialAsync(identity, TestContext.Current.CancellationToken));

        Assert.Equal(identity.ActiveCredential.CredentialId, store.Value!.ActiveCredential.CredentialId);
        Assert.Null(store.Value.PendingCredential);
    }

    [Fact]
    public async Task RevocationHaltsScheduleAndDeletesCredential()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var store = new MemoryIdentityStore { Value = identity };
        var sink = new ConfigurationApplicationTests.RecordingScheduleSink();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent("{\"type\":\"about:blank\",\"status\":410,\"code\":\"agent-revoked\"}", Encoding.UTF8, "application/problem+json"),
        });
        var client = Client(handler, store, new AgentRevocationHandler(sink, store));

        var exception = await Assert.ThrowsAsync<AgentApiException>(async () =>
            await client.SendHeartbeatAsync(identity, Heartbeat(identity), TestContext.Current.CancellationToken));

        Assert.Equal(AgentProblemCodes.AgentRevoked, exception.Code);
        Assert.True(sink.Halted);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task HeartbeatRetriesSameIdAfterTransientFailure()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var heartbeat = Heartbeat(identity);
        var observedIds = new List<Guid>();
        var handler = new StubHandler(request =>
        {
            var body = request.Content!.ReadFromJsonAsync<AgentHeartbeatRequest>(JsonOptions).GetAwaiter().GetResult();
            observedIds.Add(body!.HeartbeatId);
            if (observedIds.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return JsonResponse(new AgentHeartbeatResponse(
                1,
                heartbeat.HeartbeatId,
                identity.AgentId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                20,
                1,
                false,
                false,
                false,
                null));
        });
        var delay = new RecordingDelay();
        var client = Client(handler, new MemoryIdentityStore { Value = identity }, retryDelay: delay);

        await client.SendHeartbeatAsync(identity, heartbeat, TestContext.Current.CancellationToken);

        Assert.Equal([heartbeat.HeartbeatId, heartbeat.HeartbeatId], observedIds);
        Assert.Single(delay.Delays);
    }

    [Fact]
    public async Task HeartbeatTimeoutRetriesWhenServiceIsNotStopping()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var heartbeat = Heartbeat(identity);
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new TaskCanceledException("Synthetic HTTP timeout.");
            }

            return JsonResponse(new AgentHeartbeatResponse(
                1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                20, 1, false, false, false, null));
        });
        var delay = new RecordingDelay();
        var client = Client(handler, new MemoryIdentityStore { Value = identity }, retryDelay: delay);

        var response = await client.SendHeartbeatAsync(identity, heartbeat, TestContext.Current.CancellationToken);

        Assert.Equal(heartbeat.HeartbeatId, response.HeartbeatId);
        Assert.Equal(2, calls);
        Assert.Single(delay.Delays);
    }

    [Fact]
    public async Task OutboundHeartbeatTimestampUsesZuluSuffix()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var heartbeat = Heartbeat(identity);
        var handler = new StubHandler(request =>
        {
            var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"sentAt\":\"", json, StringComparison.Ordinal);
            Assert.Contains("Z\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
            return JsonResponse(new AgentHeartbeatResponse(
                1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                20, 1, false, false, false, null));
        });
        var client = Client(handler, new MemoryIdentityStore { Value = identity });

        await client.SendHeartbeatAsync(identity, heartbeat, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InboundNonZuluUtcOffsetIsRejected()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var heartbeat = Heartbeat(identity);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {"schemaVersion":1,"heartbeatId":"{{heartbeat.HeartbeatId:D}}","agentId":"{{identity.AgentId:D}}","receivedAt":"2026-08-11T00:00:00+00:00","serverTime":"2026-08-11T00:00:00+00:00","nextHeartbeatSeconds":20,"desiredConfigurationVersion":1,"configurationChanged":false,"credentialRotationRequired":false,"clockSkewSuspected":false,"warningCode":null}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        var client = Client(handler, new MemoryIdentityStore { Value = identity });

        await Assert.ThrowsAsync<JsonException>(async () =>
            await client.SendHeartbeatAsync(identity, heartbeat, TestContext.Current.CancellationToken));
    }

    private static AgentApiClient Client(
        HttpMessageHandler handler,
        MemoryIdentityStore store,
        IAgentRevocationHandler? revocation = null,
        IAgentRetryDelay? retryDelay = null) =>
        new(
            new HttpClient(handler),
            store,
            revocation ?? new NullRevocationHandler(),
            retryDelay ?? new RecordingDelay(),
            new AgentClientOptions(new Uri("https://127.0.0.1/"), true));

    private static AgentHeartbeatRequest Heartbeat(AgentIdentity identity) =>
        new(1, Guid.NewGuid(), identity.AgentVersion, identity.MachineName, 0, 0, "Healthy", DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    internal sealed class MemoryIdentityStore : IAgentIdentityStore
    {
        public AgentIdentity? Value { get; set; }

        public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Value);

        public ValueTask SaveAsync(AgentIdentity identity, CancellationToken cancellationToken)
        {
            Value = identity;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            Value = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullRevocationHandler : IAgentRevocationHandler
    {
        public ValueTask HandleRevocationAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingDelay : IAgentRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return ValueTask.CompletedTask;
        }
    }
}

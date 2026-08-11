using System.Net;
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

public sealed class AcknowledgementDurabilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AgentJsonContract.AddConverters(options);
        return options;
    }

    [Fact]
    public async Task LostAcknowledgementResponseRetriesSameIdAcrossRestart()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var identityStore = new IdentityStore(identity);
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore();
        var pendingStore = new PendingStore();
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var acknowledgementIds = new List<Guid>();
        var firstHandler = new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = Read<AgentHeartbeatRequest>(request);
                return JsonResponse(new AgentHeartbeatResponse(
                    1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    20, 1, true, false, false, null));
            }

            if (request.Method == HttpMethod.Get)
            {
                var response = JsonResponse(configuration);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag(configuration));
                return response;
            }

            acknowledgementIds.Add(Read<AgentConfigurationAcknowledgementRequest>(request).AcknowledgementId);
            throw new HttpRequestException("Synthetic lost acknowledgement response.");
        });
        var firstRuntime = Runtime(firstHandler, identityStore, configurationStore, pendingStore);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await firstRuntime.ExecuteCycleAsync(TestContext.Current.CancellationToken));

        var pending = await pendingStore.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(3, acknowledgementIds.Count);
        Assert.All(acknowledgementIds, id => Assert.Equal(pending.AcknowledgementId, id));

        Guid? restartAcknowledgementId = null;
        var secondHandler = new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/acknowledgements", StringComparison.Ordinal))
            {
                var acknowledgement = Read<AgentConfigurationAcknowledgementRequest>(request);
                restartAcknowledgementId = acknowledgement.AcknowledgementId;
                return JsonResponse(new AgentConfigurationAcknowledgementResponse(
                    1, acknowledgement.AcknowledgementId, identity.AgentId, acknowledgement.ConfigurationVersion,
                    DateTimeOffset.UtcNow, 1, 1));
            }

            var heartbeat = Read<AgentHeartbeatRequest>(request);
            return JsonResponse(new AgentHeartbeatResponse(
                1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                20, 1, false, false, false, null));
        });
        var restartedRuntime = Runtime(secondHandler, identityStore, configurationStore, pendingStore);

        await restartedRuntime.ExecuteCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(pending.AcknowledgementId, restartAcknowledgementId);
        Assert.Null(await pendingStore.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestartRestoresValidatedScheduleBeforeUnchangedResponse()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var tag = ETag(configuration);
        var identityStore = new IdentityStore(identity);
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(configuration, null, tag),
        };
        var sink = new ConfigurationApplicationTests.RecordingScheduleSink();
        var handler = new RouteHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(tag, request.Headers.IfNoneMatch.Single().ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            Assert.NotNull(sink.Active);
            var heartbeat = Read<AgentHeartbeatRequest>(request);
            return JsonResponse(new AgentHeartbeatResponse(
                1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                20, 1, true, false, false, null));
        });
        var pendingStore = new PendingStore();
        var client = new AgentApiClient(
            new HttpClient(handler),
            identityStore,
            new AgentRevocationHandler(sink, identityStore, pendingStore),
            new NoDelay(),
            new AgentClientOptions(new Uri("https://127.0.0.1/"), true));
        using var applier = new AgentConfigurationApplier(
            new AgentConfigurationValidator(false),
            configurationStore,
            sink);
        var runtime = new AgentRuntime(
            client,
            identityStore,
            configurationStore,
            pendingStore,
            applier,
            new Status(),
            TimeProvider.System,
            false);

        await runtime.ExecuteCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sink.Active!.ConfigurationVersion);
    }

    [Fact]
    public async Task InvalidSnapshotKeepsLastKnownGoodAndEmitsSanitizedRejection()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var active = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var invalid = active with
        {
            ConfigurationVersion = 2,
            Probes = [active.Probes[0] with { FailureThreshold = 0 }],
        };
        var identityStore = new IdentityStore(identity);
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(active, null, ETag(active)),
        };
        AgentConfigurationAcknowledgementRequest? captured = null;
        var handler = new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = Read<AgentHeartbeatRequest>(request);
                return JsonResponse(new AgentHeartbeatResponse(
                    1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    20, 2, true, false, false, null));
            }

            if (request.Method == HttpMethod.Get)
            {
                var response = JsonResponse(invalid);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag(invalid));
                return response;
            }

            captured = Read<AgentConfigurationAcknowledgementRequest>(request);
            return JsonResponse(new AgentConfigurationAcknowledgementResponse(
                1, captured.AcknowledgementId, identity.AgentId, 2, DateTimeOffset.UtcNow, 1, 2));
        });
        var pendingStore = new PendingStore();
        var runtime = Runtime(handler, identityStore, configurationStore, pendingStore);

        await runtime.ExecuteCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, configurationStore.Value!.Active.ConfigurationVersion);
        Assert.Equal("Rejected", captured!.Status);
        Assert.Equal(AgentConfigurationRejectionCodes.ConfigurationInvalid, captured.ErrorCode);
        Assert.Null(captured.AppliedAt);
    }

    [Fact]
    public async Task RestartPendingAcknowledgementFallsBackOnceToActiveCredential()
    {
        var pendingCredential = new AgentCredential(
            Guid.NewGuid(),
            ConfigurationApplicationTests.RuntimeSecret(),
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var identity = ConfigurationApplicationTests.Identity(pendingCredential);
        var identityStore = new IdentityStore(identity);
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(configuration, null, ETag(configuration)),
        };
        var acknowledgement = new AgentConfigurationAcknowledgementRequest(
            1, Guid.NewGuid(), 1, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);
        var pendingStore = new PendingStore();
        await pendingStore.SaveAsync(acknowledgement, TestContext.Current.CancellationToken);
        var credentials = new List<string?>();
        var handler = new RouteHandler(request =>
        {
            credentials.Add(request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith("/acknowledgements", StringComparison.Ordinal) &&
                string.Equals(request.Headers.Authorization?.Parameter, pendingCredential.Secret, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"code\":\"agent-authentication-required\"}", Encoding.UTF8, "application/problem+json"),
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/acknowledgements", StringComparison.Ordinal))
            {
                return JsonResponse(new AgentConfigurationAcknowledgementResponse(
                    1, acknowledgement.AcknowledgementId, identity.AgentId, 1, DateTimeOffset.UtcNow, 1, 1));
            }

            var heartbeat = Read<AgentHeartbeatRequest>(request);
            return JsonResponse(new AgentHeartbeatResponse(
                1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                20, 1, false, false, false, null));
        });
        var runtime = Runtime(handler, identityStore, configurationStore, pendingStore);

        await runtime.ExecuteCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [pendingCredential.Secret, identity.ActiveCredential.Secret, identity.ActiveCredential.Secret],
            credentials);
        Assert.Null((await identityStore.LoadAsync(TestContext.Current.CancellationToken))!.PendingCredential);
        Assert.Null(await pendingStore.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RuntimeRecoversAfterTransientTransportOutage()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var identityStore = new IdentityStore(identity);
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(configuration, null, ETag(configuration)),
        };
        var calls = 0;
        var handler = new RouteHandler(request =>
        {
            calls++;
            if (calls <= 3)
            {
                throw new HttpRequestException("Synthetic temporary outage.");
            }

            var heartbeat = Read<AgentHeartbeatRequest>(request);
            return JsonResponse(new AgentHeartbeatResponse(
                1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                20, 1, false, false, false, null));
        });
        var pendingStore = new PendingStore();
        using var cancellation = new CancellationTokenSource();
        var delay = new CancelAfterRecoveryDelay(cancellation);
        var runtime = Runtime(handler, identityStore, configurationStore, pendingStore, delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync(cancellation.Token));

        Assert.Equal(4, calls);
        Assert.Equal(2, delay.Delays.Count);
        Assert.InRange(delay.Delays[0], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1_250));
        Assert.Equal(TimeSpan.FromSeconds(20), delay.Delays[1]);
    }

    [Fact]
    public async Task RuntimePreservesServiceShutdownCancellation()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var identityStore = new IdentityStore(identity);
        var configuration = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(configuration, null, ETag(configuration)),
        };
        using var cancellation = new CancellationTokenSource();
        var delay = new CancelAfterRecoveryDelay(cancellation);
        var runtime = Runtime(
            new ShutdownCancellationHandler(cancellation),
            identityStore,
            configurationStore,
            new PendingStore(),
            delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync(cancellation.Token));

        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task UnknownRemoteCommandIsRejectedBeforeLastKnownGoodMutation()
    {
        var identity = ConfigurationApplicationTests.Identity();
        var identityStore = new IdentityStore(identity);
        var active = AllowedNetworkPolicyTests.Configuration(
            agentId: identity.AgentId,
            groupId: identity.AgentGroupId);
        var candidate = active with { ConfigurationVersion = 2 };
        var configurationStore = new ConfigurationApplicationTests.MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(active, null, ETag(active)),
        };
        var candidateJson = JsonSerializer.Serialize(candidate, JsonOptions);
        var closedSchemaViolation = candidateJson[..^1] + ",\"command\":\"synthetic-disallowed\"}";
        AgentConfigurationAcknowledgementRequest? captured = null;
        var handler = new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = Read<AgentHeartbeatRequest>(request);
                return JsonResponse(new AgentHeartbeatResponse(
                    1, heartbeat.HeartbeatId, identity.AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    20, 2, true, false, false, null));
            }

            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(closedSchemaViolation, Encoding.UTF8, "application/json"),
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag(candidate));
                return response;
            }

            captured = Read<AgentConfigurationAcknowledgementRequest>(request);
            return JsonResponse(new AgentConfigurationAcknowledgementResponse(
                1, captured.AcknowledgementId, identity.AgentId, 2, DateTimeOffset.UtcNow, 1, 2));
        });
        var sink = new ConfigurationApplicationTests.RecordingScheduleSink();
        var runtime = Runtime(handler, identityStore, configurationStore, new PendingStore(), scheduleSink: sink);

        await runtime.ExecuteCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, configurationStore.Value!.Active.ConfigurationVersion);
        Assert.Null(configurationStore.Value.LastKnownGood);
        Assert.Equal(1, sink.Active!.ConfigurationVersion);
        Assert.Equal("Rejected", captured!.Status);
        Assert.Equal(AgentConfigurationRejectionCodes.ConfigurationInvalid, captured.ErrorCode);
        Assert.DoesNotContain("synthetic-disallowed", JsonSerializer.Serialize(captured, JsonOptions), StringComparison.Ordinal);
    }

    private static AgentRuntime Runtime(
        HttpMessageHandler handler,
        IAgentIdentityStore identityStore,
        IAgentConfigurationStore configurationStore,
        IPendingAcknowledgementStore pendingStore,
        IAgentRuntimeDelay? runtimeDelay = null,
        ConfigurationApplicationTests.RecordingScheduleSink? scheduleSink = null)
    {
        var sink = scheduleSink ?? new ConfigurationApplicationTests.RecordingScheduleSink();
        var client = new AgentApiClient(
            new HttpClient(handler),
            identityStore,
            new AgentRevocationHandler(sink, identityStore, pendingStore),
            new NoDelay(),
            new AgentClientOptions(new Uri("https://127.0.0.1/"), true));
        return new AgentRuntime(
            client,
            identityStore,
            configurationStore,
            pendingStore,
            new AgentConfigurationApplier(new AgentConfigurationValidator(false), configurationStore, sink),
            new Status(),
            TimeProvider.System,
            false,
            runtimeDelay);
    }

    private static T Read<T>(HttpRequestMessage request) =>
        request.Content!.ReadFromJsonAsync<T>(JsonOptions).GetAwaiter().GetResult()!;

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json"),
    };

    private static string ETag(AgentConfigurationResponse configuration)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions);
        return $"\"v1-{configuration.ConfigurationVersion}-{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"";
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class ShutdownCancellationHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    private sealed class IdentityStore(AgentIdentity identity) : IAgentIdentityStore
    {
        private AgentIdentity? value = identity;

        public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(value);
        public ValueTask SaveAsync(AgentIdentity next, CancellationToken cancellationToken)
        {
            value = next;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            value = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PendingStore : IPendingAcknowledgementStore
    {
        private AgentConfigurationAcknowledgementRequest? value;

        public ValueTask<AgentConfigurationAcknowledgementRequest?> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(value);

        public ValueTask SaveAsync(
            AgentConfigurationAcknowledgementRequest acknowledgement,
            CancellationToken cancellationToken)
        {
            value = acknowledgement;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            value = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Status : IAgentSelfStatus
    {
        public long QueueDepth => 0;
        public string HealthState => "Healthy";
    }

    private sealed class NoDelay : IAgentRetryDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CancelAfterRecoveryDelay(CancellationTokenSource cancellation) : IAgentRuntimeDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            if (Delays.Count == 2)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled(cancellationToken);
            }

            return ValueTask.CompletedTask;
        }
    }
}

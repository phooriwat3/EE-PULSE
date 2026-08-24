using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Transport;
using EePulse.Agent.Infrastructure.Storage;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.IntegrationTests;

public sealed class Wp05DeliveryEndToEndTests
{
    private static readonly Guid ActorId = Guid.Parse("be2e5861-20f7-488e-b7e9-b7b0d4cdfecb");
    private static readonly JsonSerializerOptions AgentJson = CreateAgentJson();

    [Fact]
    public async Task DurableLocalResultDeliversToPostgresAndAcknowledgedRowIsCleanupEligibleAfterReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"ee-pulse-wp05-e2e-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "probe-results.db");
        Directory.CreateDirectory(directory);

        try
        {
            await using var postgres = await PostgresTestDatabase.StartAsync(ct);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
            using var backendClient = factory.CreateClient();
            var enrolled = await EnrollConfiguredAgentAsync(backendClient, ct);
            var identity = new AgentIdentity(
                enrolled.AgentId, enrolled.AgentGroupId, Guid.NewGuid(), "wp05-e2e-agent", "1.2.3", ["192.0.2.0/24"],
                new(enrolled.CredentialId, enrolled.Credential, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue), null, 20, 60, enrolled.ConfigurationVersion);
            var identities = new FixedIdentityStore(identity);
            Guid resultId;

            await using (var outbox = new SqliteProbeResultOutbox(databasePath))
            {
                var sink = new DurableLocalProbeResultSink(outbox, identities);
                sink.Publish(new LocalProbeResult(enrolled.ConfigurationVersion, enrolled.ProbeId,
                    new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 24, 9, 0, 1, TimeSpan.Zero), 1, 1, 0m, 1m, 1m, 1m, null));
                resultId = Assert.Single(await outbox.ReadPendingAsync(new(10, 1_000_000), ct)).Envelope.ResultId;
            }

            await using (var outbox = new SqliteProbeResultOutbox(databasePath))
            {
                using var deliveryClient = factory.CreateClient();
                await using var apiClient = new AgentApiClient(deliveryClient, identities, new NullRevocationHandler(), new NoDelay(),
                    new AgentClientOptions(deliveryClient.BaseAddress!, IsProduction: false));
                var delivery = new ProbeResultDeliveryCoordinator(outbox, apiClient, TimeProvider.System, new FixedRandom());
                var cycle = await delivery.DeliverOnceAsync(identity, ct);
                Assert.True(cycle.Delivered);
                Assert.Empty(await outbox.ReadPendingAsync(new(10, 1_000_000), ct));

                await using var scope = factory.Services.CreateAsyncScope();
                var ledger = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries
                    .SingleAsync(entry => entry.AgentId == enrolled.AgentId && entry.ResultId == resultId, ct);
                Assert.Equal(enrolled.ProbeId, ledger.ProbeId);
                Assert.Equal(enrolled.ConfigurationVersion, ledger.ConfigurationVersion);
            }

            await using (var reopened = new SqliteProbeResultOutbox(databasePath))
            {
                Assert.Empty(await reopened.ReadPendingAsync(new(10, 1_000_000), ct));
                Assert.Equal(1, await reopened.CleanupAcknowledgedAsync(DateTimeOffset.MaxValue, 10, ct));
            }

            await using var afterCleanup = new SqliteProbeResultOutbox(databasePath);
            Assert.Empty(await afterCleanup.ReadPendingAsync(new(10, 1_000_000), ct));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LostResponseThenAgentRestartReplaysImmutableResultWithoutDuplicatingLedgerEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"ee-pulse-wp05-lost-response-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "probe-results.db");
        Directory.CreateDirectory(directory);

        try
        {
            await using var postgres = await PostgresTestDatabase.StartAsync(ct);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
            using var backendClient = factory.CreateClient();
            var enrolled = await EnrollConfiguredAgentAsync(backendClient, ct);
            var identity = new AgentIdentity(
                enrolled.AgentId, enrolled.AgentGroupId, Guid.NewGuid(), "wp05-lost-response-agent", "1.2.3", ["192.0.2.0/24"],
                new(enrolled.CredentialId, enrolled.Credential, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue), null, 20, 60, enrolled.ConfigurationVersion);
            var identities = new FixedIdentityStore(identity);
            Guid resultId;

            await using (var outbox = new SqliteProbeResultOutbox(databasePath))
            {
                var sink = new DurableLocalProbeResultSink(outbox, identities);
                sink.Publish(new LocalProbeResult(enrolled.ConfigurationVersion, enrolled.ProbeId,
                    new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 24, 9, 0, 1, TimeSpan.Zero), 1, 1, 0m, 1m, 1m, 1m, null));
                resultId = Assert.Single(await outbox.ReadPendingAsync(new(10, 1_000_000), ct)).Envelope.ResultId;
            }

            using var forwardingClient = factory.CreateClient();
            using var responseLossHandler = new LoseFirstSuccessfulResultBatchResponseHandler(forwardingClient);
            using (var faultedDeliveryClient = new HttpClient(responseLossHandler, disposeHandler: false)
                   {
                       BaseAddress = forwardingClient.BaseAddress,
                   })
            await using (var outbox = new SqliteProbeResultOutbox(databasePath))
            await using (var apiClient = new AgentApiClient(faultedDeliveryClient, identities, new NullRevocationHandler(), new NoDelay(),
                             new AgentClientOptions(forwardingClient.BaseAddress!, IsProduction: false)))
            {
                var delivery = new ProbeResultDeliveryCoordinator(outbox, apiClient, TimeProvider.System, new FixedRandom());

                var cycle = await delivery.DeliverOnceAsync(identity, ct);

                Assert.False(cycle.Delivered);
                Assert.True(cycle.HasPendingResults);
                Assert.Equal(TimeSpan.FromMilliseconds(500), cycle.NextDelay);
                Assert.Equal(1, responseLossHandler.ForwardedResultBatchCount);
                Assert.True(responseLossHandler.SuccessfulResponseLost);
                var pending = Assert.Single(await outbox.ReadPendingAsync(new(10, 1_000_000), ct));
                Assert.Equal(resultId, pending.Envelope.ResultId);
                Assert.Equal(ProbeResultOutboxState.Pending, pending.State);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var ledger = scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries;
                Assert.Equal(1, await ledger.CountAsync(entry => entry.AgentId == enrolled.AgentId && entry.ResultId == resultId, ct));
            }

            await using (var reopenedOutbox = new SqliteProbeResultOutbox(databasePath))
            {
                using var replayDeliveryClient = factory.CreateClient();
                await using var replayApiClient = new AgentApiClient(replayDeliveryClient, identities, new NullRevocationHandler(), new NoDelay(),
                    new AgentClientOptions(replayDeliveryClient.BaseAddress!, IsProduction: false));
                var replayDelivery = new ProbeResultDeliveryCoordinator(reopenedOutbox, replayApiClient, TimeProvider.System, new FixedRandom());

                var replayCycle = await replayDelivery.DeliverOnceAsync(identity, ct);

                Assert.True(replayCycle.Delivered);
                Assert.True(replayCycle.HasPendingResults);
                Assert.Equal(TimeSpan.Zero, replayCycle.NextDelay);
                Assert.Empty(await reopenedOutbox.ReadPendingAsync(new(10, 1_000_000), ct));
                Assert.Equal(1, await reopenedOutbox.CleanupAcknowledgedAsync(DateTimeOffset.MaxValue, 10, ct));
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var ledger = scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries;
                Assert.Equal(1, await ledger.CountAsync(entry => entry.AgentId == enrolled.AgentId && entry.ResultId == resultId, ct));
            }

            await using var afterCleanup = new SqliteProbeResultOutbox(databasePath);
            Assert.Empty(await afterCleanup.ReadPendingAsync(new(10, 1_000_000), ct));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialAcknowledgementDurablyQuarantinesRejectedResultAndContinuesFifoDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"ee-pulse-wp05-partial-ack-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "probe-results.db");
        Directory.CreateDirectory(directory);

        try
        {
            await using var postgres = await PostgresTestDatabase.StartAsync(ct);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
            using var backendClient = factory.CreateClient();
            var enrolled = await EnrollConfiguredAgentAsync(backendClient, ct);
            var identity = new AgentIdentity(
                enrolled.AgentId, enrolled.AgentGroupId, Guid.NewGuid(), "wp05-partial-ack-agent", "1.2.3", ["192.0.2.0/24"],
                new(enrolled.CredentialId, enrolled.Credential, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue), null, 20, 60, enrolled.ConfigurationVersion);
            var identities = new FixedIdentityStore(identity);
            Guid[] resultIds;

            await using (var outbox = new SqliteProbeResultOutbox(databasePath))
            {
                var sink = new DurableLocalProbeResultSink(outbox, identities);
                sink.Publish(new LocalProbeResult(enrolled.ConfigurationVersion, enrolled.ProbeId,
                    new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 24, 10, 0, 1, TimeSpan.Zero), 1, 1, 0m, 1m, 1m, 1m, null));
                // The real Backend rejects sub-microsecond RTT precision as a per-result "result-invalid" outcome.
                sink.Publish(new LocalProbeResult(enrolled.ConfigurationVersion, enrolled.ProbeId,
                    new DateTimeOffset(2026, 8, 24, 10, 1, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 24, 10, 1, 1, TimeSpan.Zero), 1, 1, 0m, 0.0000001m, 0.0000001m, 0.0000001m, null));
                sink.Publish(new LocalProbeResult(enrolled.ConfigurationVersion, enrolled.ProbeId,
                    new DateTimeOffset(2026, 8, 24, 10, 2, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 24, 10, 2, 1, TimeSpan.Zero), 1, 1, 0m, 2m, 2m, 2m, null));

                var initial = await outbox.ReadPendingAsync(new(10, 1_000_000), ct);
                Assert.Equal([1L, 2L, 3L], initial.Select(record => record.Sequence));
                resultIds = initial.Select(record => record.Envelope.ResultId).ToArray();
            }

            using var forwardingClient = factory.CreateClient();
            using var observer = new ObservingResultBatchHandler(forwardingClient);
            using var deliveryClient = new HttpClient(observer, disposeHandler: false) { BaseAddress = forwardingClient.BaseAddress };
            await using var apiClient = new AgentApiClient(deliveryClient, identities, new NullRevocationHandler(), new NoDelay(),
                new AgentClientOptions(forwardingClient.BaseAddress!, IsProduction: false));

            await using (var outbox = new SqliteProbeResultOutbox(databasePath))
            {
                var delivery = new ProbeResultDeliveryCoordinator(outbox, apiClient, TimeProvider.System, new FixedRandom(),
                    new ProbeResultDeliveryOptions(MaximumBatchCount: 2));
                var firstCycle = await delivery.DeliverOnceAsync(identity, ct);

                Assert.True(firstCycle.Delivered);
                Assert.Equal(TimeSpan.Zero, firstCycle.NextDelay);
                Assert.Equal([resultIds[0], resultIds[1]], Assert.Single(observer.ResultBatchResultIds));
            }

            var afterFirstDelivery = await ReadDurableOutboxRowsAsync(databasePath, ct);
            Assert.Equal([(resultIds[0], 1), (resultIds[2], 0)], afterFirstDelivery.Select(row => (row.ResultId, row.State)));
            Assert.NotNull(Assert.Single(afterFirstDelivery, row => row.ResultId == resultIds[0]).AcknowledgedAtTicks);
            var quarantinedAfterFirstDelivery = Assert.Single(await ReadQuarantinedOutboxRowsAsync(databasePath, ct));
            Assert.Equal(resultIds[1], quarantinedAfterFirstDelivery.ResultId);
            Assert.Equal("result-invalid", quarantinedAfterFirstDelivery.ReasonCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var ledgerResultIds = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries
                    .Where(entry => entry.AgentId == enrolled.AgentId)
                    .Select(entry => entry.ResultId)
                    .ToArrayAsync(ct);
                Assert.Equal([resultIds[0]], ledgerResultIds);
            }

            await using (var reopenedOutbox = new SqliteProbeResultOutbox(databasePath))
            {
                var pending = Assert.Single(await reopenedOutbox.ReadPendingAsync(new(10, 1_000_000), ct));
                Assert.Equal(resultIds[2], pending.Envelope.ResultId);

                var delivery = new ProbeResultDeliveryCoordinator(reopenedOutbox, apiClient, TimeProvider.System, new FixedRandom(),
                    new ProbeResultDeliveryOptions(MaximumBatchCount: 2));
                var secondCycle = await delivery.DeliverOnceAsync(identity, ct);

                Assert.True(secondCycle.Delivered);
                Assert.Equal(TimeSpan.Zero, secondCycle.NextDelay);
                Assert.Empty(await reopenedOutbox.ReadPendingAsync(new(10, 1_000_000), ct));
            }

            Assert.Collection(observer.ResultBatchResultIds,
                batch => Assert.Equal([resultIds[0], resultIds[1]], batch),
                batch => Assert.Equal([resultIds[2]], batch));
            var afterSecondDelivery = await ReadDurableOutboxRowsAsync(databasePath, ct);
            Assert.Equal([(resultIds[0], 1), (resultIds[2], 1)], afterSecondDelivery.Select(row => (row.ResultId, row.State)));
            Assert.All(afterSecondDelivery, row => Assert.NotNull(row.AcknowledgedAtTicks));
            var quarantinedAfterSecondDelivery = Assert.Single(await ReadQuarantinedOutboxRowsAsync(databasePath, ct));
            Assert.Equal((resultIds[1], "result-invalid"), (quarantinedAfterSecondDelivery.ResultId, quarantinedAfterSecondDelivery.ReasonCode));
            Assert.Equal(resultIds.OrderBy(id => id), afterSecondDelivery.Select(row => row.ResultId)
                .Append(quarantinedAfterSecondDelivery.ResultId).OrderBy(id => id));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var ledgerResultIds = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries
                    .Where(entry => entry.AgentId == enrolled.AgentId)
                    .OrderBy(entry => entry.StartedAt)
                    .Select(entry => entry.ResultId)
                    .ToArrayAsync(ct);
                Assert.Equal([resultIds[0], resultIds[2]], ledgerResultIds);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(Guid AgentId, Guid AgentGroupId, Guid CredentialId, string Credential, Guid ProbeId, long ConfigurationVersion)> EnrollConfiguredAgentAsync(HttpClient client, CancellationToken ct)
    {
        var group = await AdminAsync<AgentGroupResponse>(client, HttpMethod.Post, "/api/v1/agent-groups", new CreateAgentGroupRequest($"wp05-{Guid.NewGuid():N}", null), ct);
        _ = await AdminAsync<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct);
        var site = await AdminAsync<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", new CreateSiteRequest("E2E" + Guid.NewGuid().ToString("N")[..6], "WP-05 E2E", "UTC"), ct);
        var device = await AdminAsync<DeviceResponse>(client, HttpMethod.Post, "/api/v1/devices", new CreateDeviceRequest(site.Id, "target", "192.0.2.10", null, "server", null, null, "Normal", []), ct);
        var probe = await AdminAsync<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", new CreateProbeRequest(device.Id, group.Id, 20, 1000, 1, null, null, 1, 1), ct);
        var token = await AdminAsync<CreateAgentEnrollmentTokenResponse>(client, HttpMethod.Post, "/api/v1/agent-enrollment-tokens", new CreateAgentEnrollmentTokenRequest(1, Guid.Parse(group.Id), "wp05-e2e", null, ["192.0.2.0/24"]), ct);
        var enrollment = await PostAsync<AgentEnrollmentResponse>(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, token.EnrollmentToken, Guid.NewGuid(), "wp05-e2e-agent", "1.2.3", token.AllowedNetworks, DateTimeOffset.UtcNow), ct);
        var configuration = await GetAsync<AgentConfigurationResponse>(client, $"/api/v1/agents/{enrollment.AgentId}/configuration", enrollment.AgentCredential, ct);
        using var acknowledgement = AgentRequest(HttpMethod.Post, $"/api/v1/agents/{enrollment.AgentId}/configuration/acknowledgements", enrollment.AgentCredential,
            new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), configuration.ConfigurationVersion, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow));
        Assert.True((await client.SendAsync(acknowledgement, ct)).IsSuccessStatusCode);
        return (enrollment.AgentId, Guid.Parse(group.Id), enrollment.CredentialId, enrollment.AgentCredential, Guid.Parse(probe.Id), configuration.ConfigurationVersion);
    }

    private static async Task<T> AdminAsync<T>(HttpClient client, HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: AgentJson) };
        request.Headers.Add("X-EE-Pulse-Role", "Administrator");
        request.Headers.Add("X-EE-Pulse-Actor", ActorId.ToString());
        var response = await client.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body, CancellationToken ct)
    {
        var response = await client.PostAsync(path, JsonContent.Create(body, options: AgentJson), ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(AgentJson, ct))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, string credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var response = await client.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(AgentJson, ct))!;
    }

    private static HttpRequestMessage AgentRequest(HttpMethod method, string path, string credential, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: AgentJson) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private sealed class FixedIdentityStore(AgentIdentity identity) : IAgentIdentityStore
    {
        public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) => new((AgentIdentity?)identity);
        public ValueTask SaveAsync(AgentIdentity value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NullRevocationHandler : IAgentRevocationHandler
    {
        public ValueTask HandleRevocationAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoDelay : IAgentRetryDelay
    {
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FixedRandom : IProbeResultDeliveryRandom
    {
        public double NextDouble() => 0.5;
    }

    private sealed record DurableOutboxRow(Guid ResultId, int State, long? AcknowledgedAtTicks);
    private sealed record QuarantinedOutboxRow(Guid ResultId, string ReasonCode);

    private static async Task<IReadOnlyList<DurableOutboxRow>> ReadDurableOutboxRowsAsync(string databasePath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_id, state, acknowledged_at_ticks FROM probe_result_outbox ORDER BY sequence ASC;";
        var rows = new List<DurableOutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<QuarantinedOutboxRow>> ReadQuarantinedOutboxRowsAsync(string databasePath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_id, reason_code FROM probe_result_outbox_quarantine ORDER BY quarantined_at_ticks ASC;";
        var rows = new List<QuarantinedOutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }

        return rows;
    }

    private sealed class ObservingResultBatchHandler(HttpClient forwardingClient) : DelegatingHandler
    {
        public List<IReadOnlyList<Guid>> ResultBatchResultIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var contentBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/result-batches", StringComparison.Ordinal))
            {
                var batch = JsonSerializer.Deserialize<ProbeResultIngestionBatchRequest>(contentBytes!, AgentJson)
                    ?? throw new InvalidDataException("Result batch body is required.");
                ResultBatchResultIds.Add(batch.Results.Select(result => result.ResultId).ToArray());
            }

            using var forwardedRequest = CloneRequest(request, contentBytes);
            return await forwardingClient.SendAsync(forwardedRequest, cancellationToken);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? contentBytes)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri ?? throw new InvalidOperationException("Request URI is required."));
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var content = new ByteArrayContent(contentBytes!);
                foreach (var header in request.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = content;
            }

            return clone;
        }
    }

    private sealed class LoseFirstSuccessfulResultBatchResponseHandler(HttpClient forwardingClient) : DelegatingHandler
    {
        public int ForwardedResultBatchCount { get; private set; }
        public bool SuccessfulResponseLost { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var forwardedRequest = await CloneRequestAsync(request, cancellationToken);
            var response = await forwardingClient.SendAsync(forwardedRequest, cancellationToken);
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/result-batches", StringComparison.Ordinal))
            {
                return response;
            }

            ForwardedResultBatchCount++;
            if (ForwardedResultBatchCount != 1)
            {
                return response;
            }

            response.EnsureSuccessStatusCode();
            SuccessfulResponseLost = true;
            response.Dispose();
            throw new HttpRequestException("Synthetic loss after the Backend accepted the result batch response.");
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri ?? throw new InvalidOperationException("Request URI is required."));
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = content;
            }

            return clone;
        }
    }

    private static JsonSerializerOptions CreateAgentJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        AgentJsonContract.AddConverters(options);
        return options;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EePulse.Application.Time;
using EePulse.Api.Agents;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Inventory;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class ProbeResultIngestionApiTests
{
    private static readonly Guid ActorId = Guid.Parse("6a7f78d4-679d-4ed2-9aea-1c395a439d30");
    private static readonly JsonSerializerOptions AgentJson = CreateAgentJson();

    [Fact]
    public async Task ResultIngestionUsesOnePostgresReceiptTimestampPerNewBatchAndPreservesItOnReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var applicationNow = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUtcClock>();
                services.AddSingleton<IUtcClock>(new FixedClock(applicationNow));
            });
        });
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgentWithProbes(client, 2, ct);
        var first = Result(enrolled.AgentId, enrolled.ProbeIds[0], enrolled.ConfigurationVersion);
        var second = Result(enrolled.AgentId, enrolled.ProbeIds[1], enrolled.ConfigurationVersion);

        var before = await ReadPostgresTimestampAsync(postgres.ConnectionString, ct);
        var accepted = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [first, second]), HttpStatusCode.OK, ct);
        var after = await ReadPostgresTimestampAsync(postgres.ConnectionString, ct);

        Assert.Equal(new[] { first.ResultId, second.ResultId }.OrderBy(id => id).ToArray(), accepted.AcceptedResultIds);
        var receipts = await ReadLedgerReceiptsAsync(factory, enrolled.AgentId, [first.ResultId, second.ResultId], ct);
        var receipt = Assert.Single(receipts.Values.Distinct());
        Assert.InRange(receipt, before, after);
        Assert.NotEqual(applicationNow, receipt);

        var replay = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [second, first]), HttpStatusCode.OK, ct);
        Assert.Equal(new[] { first.ResultId, second.ResultId }.OrderBy(id => id).ToArray(), replay.AcceptedResultIds);
        Assert.Equal(receipts, await ReadLedgerReceiptsAsync(factory, enrolled.AgentId, [first.ResultId, second.ResultId], ct));
        await AssertLedgerCount(factory, 2, ct);
    }

    [Fact]
    public async Task ResultIngestionIsAuthenticatedValidatedIdempotentAndSanitized()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgent(client, ct);
        var result = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);

        var accepted = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]), HttpStatusCode.OK, ct);
        Assert.Equal([result.ResultId], accepted.AcceptedResultIds);
        Assert.Empty(accepted.Rejections);
        await AssertLedgerCount(factory, 1, ct);

        var replay = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]), HttpStatusCode.OK, ct);
        Assert.Equal([result.ResultId], replay.AcceptedResultIds);
        await AssertLedgerCount(factory, 1, ct);

        var conflictPayload = JsonSerializer.Serialize(new
        {
            batchId = Guid.NewGuid(),
            results = new[]
            {
                new
                {
                    result.ResultSchemaVersion, result.ResultId, result.AgentId, result.ProbeId, result.ConfigurationVersion, result.StartedAt, result.EndedAt,
                    result.AttemptCount, result.SuccessfulAttemptCount, packetLossRatio = 1m, result.MinRttMilliseconds, result.AverageRttMilliseconds, result.MaxRttMilliseconds, result.ErrorCategory
                }
            }
        }, AgentJson);
        ProbeResultIngestionBatchResponse conflict;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/result-batches") { Content = new StringContent(conflictPayload, Encoding.UTF8, "application/json") })
        { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Credential); var response = await client.SendAsync(request, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); conflict = (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!; }
        Assert.Empty(conflict.AcceptedResultIds);
        Assert.Equal("result-identity-conflict", Assert.Single(conflict.Rejections).Code);
        await AssertLedgerCount(factory, 1, ct);
        await AssertSafeConflictAudit(factory, enrolled.AgentId, result.ResultId, enrolled.Credential, ct);

        var subMicrosecondConflict = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { StartedAt = result.StartedAt.AddTicks(1) }]), HttpStatusCode.OK, ct);
        Assert.Empty(subMicrosecondConflict.AcceptedResultIds);
        Assert.Equal("result-identity-conflict", Assert.Single(subMicrosecondConflict.Rejections).Code);
        await AssertLedgerCount(factory, 1, ct);

        var sameBatchReplay = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);
        var sameBatchAcknowledgement = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [sameBatchReplay, sameBatchReplay]), HttpStatusCode.OK, ct);
        Assert.Equal([sameBatchReplay.ResultId], sameBatchAcknowledgement.AcceptedResultIds);
        Assert.Empty(sameBatchAcknowledgement.Rejections);
        await AssertLedgerCount(factory, 2, ct);

        var sameBatchConflict = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);
        var sameBatchConflictResponse = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [sameBatchConflict, sameBatchConflict with { PacketLossRatio = 1m }]), HttpStatusCode.OK, ct);
        Assert.Empty(sameBatchConflictResponse.AcceptedResultIds);
        Assert.Equal("result-identity-conflict", Assert.Single(sameBatchConflictResponse.Rejections).Code);
        await AssertLedgerCount(factory, 2, ct);

        const decimal maximumRtt = 999999999999.999999m;
        var maximumRttResponse = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion) with { MinRttMilliseconds = maximumRtt, AverageRttMilliseconds = maximumRtt, MaxRttMilliseconds = maximumRtt }]), HttpStatusCode.OK, ct);
        Assert.Single(maximumRttResponse.AcceptedResultIds);
        await AssertLedgerCount(factory, 3, ct);
        var overflowRtt = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion) with { MinRttMilliseconds = 1_000_000_000_000m, AverageRttMilliseconds = 1_000_000_000_000m, MaxRttMilliseconds = 1_000_000_000_000m }]), HttpStatusCode.OK, ct);
        Assert.Empty(overflowRtt.AcceptedResultIds);
        Assert.Equal("result-invalid", Assert.Single(overflowRtt.Rejections).Code);
        var overScaleRtt = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion) with { MinRttMilliseconds = 0.0000001m, AverageRttMilliseconds = 0.0000001m, MaxRttMilliseconds = 0.0000001m }]), HttpStatusCode.OK, ct);
        Assert.Empty(overScaleRtt.AcceptedResultIds);
        Assert.Equal("result-invalid", Assert.Single(overScaleRtt.Rejections).Code);
        await AssertLedgerCount(factory, 3, ct);

        var concurrent = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [concurrent]), HttpStatusCode.OK, ct)));
        Assert.All(responses, response => Assert.Equal([concurrent.ResultId], response.AcceptedResultIds));
        await AssertLedgerCount(factory, 4, ct);

        using (var wrongRoute = Request($"/api/v1/agents/{Guid.NewGuid()}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result])))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongRoute, ct)).StatusCode);
        using (var wrongBody = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { ResultId = Guid.NewGuid(), AgentId = Guid.NewGuid() }])))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongBody, ct)).StatusCode);

        var unsupported = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { ResultId = Guid.NewGuid(), ResultSchemaVersion = 99 }]), HttpStatusCode.OK, ct);
        Assert.Equal(AgentProblemCodes.SchemaUnsupported, Assert.Single(unsupported.Rejections).Code);
        var invalid = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result with { ResultId = Guid.NewGuid(), AttemptCount = 0 }]), HttpStatusCode.OK, ct);
        Assert.Empty(invalid.AcceptedResultIds);
        Assert.Equal("result-invalid", Assert.Single(invalid.Rejections).Code);
        await AssertLedgerCount(factory, 4, ct);

        var nullBatch = JsonSerializer.Serialize(new { batchId = Guid.NewGuid(), results = new object?[] { null } }, AgentJson);
        using (var nullRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/result-batches") { Content = new StringContent(nullBatch, Encoding.UTF8, "application/json") })
        { nullRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Credential); var response = await client.SendAsync(nullRequest, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct); Assert.Empty(body!.AcceptedResultIds); Assert.Equal("result-invalid", Assert.Single(body.Rejections).Code); }
        await AssertLedgerCount(factory, 4, ct);

        const string canary = "secret-result-canary";
        var nonUtc = JsonSerializer.Serialize(new
        {
            batchId = Guid.NewGuid(),
            results = new[] { new { resultSchemaVersion = 1, resultId = Guid.NewGuid(), agentId = enrolled.AgentId, probeId = enrolled.ProbeId, configurationVersion = enrolled.ConfigurationVersion, startedAt = "2026-08-23T13:00:00+00:00", endedAt = "2026-08-23T13:00:01Z", attemptCount = 1, successfulAttemptCount = 1, packetLossRatio = 0, errorCategory = canary } }
        }, AgentJson);
        using (var nonUtcRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{enrolled.AgentId}/result-batches") { Content = new StringContent(nonUtc, Encoding.UTF8, "application/json") })
        { nonUtcRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrolled.Credential); var response = await client.SendAsync(nonUtcRequest, ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); var body = await response.Content.ReadAsStringAsync(ct); Assert.DoesNotContain(canary, body, StringComparison.Ordinal); Assert.Equal(AgentProblemCodes.TimestampNotUtc, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()); }
        using (var badCredential = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", "malformed-secret-canary", new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [])))
        { var response = await client.SendAsync(badCredential, ct); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.DoesNotContain("malformed-secret-canary", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal); }
    }

    [Fact]
    public async Task ResultIngestionWaitsForHeldProbeLockThenCommitsAndAcknowledges()
    {
        await AssertResultIngestionWaitsForHeldProbeLockAsync(
            static (transaction, cancellationToken) => transaction.CommitAsync(cancellationToken));
    }

    [Fact]
    public async Task ResultIngestionWaitsForHeldProbeLockUntilRollbackReleasesIt()
    {
        await AssertResultIngestionWaitsForHeldProbeLockAsync(
            static (transaction, cancellationToken) => transaction.RollbackAsync(cancellationToken));
    }

    [Fact]
    public async Task T4A3ResultIngestionAcquiresReceivingAgentBeforeRequestingProbeLock()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var applicationName = "t4a3-ingestion-" + Guid.NewGuid().ToString("N");
        var requestConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", requestConnectionString));
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgent(client, ct);
        var result = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);

        await using var agentBlocker = new NpgsqlConnection(postgres.ConnectionString);
        await using var probeBlocker = new NpgsqlConnection(postgres.ConnectionString);
        await using var observer = new NpgsqlConnection(postgres.ConnectionString);
        NpgsqlTransaction? agentTransaction = null;
        NpgsqlTransaction? probeTransaction = null;
        Task<HttpResponseMessage>? pendingResponse = null;
        var agentReleased = false;
        var probeReleased = false;
        Exception? primary = null;
        try
        {
            await agentBlocker.OpenAsync(ct);
            await probeBlocker.OpenAsync(ct);
            await observer.OpenAsync(ct);
            agentTransaction = await agentBlocker.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            probeTransaction = await probeBlocker.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var agentBlockerPid = await BackendPidAsync(agentBlocker, ct);
            var probeBlockerPid = await BackendPidAsync(probeBlocker, ct);
            var observerPid = await BackendPidAsync(observer, ct);
            Assert.NotEqual(agentBlockerPid, probeBlockerPid);
            Assert.NotEqual(agentBlockerPid, observerPid);
            Assert.NotEqual(probeBlockerPid, observerPid);

            await using (var lockAgent = new NpgsqlCommand("SELECT id FROM agents WHERE id = @agentId FOR UPDATE", agentBlocker, agentTransaction))
            {
                lockAgent.Parameters.AddWithValue("agentId", enrolled.AgentId);
                Assert.Equal(enrolled.AgentId, await lockAgent.ExecuteScalarAsync(ct));
            }
            await using (var lockProbe = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@probeId, 0))", probeBlocker, probeTransaction))
            {
                lockProbe.Parameters.AddWithValue("probeId", enrolled.ProbeId.ToString("D"));
                await lockProbe.ExecuteNonQueryAsync(ct);
            }

            var requestBefore = await ReadPostgresTimestampAsync(postgres.ConnectionString, ct);
            using var request = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]));
            pendingResponse = client.SendAsync(request, ct);
            var requestPid = await WaitForAgentBlockAsync(observer, applicationName, agentBlockerPid, probeBlockerPid, pendingResponse, ct);
            Assert.NotEqual(agentBlockerPid, requestPid);
            Assert.NotEqual(probeBlockerPid, requestPid);
            await AssertLedgerCount(factory, 0, ct);

            await agentTransaction.RollbackAsync(ct);
            agentReleased = true;

            await WaitForProbeBlockAsync(observer, requestPid, enrolled.ProbeId, agentBlockerPid, probeBlockerPid, pendingResponse, ct);
            await probeTransaction.RollbackAsync(ct);
            probeReleased = true;

            using var response = await WaitForRequestCompletionAsync(pendingResponse, agentBlockerPid, probeBlockerPid, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!;
            var requestAfter = await ReadPostgresTimestampAsync(postgres.ConnectionString, ct);
            Assert.Equal([result.ResultId], body.AcceptedResultIds);
            Assert.Empty(body.Rejections);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var persisted = await db.ProbeResultLedgerEntries.AsNoTracking().SingleAsync(x => x.AgentId == enrolled.AgentId && x.ResultId == result.ResultId, ct);
            Assert.Equal((enrolled.AgentId, result.ResultId, enrolled.ProbeId, enrolled.ConfigurationVersion, result.StartedAt, result.EndedAt,
                    result.AttemptCount, result.SuccessfulAttemptCount, result.PacketLossRatio, result.MinRttMilliseconds,
                    result.AverageRttMilliseconds, result.MaxRttMilliseconds, result.ErrorCategory),
                (persisted.AgentId, persisted.ResultId, persisted.ProbeId, persisted.ConfigurationVersion, persisted.StartedAt,
                    persisted.EndedAt, persisted.AttemptCount, persisted.SuccessfulAttemptCount, persisted.PacketLossRatio,
                    persisted.MinRttMilliseconds, persisted.AverageRttMilliseconds, persisted.MaxRttMilliseconds, persisted.ErrorCategory));
            Assert.InRange(persisted.ReceivedAt, requestBefore, requestAfter);
            Assert.Equal(1, await db.ProbeResultLedgerEntries.AsNoTracking().CountAsync(ct));
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (!agentReleased && agentTransaction is not null)
                await TryRollbackAsync(agentTransaction, cleanupFailures);
            if (!probeReleased && probeTransaction is not null)
                await TryRollbackAsync(probeTransaction, cleanupFailures);
            if (pendingResponse is not null)
                await TryAwaitRequestAsync(pendingResponse, cleanupFailures);
            if (cleanupFailures.Count != 0)
            {
                if (primary is not null)
                {
                    foreach (var failure in cleanupFailures) primary.Data["T4A3CleanupFailure" + primary.Data.Count] = failure;
                }
                else throw new AggregateException("T4A3 cleanup failed.", cleanupFailures);
            }
        }
    }

    [Fact]
    public async Task ReversedOverlappingProbeBatchesCompleteAndPersistEachIdentityOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgentWithProbes(client, 2, ct);
        var orderedProbeIds = enrolled.ProbeIds.OrderBy(probeId => probeId.ToString("D"), StringComparer.Ordinal).ToArray();
        var lowProbeId = orderedProbeIds[0];
        var highProbeId = orderedProbeIds[1];
        var first = Result(enrolled.AgentId, lowProbeId, enrolled.ConfigurationVersion);
        var second = Result(enrolled.AgentId, highProbeId, enrolled.ConfigurationVersion);
        var third = Result(enrolled.AgentId, lowProbeId, enrolled.ConfigurationVersion);
        var fourth = Result(enrolled.AgentId, highProbeId, enrolled.ConfigurationVersion);

        await using var held = new EePulseDbContext(CreateOptions(postgres.ConnectionString));
        await using var heldTransaction = await held.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(held, highProbeId, ct);

        using var firstRequest = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [first, second]));
        var firstResponse = client.SendAsync(firstRequest, ct);
        Task<HttpResponseMessage>? secondResponse = null;
        var heldReleased = false;
        try
        {
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(ct);
            await WaitForProbeLockAsync(observer, lowProbeId, granted: true, [firstResponse], ct);
            await WaitForProbeLockAsync(observer, highProbeId, granted: false, [firstResponse], ct);

            using var secondRequest = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [fourth, third]));
            secondResponse = client.SendAsync(secondRequest, ct);
            await WaitForProbeLockAsync(observer, lowProbeId, granted: false, [firstResponse, secondResponse], ct);

            await heldTransaction.RollbackAsync(ct);
            heldReleased = true;

            using var firstCompletedResponse = await firstResponse.WaitAsync(ct);
            using var secondCompletedResponse = await secondResponse.WaitAsync(ct);
            Assert.Equal(HttpStatusCode.OK, firstCompletedResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondCompletedResponse.StatusCode);
            var firstBody = (await firstCompletedResponse.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!;
            var secondBody = (await secondCompletedResponse.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!;
            Assert.Equal(new[] { first.ResultId, second.ResultId }.OrderBy(x => x), firstBody.AcceptedResultIds);
            Assert.Equal(new[] { third.ResultId, fourth.ResultId }.OrderBy(x => x), secondBody.AcceptedResultIds);
            await AssertLedgerCount(factory, 4, ct);
        }
        finally
        {
            if (!heldReleased && held.Database.CurrentTransaction is not null && held.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            {
                await heldTransaction.RollbackAsync(CancellationToken.None);
            }

            await firstResponse;
            if (secondResponse is not null) await secondResponse;
        }
    }

    [Fact]
    public async Task ResultIngestionForDifferentProbeProceedsWhileAnotherProbeLockIsHeld()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgentWithProbes(client, 2, ct);

        await using var held = new EePulseDbContext(CreateOptions(postgres.ConnectionString));
        await using var heldTransaction = await held.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(held, enrolled.ProbeIds[0], ct);

        var result = Result(enrolled.AgentId, enrolled.ProbeIds[1], enrolled.ConfigurationVersion);
        var response = await Send(client, enrolled, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]), HttpStatusCode.OK, ct);
        Assert.Equal([result.ResultId], response.AcceptedResultIds);
        await AssertLedgerCount(factory, 1, ct);

        await heldTransaction.RollbackAsync(ct);
    }

    private static async Task AssertResultIngestionWaitsForHeldProbeLockAsync(
        Func<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction, CancellationToken, Task> releaseHeldTransaction)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();
        var enrolled = await EnrollConfiguredAgent(client, ct);
        var result = Result(enrolled.AgentId, enrolled.ProbeId, enrolled.ConfigurationVersion);

        await using var held = new EePulseDbContext(CreateOptions(postgres.ConnectionString));
        await using var heldTransaction = await held.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(held, enrolled.ProbeId, ct);

        using var request = Request($"/api/v1/agents/{enrolled.AgentId}/result-batches", enrolled.Credential, new ProbeResultIngestionBatchRequest(Guid.NewGuid(), [result]));
        var pendingResponse = client.SendAsync(request, ct);
        var released = false;
        try
        {
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(ct);
            await WaitForUngrantedProbeLockAsync(observer, enrolled.ProbeId, pendingResponse, ct);
            await AssertLedgerCount(factory, 0, ct);

            await releaseHeldTransaction(heldTransaction, ct);
            released = true;

            using var response = await pendingResponse;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!;
            Assert.Equal([result.ResultId], body.AcceptedResultIds);
            await AssertLedgerCount(factory, 1, ct);
        }
        finally
        {
            if (!released)
            {
                await heldTransaction.RollbackAsync(CancellationToken.None);
                await pendingResponse;
            }
        }
    }

    private static async Task WaitForUngrantedProbeLockAsync(
        NpgsqlConnection observer,
        Guid probeId,
        Task pendingRequest,
        CancellationToken cancellationToken)
    {
        var canonicalProbeId = probeId.ToString("D");
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND NOT granted
                      AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0')
                )
                """, observer);
            command.Parameters.AddWithValue("probeId", canonicalProbeId);

            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            if (pendingRequest.IsCompleted)
            {
                await pendingRequest;
                throw new Xunit.Sdk.XunitException("The ingestion request completed before waiting for its Probe transaction lock.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static async Task<int> WaitForAgentBlockAsync(NpgsqlConnection observer, string applicationName, int agentBlockerPid, int probeBlockerPid, Task pendingRequest, CancellationToken cancellationToken)
    {
        LockWaitObservation? last = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                last = await ReadLockWaitObservationAsync(observer, applicationName, null, null, deadline.Token);
                if (last is { RequestPid: not null, WaitEventType: "Lock", HasUngrantedTransactionId: true, HasAnyAdvisory: false } && last.BlockingPids.Contains(agentBlockerPid) && !last.BlockingPids.Contains(probeBlockerPid))
                    return last.RequestPid.Value;
                if (pendingRequest.IsCompleted)
                {
                    await pendingRequest;
                    throw new Xunit.Sdk.XunitException("The ingestion request completed before its Agent-row lock wait was observed.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for the Agent lock wait. agentBlockerPid={agentBlockerPid}, probeBlockerPid={probeBlockerPid}, last={last}");
        }
    }

    private static async Task WaitForProbeBlockAsync(NpgsqlConnection observer, int requestPid, Guid probeId, int agentBlockerPid, int probeBlockerPid, Task pendingRequest, CancellationToken cancellationToken)
    {
        LockWaitObservation? last = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                last = await ReadLockWaitObservationAsync(observer, null, requestPid, probeId, deadline.Token);
                if (last is { WaitEventType: "Lock", HasMatchingUngrantedAdvisory: true } && last.BlockingPids.Contains(probeBlockerPid) && !last.BlockingPids.Contains(agentBlockerPid)) return;
                if (pendingRequest.IsCompleted)
                {
                    await pendingRequest;
                    throw new Xunit.Sdk.XunitException("The ingestion request completed before its Probe advisory-lock wait was observed.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for the Probe advisory-lock wait. requestPid={requestPid}, agentBlockerPid={agentBlockerPid}, probeBlockerPid={probeBlockerPid}, probeId={probeId:D}, last={last}");
        }
    }

    private static async Task<LockWaitObservation?> ReadLockWaitObservationAsync(NpgsqlConnection observer, string? applicationName, int? requestPid, Guid? probeId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.pid, a.wait_event_type, a.wait_event,
                   EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid = a.pid AND l.locktype = 'transactionid' AND NOT l.granted),
                   EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid = a.pid AND l.locktype = 'advisory'),
                   EXISTS (SELECT 1 FROM pg_locks l WHERE l.pid = a.pid AND l.locktype = 'advisory' AND NOT l.granted
                       AND (lpad(to_hex(l.classid::bigint), 8, '0') || lpad(to_hex(l.objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0')),
                   pg_blocking_pids(a.pid)
            FROM pg_stat_activity a
            WHERE (@applicationName IS NULL OR a.application_name = @applicationName)
              AND (@requestPid IS NULL OR a.pid = @requestPid)
              AND (@applicationName IS NULL OR a.state <> 'idle')
            ORDER BY a.pid
            LIMIT 1
            """, observer);
        command.Parameters.AddWithValue("applicationName", (object?)applicationName ?? DBNull.Value);
        command.Parameters.AddWithValue("requestPid", (object?)requestPid ?? DBNull.Value);
        command.Parameters.AddWithValue("probeId", (object?)(probeId?.ToString("D")) ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetFieldValue<int[]>(6));
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task TryRollbackAsync(NpgsqlTransaction transaction, List<Exception> failures)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await transaction.RollbackAsync(timeout.Token);
        }
        catch (Exception exception) { failures.Add(exception); }
    }

    private static async Task TryAwaitRequestAsync(Task<HttpResponseMessage> request, List<Exception> failures)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await request.WaitAsync(timeout.Token);
        }
        catch (Exception exception) { failures.Add(exception); }
    }

    private static async Task<HttpResponseMessage> WaitForRequestCompletionAsync(Task<HttpResponseMessage> request, int agentBlockerPid, int probeBlockerPid, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await request.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for the ingestion request to complete. agentBlockerPid={agentBlockerPid}, probeBlockerPid={probeBlockerPid}");
        }
    }

    private static async Task WaitForProbeLockAsync(
        NpgsqlConnection observer,
        Guid probeId,
        bool granted,
        IReadOnlyCollection<Task<HttpResponseMessage>> pendingResponses,
        CancellationToken cancellationToken)
    {
        var canonicalProbeId = probeId.ToString("D");
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND granted = @granted
                      AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0')
                )
                """, observer);
            command.Parameters.AddWithValue("granted", granted);
            command.Parameters.AddWithValue("probeId", canonicalProbeId);

            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            if (pendingResponses.Any(response => response.IsCompleted))
            {
                await Task.WhenAll(pendingResponses);
                throw new Xunit.Sdk.XunitException("An ingestion request completed before the expected Probe advisory-lock state was observed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static async Task<(Guid AgentId, Guid ProbeId, long ConfigurationVersion, string Credential)> EnrollConfiguredAgent(HttpClient client, CancellationToken ct)
    {
        var enrolled = await EnrollConfiguredAgentWithProbes(client, 1, ct);
        return (enrolled.AgentId, enrolled.ProbeIds[0], enrolled.ConfigurationVersion, enrolled.Credential);
    }

    private static async Task<(Guid AgentId, IReadOnlyList<Guid> ProbeIds, long ConfigurationVersion, string Credential)> EnrollConfiguredAgentWithProbes(HttpClient client, int probeCount, CancellationToken ct)
    {
        var group = await Admin<AgentGroupResponse>(client, HttpMethod.Post, "/api/v1/agent-groups", new CreateAgentGroupRequest($"ingestion-{Guid.NewGuid():N}", null), ct);
        _ = await Admin<AgentNetworkPolicyResponse>(client, HttpMethod.Put, $"/api/v1/agent-groups/{group.Id}/allowed-networks", new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), ct);
        var site = await Admin<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", new CreateSiteRequest("ING" + Guid.NewGuid().ToString("N")[..6], "Ingestion", "UTC"), ct);
        var probeIds = new List<Guid>();
        for (var index = 0; index < probeCount; index++)
        {
            var device = await Admin<DeviceResponse>(client, HttpMethod.Post, "/api/v1/devices", new CreateDeviceRequest(site.Id, $"target-{index}", $"192.0.2.{10 + index}", null, "server", null, null, "Normal", []), ct);
            var probe = await Admin<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", new CreateProbeRequest(device.Id, group.Id, 20, 1000, 1, null, null, 1, 1), ct);
            probeIds.Add(Guid.Parse(probe.Id));
        }
        var token = await Admin<CreateAgentEnrollmentTokenResponse>(client, HttpMethod.Post, "/api/v1/agent-enrollment-tokens", new CreateAgentEnrollmentTokenRequest(1, Guid.Parse(group.Id), "ingestion", null, ["192.0.2.0/24"]), ct);
        var enrollment = await Post<AgentEnrollmentResponse>(client, "/api/v1/agents/enroll", new AgentEnrollmentRequest(1, token.EnrollmentToken, Guid.NewGuid(), "ingestion-agent", "1.2.3", token.AllowedNetworks, DateTimeOffset.UtcNow), ct);
        var configuration = await Get<AgentConfigurationResponse>(client, $"/api/v1/agents/{enrollment.AgentId}/configuration", enrollment.AgentCredential, ct);
        _ = await SendAck(client, enrollment, configuration.ConfigurationVersion, ct);
        return (enrollment.AgentId, probeIds, configuration.ConfigurationVersion, enrollment.AgentCredential);
    }

    private static ProbeResultIngestionEnvelope Result(Guid agentId, Guid probeId, long version) => new(1, Guid.NewGuid(), agentId, probeId, version, new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 23, 13, 0, 1, TimeSpan.Zero), 1, 1, 0m, 1m, 1m, 1m, null);
    private static async Task<ProbeResultIngestionBatchResponse> Send(HttpClient client, (Guid AgentId, Guid ProbeId, long ConfigurationVersion, string Credential) agent, ProbeResultIngestionBatchRequest request, HttpStatusCode expected, CancellationToken ct) { using var message = Request($"/api/v1/agents/{agent.AgentId}/result-batches", agent.Credential, request); var response = await client.SendAsync(message, ct); Assert.Equal(expected, response.StatusCode); return (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!; }
    private static async Task<ProbeResultIngestionBatchResponse> Send(HttpClient client, (Guid AgentId, IReadOnlyList<Guid> ProbeIds, long ConfigurationVersion, string Credential) agent, ProbeResultIngestionBatchRequest request, HttpStatusCode expected, CancellationToken ct) { using var message = Request($"/api/v1/agents/{agent.AgentId}/result-batches", agent.Credential, request); var response = await client.SendAsync(message, ct); Assert.Equal(expected, response.StatusCode); return (await response.Content.ReadFromJsonAsync<ProbeResultIngestionBatchResponse>(AgentJson, ct))!; }
    private static HttpRequestMessage Request(string path, string credential, object body) { var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson) }; request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); return request; }
    private static async Task<T> Admin<T>(HttpClient client, HttpMethod method, string path, object body, CancellationToken ct) { using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson) }; request.Headers.Add("X-EE-Pulse-Role", "Administrator"); request.Headers.Add("X-EE-Pulse-Actor", ActorId.ToString()); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<T>(ct))!; }
    private static async Task<T> Post<T>(HttpClient client, string path, object body, CancellationToken ct) { var response = await client.PostAsync(path, JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), AgentJson), ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<T>(AgentJson, ct))!; }
    private static async Task<T> Get<T>(HttpClient client, string path, string credential, CancellationToken ct) { using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode); return (await response.Content.ReadFromJsonAsync<T>(AgentJson, ct))!; }
    private static async Task<AgentConfigurationAcknowledgementResponse> SendAck(HttpClient client, AgentEnrollmentResponse agent, long version, CancellationToken ct) { using var request = Request($"/api/v1/agents/{agent.AgentId}/configuration/acknowledgements", agent.AgentCredential, new AgentConfigurationAcknowledgementRequest(1, Guid.NewGuid(), version, "Applied", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)); var response = await client.SendAsync(request, ct); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct)); return (await response.Content.ReadFromJsonAsync<AgentConfigurationAcknowledgementResponse>(AgentJson, ct))!; }
    private static async Task AssertLedgerCount(WebApplicationFactory<Program> factory, int expected, CancellationToken ct) { await using var scope = factory.Services.CreateAsyncScope(); Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries.CountAsync(ct)); }
    private static async Task<DateTimeOffset> ReadPostgresTimestampAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        var timestamp = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(ct));
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        return PostgresTimestamp(new DateTimeOffset(timestamp));
    }
    private static async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> ReadLedgerReceiptsAsync(WebApplicationFactory<Program> factory, Guid agentId, IReadOnlyList<Guid> resultIds, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().ProbeResultLedgerEntries
            .Where(row => row.AgentId == agentId && resultIds.Contains(row.ResultId))
            .ToDictionaryAsync(row => row.ResultId, row => row.ReceivedAt, ct);
    }
    private static DateTimeOffset PostgresTimestamp(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private static DbContextOptions<EePulseDbContext> CreateOptions(string connectionString) => new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
    private static async Task AssertSafeConflictAudit(WebApplicationFactory<Program> factory, Guid agentId, Guid resultId, string credential, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var audit = Assert.Single(await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().AuditEvents.Where(x => x.Action == "agent.result.identity-conflict" && x.EntityId == agentId).ToListAsync(ct));
        Assert.Null(audit.ActorId); Assert.Equal("Agent", audit.EntityType); Assert.Null(audit.BeforeJson); Assert.Null(audit.SourceIp); Assert.False(string.IsNullOrWhiteSpace(audit.CorrelationId));
        Assert.DoesNotContain(credential, audit.AfterJson, StringComparison.Ordinal); Assert.DoesNotContain("packetLossRatio", audit.AfterJson, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(["agentId", "reasonCode", "resultId"], metadata.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.Equal(agentId, metadata.RootElement.GetProperty("agentId").GetGuid()); Assert.Equal(resultId, metadata.RootElement.GetProperty("resultId").GetGuid()); Assert.Equal("immutable-payload-digest-mismatch", metadata.RootElement.GetProperty("reasonCode").GetString());
    }
    private static JsonSerializerOptions CreateAgentJson() { var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); AgentJsonContract.AddConverters(options); return options; }
    private sealed class FixedClock(DateTimeOffset now) : IUtcClock { public DateTimeOffset UtcNow => now; }
    private sealed record LockWaitObservation(int? RequestPid, string? WaitEventType, string? WaitEvent,
        bool HasUngrantedTransactionId, bool HasAnyAdvisory, bool HasMatchingUngrantedAdvisory, int[] BlockingPids);
}

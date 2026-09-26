using EePulse.Api.Authorization;
using EePulse.Api.Dashboard;
using EePulse.Domain.Auditing;
using EePulse.Domain.Identity;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using System.Data.Common;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EePulse.Contracts.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EePulse.IntegrationTests;

public sealed class Wp07IncidentRuntimeApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] MalformedConditionalValues = ["bad", "also-bad"];

    [Fact]
    public async Task IncidentCursorStartupRejectsInvalidConfigurationBeforeDatabaseWork()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var cases = new (string? Active, string? Ring)[]
        {
            (null, null), ("", ""), ("active", null), (null, "active=" + key),
            ("other", "active=" + key), ("-active", "-active=" + key),
            ("active", "active=" + key + ";active=" + key), ("active", "active==" + key),
            ("active", "active=" + key[..^1]), ("active", "active=" + key + ";"),
            ("active", "active=" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(31))),
            ("active", "active=" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(33))),
            ("active", "active=" + key + " "), ("active", "active=" + key[..^2] + "-="),
            ("active", "active=" + key + ";;other=" + key), ("active", "active=" + key + "="),
            ("Active", "active=" + key), (new string('a', 65), new string('a', 65) + "=" + key),
            ("é", "é=" + key), ("active", "= " + key)
        };
        foreach (var value in cases)
        {
            var observer = new RuntimeCommandObserver();
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("IncidentCursor:ActiveKeyId", value.Active ?? string.Empty);
                builder.UseSetting("IncidentCursor:KeyRing", value.Ring ?? string.Empty);
                builder.ConfigureTestServices(services => services.AddDbContext<EePulseDbContext>(options =>
                    options.UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable").AddInterceptors(observer)));
            });
            var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains(IncidentCursorKeyRing.InvalidConfigurationMessage, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(key, exception.ToString(), StringComparison.Ordinal);
            Assert.Empty(observer.Commands);
        }
    }

    [Fact]
    public async Task IncidentCommandEndpointsCommitCanonicalResponsesAndExactReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var actor = Guid.NewGuid().ToString("D");
        using var client = factory.CreateClient();

        async Task<string> CurrentEtagAsync()
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/incidents/{seed.IncidentId:D}");
            get.Headers.Add("X-EE-Pulse-Role", "Administrator");
            get.Headers.Add("X-EE-Pulse-Actor", actor);
            using var response = await client.SendAsync(get, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response.Headers.ETag!.Tag!;
        }

        async Task<HttpResponseMessage> PostAsync(string suffix, string key, string etag, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{suffix}");
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-EE-Pulse-Role", "Administrator");
            request.Headers.Add("X-EE-Pulse-Actor", actor);
            request.Headers.Add("Idempotency-Key", key);
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(request, ct);
        }

        var etag = await CurrentEtagAsync();
        using var comment = await PostAsync("comments", Key(901), etag, "{\"comment\":\"  command comment  \"}");
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        var commentBytes = await comment.Content.ReadAsByteArrayAsync(ct);
        var commentEtag = comment.Headers.ETag!.Tag!;
        Assert.Contains("command comment", Encoding.UTF8.GetString(commentBytes), StringComparison.Ordinal);

        using var replay = await PostAsync("comments", Key(901), etag, "{\"comment\":\"  command comment  \"}");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(commentEtag, replay.Headers.ETag!.Tag);
        Assert.Equal(commentBytes, await replay.Content.ReadAsByteArrayAsync(ct));

        etag = await CurrentEtagAsync();
        using var acknowledgement = await PostAsync("acknowledge", Key(902), etag, "{\"comment\":\"acknowledged\"}");
        Assert.Equal(HttpStatusCode.OK, acknowledgement.StatusCode);
        Assert.Equal("Acknowledged", JsonDocument.Parse(await acknowledgement.Content.ReadAsStreamAsync(ct)).RootElement.GetProperty("status").GetString());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET open_incident_id = {seed.IncidentId} WHERE probe_id = {seed.ProbeId}", ct);
        }
        etag = await CurrentEtagAsync();
        using var resolution = await PostAsync("resolve", Key(903), etag, "{\"note\":\"resolved manually\"}");
        Assert.Equal(HttpStatusCode.OK, resolution.StatusCode);
        Assert.Equal("Resolved", JsonDocument.Parse(await resolution.Content.ReadAsStreamAsync(ct)).RootElement.GetProperty("status").GetString());

        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(3, await verificationDb.IdempotencyReceipts.CountAsync(ct));
        Assert.Equal(1, await verificationDb.IncidentComments.CountAsync(ct));
        Assert.Equal(2, await verificationDb.IncidentLifecycleActions.CountAsync(ct));
        Assert.Null(await verificationDb.ProbeStatusProjections.Where(value => value.ProbeId == seed.ProbeId)
            .Select(value => value.OpenIncidentId).SingleAsync(ct));
    }

    [Fact]
    public async Task FreshCommentAdvancesIncidentEtagExactlyOnceAndReplayPreservesStoredResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var actor = Guid.NewGuid().ToString("D");
        var oldEtag = new IncidentEtagV1().Create(seed.IncidentId, 1);
        var key = Key(905);
        HttpRequestMessage Request(string requestKey, string etag, string comment) => new(HttpMethod.Post,
            $"/api/v1/incidents/{seed.IncidentId:D}/comments")
        {
            Content = new StringContent($"{{\"comment\":\"{comment}\"}}", Encoding.UTF8, "application/json"),
            Headers = { { "X-EE-Pulse-Role", "Administrator" }, { "X-EE-Pulse-Actor", actor },
                { "Idempotency-Key", requestKey }, { "If-Match", etag } }
        };

        byte[] body;
        string newEtag;
        string contentType;
        using (var fresh = await factory.CreateClient().SendAsync(Request(key, oldEtag, "first"), ct))
        {
            Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
            body = await fresh.Content.ReadAsByteArrayAsync(ct);
            newEtag = fresh.Headers.ETag!.Tag!;
            contentType = fresh.Content.Headers.ContentType!.ToString();
            Assert.NotEqual(oldEtag, newEtag);
        }
        using (var replay = await factory.CreateClient().SendAsync(Request(key, oldEtag, "first"), ct))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(newEtag, replay.Headers.ETag!.Tag);
            Assert.Equal(contentType, replay.Content.Headers.ContentType!.ToString());
            Assert.Equal(body, await replay.Content.ReadAsByteArrayAsync(ct));
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            Assert.Equal(2, await db.AvailabilityIncidents.Where(value => value.Id == seed.IncidentId).Select(value => value.RowVersion).SingleAsync(ct));
            Assert.Equal(1, await db.IncidentComments.CountAsync(ct));
            var receipt = await db.IdempotencyReceipts.AsNoTracking().SingleAsync(value => value.IdempotencyKey == key, ct);
            Assert.Equal((int)HttpStatusCode.Created, receipt.ResponseStatus);
            Assert.Equal(body, receipt.ResponseBody);
            Assert.Equal(contentType, receipt.ResponseContentType);
            Assert.Equal(newEtag, receipt.ResponseEtag);
        }
        var beforeStale = await SnapshotAsync(factory, ct);
        using (var stale = await factory.CreateClient().SendAsync(Request(Key(906), oldEtag, "stale"), ct))
            await AssertCommandProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict", ct);
        Assert.Equal(beforeStale, await SnapshotAsync(factory, ct));
        using (var current = await factory.CreateClient().SendAsync(Request(Key(907), newEtag, "current"), ct))
            Assert.Equal(HttpStatusCode.Created, current.StatusCode);
    }

    [Fact]
    public async Task CommandDtosUseWebJsonNamesAndPersistIndependentlyFramedV1Fingerprints()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var actor = Guid.NewGuid().ToString("D");
        const string text = "ASCII-\u00e9-\U0001F600-\uFFFD-<\"\\";
        var cases = new (string Suffix, IncidentCommandRoute Route, object Dto, HttpStatusCode Status, string CanonicalBody)[]
        {
            ("acknowledge", IncidentCommandRoute.Acknowledge, new AcknowledgeIncidentRequest(text), HttpStatusCode.OK, "{\"Comment\":\"ASCII-\u00e9-\U0001F600-\uFFFD-<\\\"\\\\\"}"),
            ("comments", IncidentCommandRoute.AddComment, new AddIncidentCommentRequest(text), HttpStatusCode.Created, "{\"Comment\":\"ASCII-\u00e9-\U0001F600-\uFFFD-<\\\"\\\\\"}"),
            ("resolve", IncidentCommandRoute.Resolve, new ResolveIncidentRequest(text), HttpStatusCode.OK, "{\"Note\":\"ASCII-\u00e9-\U0001F600-\uFFFD-<\\\"\\\\\"}"),
        };

        foreach (var value in cases)
        {
            var seed = await SeedAsync(factory, ct);
            if (value.Route == IncidentCommandRoute.Resolve)
            {
                await using var setup = factory.Services.CreateAsyncScope();
                await setup.ServiceProvider.GetRequiredService<EePulseDbContext>().Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE probe_status_projections SET open_incident_id = {seed.IncidentId} WHERE probe_id = {seed.ProbeId}", ct);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{value.Suffix}")
            {
                Content = JsonContent.Create(value.Dto),
            };
            var wire = await request.Content.ReadAsStringAsync(ct);
            Assert.Contains(value.Route == IncidentCommandRoute.Resolve ? "\"note\"" : "\"comment\"", wire, StringComparison.Ordinal);
            Assert.DoesNotContain(value.Route == IncidentCommandRoute.Resolve ? "\"Note\"" : "\"Comment\"", wire, StringComparison.Ordinal);
            Assert.Contains("\\uD83D\\uDE00", wire, StringComparison.Ordinal);
            request.Headers.Add("X-EE-Pulse-Role", "Administrator");
            request.Headers.Add("X-EE-Pulse-Actor", actor);
            var key = Key(940 + (int)value.Route);
            request.Headers.Add("Idempotency-Key", key);
            var etag = new IncidentEtagV1().Create(seed.IncidentId, 1);
            request.Headers.TryAddWithoutValidation("If-Match", etag);

            using var response = await client.SendAsync(request, ct);
            Assert.Equal(value.Status, response.StatusCode);
            await using var scope = factory.Services.CreateAsyncScope();
            var receipt = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().IdempotencyReceipts.AsNoTracking()
                .SingleAsync(row => row.IdempotencyKey == key, ct);
            var independentlyFramed = IndependentFingerprint(value.Route, seed.IncidentId, receipt.ActorId, etag,
                Encoding.UTF8.GetBytes(value.CanonicalBody));
            Assert.Equal(independentlyFramed, receipt.RequestDigest);
        }
    }

    [Fact]
    public async Task CommandRoutesPreserveDirectFourByteUtf8ScalarInPersistenceReplayAndFingerprint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var actor = Guid.NewGuid().ToString("D");
        const string scalar = "direct-\U0001F600";
        var cases = new[]
        {
            ("acknowledge", IncidentCommandRoute.Acknowledge, "comment", HttpStatusCode.OK),
            ("comments", IncidentCommandRoute.AddComment, "comment", HttpStatusCode.Created),
            ("resolve", IncidentCommandRoute.Resolve, "note", HttpStatusCode.OK),
        };
        foreach (var value in cases)
        {
            var seed = await SeedAsync(factory, ct);
            if (value.Item2 == IncidentCommandRoute.Resolve)
            {
                await using var setup = factory.Services.CreateAsyncScope();
                await setup.ServiceProvider.GetRequiredService<EePulseDbContext>().Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE probe_status_projections SET open_incident_id = {seed.IncidentId} WHERE probe_id = {seed.ProbeId}", ct);
            }
            var body = Encoding.UTF8.GetBytes($"{{\"{value.Item3}\":\"{scalar}\"}}");
            Assert.True(body.AsSpan().IndexOf(new byte[] { 0xf0, 0x9f, 0x98, 0x80 }) >= 0);
            var key = Key(960 + (int)value.Item2);
            var etag = new IncidentEtagV1().Create(seed.IncidentId, 1);
            HttpRequestMessage Request()
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{value.Item1}")
                {
                    Content = new ByteArrayContent(body),
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                request.Headers.Add("X-EE-Pulse-Role", "Administrator");
                request.Headers.Add("X-EE-Pulse-Actor", actor);
                request.Headers.Add("Idempotency-Key", key);
                request.Headers.TryAddWithoutValidation("If-Match", etag);
                return request;
            }
            byte[] freshBody;
            string freshEtag;
            using (var fresh = await client.SendAsync(Request(), ct))
            {
                Assert.Equal(value.Item4, fresh.StatusCode);
                freshBody = await fresh.Content.ReadAsByteArrayAsync(ct);
                freshEtag = fresh.Headers.ETag!.Tag!;
                if (value.Item2 == IncidentCommandRoute.AddComment)
                    Assert.Equal(scalar, JsonDocument.Parse(freshBody).RootElement.GetProperty("comment").GetString());
            }
            using (var replay = await client.SendAsync(Request(), ct))
            {
                Assert.Equal(value.Item4, replay.StatusCode);
                Assert.Equal(freshEtag, replay.Headers.ETag!.Tag);
                Assert.Equal(freshBody, await replay.Content.ReadAsByteArrayAsync(ct));
            }
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var receipt = await db.IdempotencyReceipts.AsNoTracking().SingleAsync(row => row.IdempotencyKey == key, ct);
            var canonicalBody = Encoding.UTF8.GetBytes($"{{\"{(value.Item2 == IncidentCommandRoute.Resolve ? "Note" : "Comment")}\":\"{scalar}\"}}");
            Assert.Equal(IndependentFingerprint(value.Item2, seed.IncidentId, receipt.ActorId, etag, canonicalBody), receipt.RequestDigest);
            if (value.Item2 == IncidentCommandRoute.AddComment)
                Assert.Equal(scalar, await db.IncidentComments.Where(row => row.IdempotencyKey == key).Select(row => row.Comment).SingleAsync(ct));
            else
                Assert.Equal(scalar, await db.IncidentLifecycleActions.Where(row => row.IdempotencyKey == key).Select(row => row.ActionNote).SingleAsync(ct));
        }
    }

    [Fact]
    public async Task CommandRequestBodyLimitIsStreamingAndRejectsKnownAndUnknownLengthBeforeDatabaseWork()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString,
            services => services.AddSingleton<IStartupFilter, EmptyHeaderFilter>(), observer);
        var seed = await SeedAsync(factory, ct);
        var baseline = await SnapshotAsync(factory, ct);
        observer.Clear();
        var body = new byte[(16 * 1024) + 1];

        foreach (var route in new[] { "acknowledge", "comments", "resolve" })
        {
            foreach (var content in new HttpContent[]
            {
                new ByteArrayContent(body),
                new StreamContent(new NonSeekableReadStream(body)),
            })
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{route}") { Content = content };
                request.Headers.Add("X-EE-Pulse-Role", "Operator");
                request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
                request.Headers.TryAddWithoutValidation("If-Match", new IncidentEtagV1().Create(seed.IncidentId, 1));
                observer.Clear();
                using var response = await factory.CreateClient().SendAsync(request, ct);
                await AssertCommandProblemAsync(response, HttpStatusCode.BadRequest, "invalid-json", ct);
                Assert.Empty(observer.Commands);
                Assert.Equal(baseline, await SnapshotAsync(factory, ct));
            }
        }
    }

    [Fact]
    public async Task FreshReplayAndReadUseOnePostgresMicrosecondCommandTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ReadClock(Now.AddTicks(7));
        var expected = new DateTimeOffset(clock.UtcNow.Ticks - (clock.UtcNow.Ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<EePulse.Application.Time.IUtcClock>();
            services.AddSingleton<EePulse.Application.Time.IUtcClock>(clock);
        });
        var actor = Guid.NewGuid().ToString("D");
        var cases = new[]
        {
            ("acknowledge", IncidentCommandRoute.Acknowledge, "{\"comment\":\"microsecond\"}", HttpStatusCode.OK, "completedAt", "lifecycle-events"),
            ("comments", IncidentCommandRoute.AddComment, "{\"comment\":\"microsecond\"}", HttpStatusCode.Created, "createdAt", "comments"),
            ("resolve", IncidentCommandRoute.Resolve, "{\"note\":\"microsecond\"}", HttpStatusCode.OK, "completedAt", "lifecycle-events"),
        };
        foreach (var value in cases)
        {
            var seed = await SeedAsync(factory, ct);
            if (value.Item2 == IncidentCommandRoute.Resolve)
            {
                await using var setup = factory.Services.CreateAsyncScope();
                await setup.ServiceProvider.GetRequiredService<EePulseDbContext>().Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE probe_status_projections SET open_incident_id = {seed.IncidentId} WHERE probe_id = {seed.ProbeId}", ct);
            }
            var key = Key(950 + (int)value.Item2);
            HttpRequestMessage Request()
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{value.Item1}")
                {
                    Content = new StringContent(value.Item3, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-EE-Pulse-Role", "Administrator");
                request.Headers.Add("X-EE-Pulse-Actor", actor);
                request.Headers.Add("Idempotency-Key", key);
                request.Headers.TryAddWithoutValidation("If-Match", new IncidentEtagV1().Create(seed.IncidentId, 1));
                return request;
            }
            byte[] freshBody;
            using (var fresh = await factory.CreateClient().SendAsync(Request(), ct))
            {
                Assert.Equal(value.Item4, fresh.StatusCode);
                freshBody = await fresh.Content.ReadAsByteArrayAsync(ct);
                using var document = JsonDocument.Parse(freshBody);
                Assert.Equal(expected, document.RootElement.GetProperty(value.Item5).GetDateTimeOffset());
            }
            using (var replay = await factory.CreateClient().SendAsync(Request(), ct))
            {
                Assert.Equal(value.Item4, replay.StatusCode);
                Assert.Equal(freshBody, await replay.Content.ReadAsByteArrayAsync(ct));
            }
            using (var history = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/incidents/{seed.IncidentId:D}/{value.Item6}"))
            {
                history.Headers.Add("X-EE-Pulse-Role", "Viewer");
                using var response = await factory.CreateClient().SendAsync(history, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                Assert.Equal(expected, document.RootElement.GetProperty("items")[0].GetProperty(value.Item5 == "createdAt" ? "createdAt" : "occurredAt").GetDateTimeOffset());
            }
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var receipt = await db.IdempotencyReceipts.AsNoTracking().SingleAsync(row => row.IdempotencyKey == key, ct);
            Assert.Equal(expected, receipt.CreatedAt);
            Assert.Equal(expected, receipt.CompletedAt);
            if (value.Item2 == IncidentCommandRoute.AddComment)
                Assert.Equal(expected, await db.IncidentComments.AsNoTracking().Where(row => row.IdempotencyKey == key).Select(row => row.CreatedAt).SingleAsync(ct));
            else
                Assert.Equal(expected, await db.IncidentLifecycleActions.AsNoTracking().Where(row => row.IdempotencyKey == key).Select(row => row.OccurredAt).SingleAsync(ct));
        }
    }

    [Fact]
    public async Task IncidentCommandRoutesEnforceAuthorizationAndFrozenPreDatabasePrecedence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString,
            services => services.AddSingleton<IStartupFilter, EmptyHeaderFilter>(), observer);
        var seed = await SeedAsync(factory, ct);
        var baseline = await SnapshotAsync(factory, ct);
        observer.Clear();
        var actor = Guid.NewGuid().ToString("D");
        var routes = new[]
        {
            ("acknowledge", "{\"comment\":\"ack\"}", HttpStatusCode.OK),
            ("comments", "{\"comment\":\"comment\"}", HttpStatusCode.Created),
            ("resolve", "{\"note\":\"resolve\"}", HttpStatusCode.OK)
        };

        HttpRequestMessage Request(string route, string? role, string? requestActor, string? key, string? ifMatch,
            string? contentType, byte[]? body, string suffix = "")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{route}{suffix}");
            if (role is not null) request.Headers.Add("X-EE-Pulse-Role", role);
            if (requestActor is not null) request.Headers.Add("X-EE-Pulse-Actor", requestActor);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null) request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
            return request;
        }

        async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken cancellationToken)
        {
            Assert.Equal(status, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
            Assert.True(Guid.TryParseExact(response.Headers.GetValues("X-Correlation-ID").Single(), "N", out _));
            Assert.Empty(observer.Commands);
        }

        async Task AssertPreDatabaseCaseAsync(string route, string body, HttpStatusCode status, string code,
            Action<HttpRequestMessage>? alter = null)
        {
            using var request = Request(route, "Operator", actor, Key(2000 + Array.IndexOf(routes, routes.Single(value => value.Item1 == route))),
                new IncidentEtagV1().Create(seed.IncidentId, 1), "application/json", Encoding.UTF8.GetBytes(body));
            alter?.Invoke(request);
            using var response = await factory.CreateClient().SendAsync(request, ct);
            await AssertProblemAsync(response, status, code, ct);
            Assert.Equal(baseline, await SnapshotAsync(factory, ct));
            observer.Clear();
        }

        foreach (var route in routes)
        {
            using (var unauthenticated = Request(route.Item1, null, null, null, null, null, null))
            using (var response = await factory.CreateClient().SendAsync(unauthenticated, ct)) { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Empty(observer.Commands); }
            foreach (var forbidden in new[] { "Viewer", "Engineer", "Auditor" })
            {
                using var request = Request(route.Item1, forbidden, actor, Key(1000 + Array.IndexOf(routes, route)), "\"bad\"", "application/json", Encoding.UTF8.GetBytes(route.Item2));
                using var response = await factory.CreateClient().SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Empty(observer.Commands);
            }
            using (var missingActor = Request(route.Item1, "Operator", null, Key(1100 + Array.IndexOf(routes, route)), "\"bad\"", "application/json", Encoding.UTF8.GetBytes(route.Item2)))
            using (var response = await factory.CreateClient().SendAsync(missingActor, ct)) await AssertProblemAsync(response, HttpStatusCode.Forbidden, "invalid-incident-actor-identity", ct);
            using (var invalidQuery = Request(route.Item1, "Operator", actor, Key(1200 + Array.IndexOf(routes, route)), "\"bad\"", "application/json", Encoding.UTF8.GetBytes(route.Item2), "?unexpected=1"))
            using (var response = await factory.CreateClient().SendAsync(invalidQuery, ct)) await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-incident-query", ct);
            using (var invalidId = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/INVALID/{route.Item1}"))
            {
                invalidId.Headers.Add("X-EE-Pulse-Role", "Operator"); invalidId.Headers.Add("X-EE-Pulse-Actor", actor);
                using var response = await factory.CreateClient().SendAsync(invalidId, ct);
                await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-incident-id", ct);
            }
            using (var zeroId = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/incidents/{Guid.Empty:D}/{route.Item1}"))
            {
                zeroId.Headers.Add("X-EE-Pulse-Role", "Operator"); zeroId.Headers.Add("X-EE-Pulse-Actor", actor);
                using var response = await factory.CreateClient().SendAsync(zeroId, ct);
                await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-idempotency-key", ct);
            }
            using (var missingKey = Request(route.Item1, "Operator", actor, null, "\"bad\"", "application/json", Encoding.UTF8.GetBytes(route.Item2)))
            using (var response = await factory.CreateClient().SendAsync(missingKey, ct)) await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-idempotency-key", ct);
            using (var missingIfMatch = Request(route.Item1, "Operator", actor, Key(1300 + Array.IndexOf(routes, route)), null, "application/json", Encoding.UTF8.GetBytes(route.Item2)))
            using (var response = await factory.CreateClient().SendAsync(missingIfMatch, ct)) await AssertProblemAsync(response, (HttpStatusCode)428, "precondition-required", ct);
            using (var invalidContent = Request(route.Item1, "Operator", actor, Key(1400 + Array.IndexOf(routes, route)), "\"bad\"", "text/plain", Encoding.UTF8.GetBytes(route.Item2)))
            using (var response = await factory.CreateClient().SendAsync(invalidContent, ct)) await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-content-type", ct);
            using (var malformedUtf8 = Request(route.Item1, "Operator", actor, Key(1500 + Array.IndexOf(routes, route)), "\"bad\"", "application/json", [0xc3, 0x28]))
            using (var response = await factory.CreateClient().SendAsync(malformedUtf8, ct)) await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-json", ct);

            // Every form below must fail before parsing can reach the database.  Exercise
            // all three command routes rather than treating coordinator-only coverage as HTTP evidence.
            var textProperty = route.Item1 == "resolve" ? "note" : "comment";
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-idempotency-key",
                request => request.Headers.Remove("Idempotency-Key"));
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-idempotency-key",
                request => { request.Headers.Remove("Idempotency-Key"); request.Headers.Add("Idempotency-Key", "not-a-guid"); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-idempotency-key",
                request => { request.Headers.Remove("Idempotency-Key"); request.Headers.Add("Idempotency-Key", new string('a', 128)); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-idempotency-key",
                request => { request.Headers.Remove("Idempotency-Key"); request.Headers.TryAddWithoutValidation("Idempotency-Key", Key(2100)); request.Headers.TryAddWithoutValidation("Idempotency-Key", Key(2101)); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-idempotency-key",
                request => { request.Headers.Remove("Idempotency-Key"); request.Headers.Add("X-Test-Empty-Header", "Idempotency-Key"); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-if-match",
                request => { request.Headers.Remove("If-Match"); request.Headers.TryAddWithoutValidation("If-Match", "W/\"weak\""); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-if-match",
                request => { request.Headers.Remove("If-Match"); request.Headers.TryAddWithoutValidation("If-Match", "*"); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-if-match",
                request => { request.Headers.Remove("If-Match"); request.Headers.TryAddWithoutValidation("If-Match", "\"one\", \"two\""); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-if-match",
                request => { request.Headers.Remove("If-Match"); request.Headers.TryAddWithoutValidation("If-Match", "\"one\""); request.Headers.TryAddWithoutValidation("If-Match", "\"two\""); });
            await AssertPreDatabaseCaseAsync(route.Item1, route.Item2, HttpStatusCode.BadRequest, "invalid-if-match",
                request => { request.Headers.Remove("If-Match"); request.Headers.Add("X-Test-Empty-Header", "If-Match"); });
            await AssertPreDatabaseCaseAsync(route.Item1, "{}", HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":null}}", HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":123}}", HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"x\",\"{textProperty}\":\"y\"}}", HttpStatusCode.BadRequest, "invalid-json");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"x\",\"unknown\":\"y\"}}", HttpStatusCode.BadRequest, "invalid-json");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"\\uD800\"}}", HttpStatusCode.BadRequest, "invalid-json");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"\\u0000\"}}", HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"\\u2028\"}}", HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"   \"}}", HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertPreDatabaseCaseAsync(route.Item1, $"{{\"{textProperty}\":\"{new string('x', 2001)}\"}}", HttpStatusCode.BadRequest, "invalid-incident-text");
        }
        Assert.Equal(baseline, await SnapshotAsync(factory, ct));

        foreach (var role in new[] { "Operator", "Administrator" })
        {
            foreach (var route in routes)
            {
                var current = await SeedAsync(factory, ct);
                if (route.Item1 == "resolve")
                {
                    await using var scope = factory.Services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET open_incident_id = {current.IncidentId} WHERE probe_id = {current.ProbeId}", ct);
                }
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{current.IncidentId:D}/{route.Item1}");
                request.Headers.Add("X-EE-Pulse-Role", role); request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
                request.Headers.Add("Idempotency-Key", Key(1600 + Array.IndexOf(routes, route) + (role == "Operator" ? 0 : 10)));
                request.Headers.TryAddWithoutValidation("If-Match", new IncidentEtagV1().Create(current.IncidentId, 1));
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(route.Item2));
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await factory.CreateClient().SendAsync(request, ct);
                Assert.Equal(route.Item3, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task AllZeroIncidentIdRunsEveryCommandValidationBeforeFrozenNotFoundWithoutCommandExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        var coordinator = new FailIfCalledCoordinator();
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<IIncidentCommandCoordinator>();
            services.AddSingleton<IIncidentCommandCoordinator>(coordinator);
        }, observer);
        var seed = await SeedAsync(factory, ct);
        var baseline = await SnapshotAsync(factory, ct);
        var actor = Guid.NewGuid().ToString("D");
        var routes = new[]
        {
            (Suffix: "acknowledge", Body: "{\"comment\":\"ack\"}"),
            (Suffix: "comments", Body: "{\"comment\":\"comment\"}"),
            (Suffix: "resolve", Body: "{\"note\":\"resolve\"}")
        };

        HttpRequestMessage Request(string suffix, byte[]? body, string? key, string? ifMatch, string? contentType,
            string query = "", bool includeActor = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{Guid.Empty:D}/{suffix}{query}");
            request.Headers.Add("X-EE-Pulse-Role", "Operator");
            if (includeActor) request.Headers.Add("X-EE-Pulse-Actor", actor);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null) request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
            return request;
        }

        async Task AssertBeforeNotFoundAsync(HttpRequestMessage request, HttpStatusCode status, string code)
        {
            observer.Clear();
            coordinator.Calls = 0;
            using var response = await factory.CreateClient().SendAsync(request, ct);
            await AssertCommandProblemAsync(response, status, code, ct);
            Assert.Empty(observer.Commands);
            Assert.Equal(0, coordinator.Calls);
            Assert.Equal(baseline, await SnapshotAsync(factory, ct));
        }

        foreach (var route in routes)
        {
            // ADR-013 §10 command precedence: actor identity, query, canonical route ID,
            // Idempotency-Key, If-Match, content type, streaming/UTF-8/JSON, then text.
            await AssertBeforeNotFoundAsync(Request(route.Suffix, null, null, null, null, includeActor: false),
                HttpStatusCode.Forbidden, "invalid-incident-actor-identity");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, null, null, null, null, "?unexpected=1"),
                HttpStatusCode.BadRequest, "invalid-incident-query");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, Encoding.UTF8.GetBytes(route.Body), null, "\"strong\"", "application/json"),
                HttpStatusCode.BadRequest, "invalid-idempotency-key");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, Encoding.UTF8.GetBytes(route.Body), Key(3300), null, "application/json"),
                (HttpStatusCode)428, "precondition-required");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, Encoding.UTF8.GetBytes(route.Body), Key(3301), "W/\"weak\"", "application/json"),
                HttpStatusCode.BadRequest, "invalid-if-match");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, Encoding.UTF8.GetBytes(route.Body), Key(3302), "\"strong\"", "text/plain"),
                HttpStatusCode.BadRequest, "invalid-content-type");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, Encoding.UTF8.GetBytes("{" + new string('x', 16 * 1024)), Key(3303), "\"strong\"", "application/json"),
                HttpStatusCode.BadRequest, "invalid-json");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, [0xc3, 0x28], Key(3304), "\"strong\"", "application/json"),
                HttpStatusCode.BadRequest, "invalid-json");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, "{}"u8.ToArray(), Key(3305), "\"strong\"", "application/json"),
                HttpStatusCode.BadRequest, "invalid-incident-text");
            await AssertBeforeNotFoundAsync(Request(route.Suffix, Encoding.UTF8.GetBytes(route.Body), Key(3306), "\"strong\"", "application/json"),
                HttpStatusCode.NotFound, "incident-not-found");
        }
    }

    [Fact]
    public async Task DevelopmentWithoutPostgresKeepsIncidentCommandsInsideFrozenEndpointBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateMissingPostgresFactory(observer);
        using var client = factory.CreateClient();
        var incidentId = Guid.NewGuid();
        var actor = Guid.NewGuid().ToString("D");
        var routes = new[]
        {
            (Suffix: "acknowledge", Body: "{\"comment\":\"ack\"}"),
            (Suffix: "comments", Body: "{\"comment\":\"comment\"}"),
            (Suffix: "resolve", Body: "{\"note\":\"resolve\"}")
        };

        HttpRequestMessage Command(string suffix, string body, string? key = null, string? ifMatch = null,
            string? query = null, string? role = "Operator", string? incidentPath = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/incidents/{incidentPath ?? incidentId.ToString("D")}/{suffix}{query}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (role is not null)
            {
                request.Headers.Add("X-EE-Pulse-Role", role);
                request.Headers.Add("X-EE-Pulse-Actor", actor);
            }
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return request;
        }

        async Task AssertEndpointProblemAsync(HttpRequestMessage request, HttpStatusCode status, string code)
        {
            observer.Clear();
            using var response = await client.SendAsync(request, ct);
            await AssertCommandProblemAsync(response, status, code, ct);
            Assert.Empty(observer.Commands);
        }

        foreach (var route in routes)
        {
            await AssertEndpointProblemAsync(Command(route.Suffix, route.Body), HttpStatusCode.BadRequest, "invalid-idempotency-key");
            await AssertEndpointProblemAsync(Command(route.Suffix, route.Body, Key(3000), "\"strong\"", "?unexpected=1"),
                HttpStatusCode.BadRequest, "invalid-incident-query");
            await AssertEndpointProblemAsync(Command(route.Suffix, route.Body, incidentPath: "INVALID"),
                HttpStatusCode.BadRequest, "invalid-incident-id");
            await AssertEndpointProblemAsync(Command(route.Suffix, route.Body, Key(3001), "W/\"weak\""),
                HttpStatusCode.BadRequest, "invalid-if-match");
            await AssertEndpointProblemAsync(Command(route.Suffix, "{", Key(3002), "\"strong\""),
                HttpStatusCode.BadRequest, "invalid-json");
            await AssertEndpointProblemAsync(Command(route.Suffix, route.Body, incidentPath: Guid.Empty.ToString("D")),
                HttpStatusCode.BadRequest, "invalid-idempotency-key");
            await AssertEndpointProblemAsync(Command(route.Suffix, route.Body, Key(3003), "\"strong\"", incidentPath: Guid.Empty.ToString("D")),
                HttpStatusCode.NotFound, "incident-not-found");

            using (var valid = Command(route.Suffix, route.Body, Key(3100 + Array.IndexOf(routes, route)), "\"strong\""))
            using (var response = await client.SendAsync(valid, ct))
            {
                await AssertCommandProblemAsync(response, HttpStatusCode.ServiceUnavailable, "incident-command-unavailable", ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                Assert.DoesNotContain("PostgreSQL persistence is not configured.", body, StringComparison.Ordinal);
            }

            using (var unauthenticated = Command(route.Suffix, route.Body, role: null))
            using (var response = await client.SendAsync(unauthenticated, ct))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Empty(observer.Commands);
            }

            using (var forbidden = Command(route.Suffix, route.Body, role: "Viewer"))
            using (var response = await client.SendAsync(forbidden, ct))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Empty(observer.Commands);
            }
        }

        using var unrelated = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sites");
        unrelated.Headers.Add("X-EE-Pulse-Role", "Administrator");
        unrelated.Headers.Add("X-EE-Pulse-Actor", actor);
        using var unrelatedResponse = await client.SendAsync(unrelated, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unrelatedResponse.StatusCode);
        using var unrelatedBody = JsonDocument.Parse(await unrelatedResponse.Content.ReadAsStringAsync(ct));
        Assert.Equal("Dependency unavailable", unrelatedBody.RootElement.GetProperty("title").GetString());
        Assert.Equal("PostgreSQL persistence is not configured.", unrelatedBody.RootElement.GetProperty("detail").GetString());
        Assert.Empty(observer.Commands);
    }

    [Fact]
    public async Task IncidentReadFailuresAreSanitizedAndCallerCancellationDoesNotMutate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var failure = new ReadFailureInterceptor();
        var logs = new IncidentLogSink();
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.RemoveAll<ILoggerProvider>();
            services.RemoveAll<Serilog.ILogger>();
            services.AddSingleton<Serilog.ILogger>(_ => new Serilog.LoggerConfiguration().Enrich.FromLogContext()
                .Filter.ByExcluding(IncidentEndpoints.IsUnsafeRequestLog).WriteTo.Sink(logs).CreateLogger());
            services.AddSingleton<ILoggerProvider>(provider => new Serilog.Extensions.Logging.SerilogLoggerProvider(
                provider.GetRequiredService<Serilog.ILogger>(), dispose: false));
            services.AddSingleton<ILoggerFactory>(provider => new LoggerFactory([provider.GetRequiredService<ILoggerProvider>()]));
        }, failure);
        var seed = await SeedAsync(factory, ct);
        Guid deviceId;
        await using (var scope = factory.Services.CreateAsyncScope())
            deviceId = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().Probes
                .Where(probe => probe.Id == seed.ProbeId).Select(probe => probe.DeviceId).SingleAsync(ct);
        var baseline = await SnapshotAsync(factory, ct);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-EE-Pulse-Role", "Viewer");
        var paths = new[] { "/api/v1/incidents", $"/api/v1/incidents/{seed.IncidentId:D}", $"/api/v1/devices/{deviceId:D}/incidents",
            $"/api/v1/incidents/{seed.IncidentId:D}/lifecycle-events", $"/api/v1/incidents/{seed.IncidentId:D}/comments" };
        foreach (var path in paths)
        {
            foreach (var providerCancellation in new[] { false, true })
            {
                logs.Entries.Clear();
                failure.Fail = true;
                failure.ProviderCancellation = providerCancellation;
                using var response = await client.GetAsync(path, ct);
                await AssertReadProblem(response, "incident-read-unavailable", HttpStatusCode.ServiceUnavailable, ct);
                Assert.DoesNotContain("provider-secret", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
                var unavailable = Assert.Single(logs.Entries, entry => entry.MessageTemplate.Text == "Incident read request {CorrelationId} failed.");
                Assert.Null(unavailable.Exception);
                Assert.Null(unavailable.TraceId);
                Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(),
                    Assert.IsType<Serilog.Events.ScalarValue>(unavailable.Properties["CorrelationId"]).Value);
                Assert.All(logs.Entries, entry => Assert.DoesNotContain("provider-secret", entry.ToString(), StringComparison.Ordinal));
                failure.Fail = false;
                Assert.Equal(baseline, await SnapshotAsync(factory, ct));
            }
            using var cancellation = new CancellationTokenSource();
            logs.Entries.Clear();
            failure.CancelCaller = cancellation;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(path, cancellation.Token));
            Assert.DoesNotContain(logs.Entries, entry => entry.MessageTemplate.Text == "Incident read request {CorrelationId} failed.");
            failure.CancelCaller = null;
            Assert.Equal(baseline, await SnapshotAsync(factory, ct));
        }
    }

    [Fact]
    public async Task IncidentCommandProviderAndCallerCancellationUseDifferentHttpBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var failure = new ReadFailureInterceptor();
        await using var factory = CreateFactory(postgres.ConnectionString, null, failure);
        using var client = factory.CreateClient();

        foreach (var route in new[] { "acknowledge", "comments", "resolve" })
        {
            var seed = await SeedAsync(factory, ct);
            var baseline = await SnapshotAsync(factory, ct);
            HttpRequestMessage Request(string key)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{seed.IncidentId:D}/{route}")
                {
                    Content = new StringContent(route == "resolve" ? "{\"note\":\"cancel\"}" : "{\"comment\":\"cancel\"}", Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-EE-Pulse-Role", "Operator");
                request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
                request.Headers.Add("Idempotency-Key", key);
                request.Headers.TryAddWithoutValidation("If-Match", new IncidentEtagV1().Create(seed.IncidentId, 1));
                return request;
            }

            failure.Fail = true;
            failure.ProviderCancellation = true;
            using (var provider = await client.SendAsync(Request(Guid.NewGuid().ToString("D")), ct))
                await AssertCommandProblemAsync(provider, HttpStatusCode.ServiceUnavailable, "incident-command-unavailable", ct);
            failure.Fail = false;
            failure.ProviderCancellation = false;
            Assert.Equal(baseline, await SnapshotAsync(factory, ct));

            using var callerCancellation = new CancellationTokenSource();
            failure.CancelCaller = callerCancellation;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Request(Guid.NewGuid().ToString("D")), callerCancellation.Token));
            failure.CancelCaller = null;
            Assert.Equal(baseline, await SnapshotAsync(factory, ct));
        }
    }

    [Fact]
    public async Task TransactionAcquisitionCancellationHasDistinctCoordinatorAndHttpBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        HttpRequestMessage Request(Guid incidentId, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{incidentId:D}/comments")
            {
                Content = new StringContent("{\"comment\":\"transaction cancellation\"}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-EE-Pulse-Role", "Operator");
            request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
            request.Headers.Add("Idempotency-Key", key);
            request.Headers.TryAddWithoutValidation("If-Match", new IncidentEtagV1().Create(incidentId, 1));
            return request;
        }

        await using (var providerFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.BeginProviderCancellation))))
        {
            var seed = await SeedAsync(providerFactory, ct);
            var before = await SnapshotAsync(providerFactory, ct);
            using var provider = await providerFactory.CreateClient().SendAsync(Request(seed.IncidentId, Key(970)), ct);
            await AssertCommandProblemAsync(provider, HttpStatusCode.ServiceUnavailable, "incident-command-unavailable", ct);
            Assert.Equal(before, await SnapshotAsync(providerFactory, ct));
        }

        using var callerCancellation = new CancellationTokenSource();
        await using (var callerFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.BeginCallerCancellation, callerCancellation))))
        {
            var seed = await SeedAsync(callerFactory, ct);
            var before = await SnapshotAsync(callerFactory, ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerFactory.CreateClient()
                .SendAsync(Request(seed.IncidentId, Key(971)), callerCancellation.Token));
            Assert.Equal(before, await SnapshotAsync(callerFactory, ct));
        }
    }

    private sealed class ReadFailureInterceptor : DbCommandInterceptor
    {
        internal bool Fail { get; set; }
        internal bool ProviderCancellation { get; set; }
        internal CancellationTokenSource? CancelCaller { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (CancelCaller is not null) { CancelCaller.Cancel(); throw new OperationCanceledException(cancellationToken); }
            if (Fail) throw ProviderCancellation ? new OperationCanceledException("provider-secret") : new NpgsqlException("provider-secret");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class IncidentLogSink : Serilog.Core.ILogEventSink
    {
        internal System.Collections.Concurrent.ConcurrentQueue<Serilog.Events.LogEvent> Entries { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Entries.Enqueue(logEvent);
    }

    [Fact]
    public async Task IncidentReadCursorsSurviveHostsAndTraverseBothSortsWithoutGaps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var ring = IncidentCursorKeyRingTestHost.CreateRing();
        var clock = new ReadClock(Now.AddDays(1));
        await using var firstHost = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<EePulse.Application.Time.IUtcClock>();
            services.AddSingleton<EePulse.Application.Time.IUtcClock>(clock);
        }).WithIncidentCursorKeyRing(ring);
        var seed = await SeedAsync(firstHost, ct);
        Guid deviceId;
        var incidents = new List<(Guid Id, DateTimeOffset At)> { (seed.IncidentId, Now) };
        await using (var scope = firstHost.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            deviceId = await db.Probes.Where(probe => probe.Id == seed.ProbeId).Select(probe => probe.DeviceId).SingleAsync(ct);
            for (var index = 0; index < 5; index++)
            {
                var id = Guid.NewGuid();
                var at = Now.AddHours(-(index / 2 + 1));
                var row = new AvailabilityIncident(id, seed.ProbeId, at);
                row.ResolveForConfirmedRecovery(at.AddSeconds(index));
                db.Add(row);
                incidents.Add((id, at));
            }
            await db.SaveChangesAsync(ct);
        }
        await using var secondHost = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<EePulse.Application.Time.IUtcClock>();
            services.AddSingleton<EePulse.Application.Time.IUtcClock>(clock);
        }).WithIncidentCursorKeyRing(ring);
        using var first = firstHost.CreateClient();
        using var second = secondHost.CreateClient();
        first.DefaultRequestHeaders.Add("X-EE-Pulse-Role", "Viewer");
        second.DefaultRequestHeaders.Add("X-EE-Pulse-Role", "Viewer");
        var before = await SnapshotAsync(firstHost, ct);
        string? sampleCursor = null;
        foreach (var route in new[] { "/api/v1/incidents", $"/api/v1/devices/{deviceId:D}/incidents" })
        {
            foreach (var sort in new[] { "OpenedAtDesc", "OpenedAtAsc" })
            {
                var actual = new List<string>();
                string? cursor = null;
                do
                {
                    using var response = await (actual.Count == 0 ? first : second).GetAsync(route + "?pageSize=2&sort=" + sort +
                        (cursor is null ? "" : "&cursor=" + cursor), ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    actual.AddRange(json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!));
                    cursor = json.RootElement.GetProperty("nextCursor").GetString();
                    if (route == "/api/v1/incidents" && sort == "OpenedAtDesc" && sampleCursor is null) sampleCursor = cursor;
                    Assert.True(actual.Count <= incidents.Count);
                } while (cursor is not null);
                var expected = (sort == "OpenedAtDesc" ? incidents.OrderByDescending(row => row.At) : incidents.OrderBy(row => row.At))
                    .ThenByDescending(row => row.Id.ToString("D"), StringComparer.Ordinal).Select(row => row.Id.ToString("D")).ToArray();
                Assert.Equal(expected, actual);
            }
        }
        Assert.NotNull(sampleCursor);
        // Rebuild a host after the issuing host is disposed, retaining only the fixture ring.
        await firstHost.DisposeAsync();
        await using var restartedHost = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<EePulse.Application.Time.IUtcClock>();
            services.AddSingleton<EePulse.Application.Time.IUtcClock>(clock);
        }).WithIncidentCursorKeyRing(ring);
        using var restarted = restartedHost.CreateClient();
        restarted.DefaultRequestHeaders.Add("X-EE-Pulse-Role", "Viewer");
        using (var afterRestart = await restarted.GetAsync("/api/v1/incidents?cursor=" + sampleCursor, ct)) Assert.Equal(HttpStatusCode.OK, afterRestart.StatusCode);
        clock.UtcNow = Now.AddDays(1).AddMilliseconds(-1);
        using (var future = await second.GetAsync("/api/v1/incidents?cursor=" + sampleCursor, ct))
            await AssertReadProblem(future, "invalid-cursor", HttpStatusCode.BadRequest, ct);
        clock.UtcNow = Now.AddDays(2).AddMilliseconds(-1);
        using (var unexpired = await second.GetAsync("/api/v1/incidents?cursor=" + sampleCursor, ct)) Assert.Equal(HttpStatusCode.OK, unexpired.StatusCode);
        clock.UtcNow = Now.AddDays(2);
        using (var expired = await second.GetAsync("/api/v1/incidents?cursor=" + sampleCursor, ct))
            await AssertReadProblem(expired, "invalid-cursor", HttpStatusCode.BadRequest, ct);
        Assert.Equal(before, await SnapshotAsync(secondHost, ct));
    }

    private sealed class ReadClock(DateTimeOffset now) : EePulse.Application.Time.IUtcClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task LifecycleUnionAndCommentReadsUseProtectedTotalCursorOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var clock = new ReadClock(Now);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<EePulse.Application.Time.IUtcClock>();
            services.AddSingleton<EePulse.Application.Time.IUtcClock>(clock);
        }, observer);
        var seed = await SeedAsync(factory, ct);
        var commentIds = new List<(Guid Id, DateTimeOffset At)>();
        foreach (var index in Enumerable.Range(0, 3))
        {
            clock.UtcNow = index == 2 ? Now.AddSeconds(-1) : Now;
            commentIds.Add(await AddCommentAsync(factory, seed, Key(201 + index), "comment-" + index, ct));
        }
        var actionIds = new List<Guid>();
        foreach (var action in new[] { ("acknowledgement", "operator-acknowledgement"), ("manual_resolution", "manual-resolution") })
        {
            clock.UtcNow = Now.AddSeconds(actionIds.Count);
            actionIds.Add(await AddLifecycleActionAsync(factory, seed, Key(211 + actionIds.Count), action.Item1, action.Item2, ct));
        }
        var engineEventId = await AddEngineLifecycleEventAsync(factory, seed, Now, ct);
        var baseline = await SnapshotAsync(factory, ct);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-EE-Pulse-Role", "Viewer");

        foreach (var sort in new[] { "CreatedAtDesc", "CreatedAtAsc" })
        {
            var actual = await ReadAllAsync(client, $"/api/v1/incidents/{seed.IncidentId:D}/comments", sort, "id", ct);
            var expected = (sort == "CreatedAtDesc" ? commentIds.OrderByDescending(value => value.At) : commentIds.OrderBy(value => value.At))
                .ThenByDescending(value => value.Id.ToString("D"), StringComparer.Ordinal).Select(value => value.Id.ToString("D")).ToArray();
            Assert.Equal(expected, actual);
        }
        var defaultCommentOrder = await ReadAllAsync(client, $"/api/v1/incidents/{seed.IncidentId:D}/comments", null, "id", ct);
        Assert.Equal(commentIds.OrderByDescending(value => value.At).ThenByDescending(value => value.Id.ToString("D"), StringComparer.Ordinal)
            .Select(value => value.Id.ToString("D")), defaultCommentOrder);
        using (var page = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}/comments?pageSize=1&sort=CreatedAtDesc", ct))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var json = JsonDocument.Parse(await page.Content.ReadAsStringAsync(ct));
            var item = json.RootElement.GetProperty("items")[0];
            Assert.True(Guid.TryParseExact(item.GetProperty("authorId").GetString(), "D", out var authorId));
            Assert.NotEqual(Guid.Empty, authorId);
            Assert.StartsWith("comment-", item.GetProperty("comment").GetString(), StringComparison.Ordinal);
            var cursor = json.RootElement.GetProperty("nextCursor").GetString()!;
            using var mismatch = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}/comments?sort=CreatedAtAsc&cursor={cursor}", ct);
            await AssertReadProblem(mismatch, "cursor-filter-mismatch", HttpStatusCode.BadRequest, ct);
        }
        foreach (var query in new[] { "sort=createdatdesc", "sort=CreatedAtDesc&sort=CreatedAtAsc", "sort=Unknown", "sort=", "unknown=x" })
        {
            using var invalid = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}/comments?{query}", ct);
            await AssertReadProblem(invalid, "invalid-incident-query", HttpStatusCode.BadRequest, ct);
        }

        observer.Clear();
        var lifecycle = await ReadAllAsync(client, $"/api/v1/incidents/{seed.IncidentId:D}/lifecycle-events", "OccurredAtDesc", "eventId", ct);
        Assert.Equal(new[] { actionIds[1], actionIds[0], engineEventId }.Select(value => value.ToString("D")), lifecycle);
        var lifecycleAscending = await ReadAllAsync(client, $"/api/v1/incidents/{seed.IncidentId:D}/lifecycle-events", "OccurredAtAsc", "eventId", ct);
        Assert.Equal(new[] { actionIds[0], engineEventId, actionIds[1] }.Select(value => value.ToString("D")), lifecycleAscending);
        Assert.Contains(observer.Commands, command => command.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase));
        using (var all = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}/lifecycle-events?pageSize=100&sort=OccurredAtAsc", ct))
        {
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            using var json = JsonDocument.Parse(await all.Content.ReadAsStringAsync(ct));
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var acknowledgement = items.Single(item => item.GetProperty("eventId").GetString() == actionIds[0].ToString("D"));
            Assert.Equal("acknowledged", acknowledgement.GetProperty("type").GetString());
            Assert.Equal("operator-acknowledgement", acknowledgement.GetProperty("reasonCode").GetString());
            Assert.True(Guid.TryParseExact(acknowledgement.GetProperty("actorId").GetString(), "D", out _));
            Assert.Equal("action-note", acknowledgement.GetProperty("comment").GetString());
            var resolution = items.Single(item => item.GetProperty("eventId").GetString() == actionIds[1].ToString("D"));
            Assert.Equal("manually-resolved", resolution.GetProperty("type").GetString());
            Assert.Equal("manual-resolution", resolution.GetProperty("reasonCode").GetString());
            Assert.True(Guid.TryParseExact(resolution.GetProperty("actorId").GetString(), "D", out _));
            Assert.Equal("action-note", resolution.GetProperty("comment").GetString());
            var engine = items.Single(item => item.GetProperty("eventId").GetString() == engineEventId.ToString("D"));
            Assert.Equal("opened", engine.GetProperty("type").GetString());
            Assert.Equal("failure-threshold-met", engine.GetProperty("reasonCode").GetString());
            Assert.Equal(JsonValueKind.Null, engine.GetProperty("actorId").ValueKind);
            Assert.Equal(JsonValueKind.Null, engine.GetProperty("comment").ValueKind);
        }
        using (var asc = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}/lifecycle-events?pageSize=1&sort=OccurredAtAsc", ct))
        {
            Assert.Equal(HttpStatusCode.OK, asc.StatusCode);
            using var json = JsonDocument.Parse(await asc.Content.ReadAsStringAsync(ct));
            Assert.Equal("acknowledged", json.RootElement.GetProperty("items")[0].GetProperty("type").GetString());
            Assert.Equal("operator-acknowledgement", json.RootElement.GetProperty("items")[0].GetProperty("reasonCode").GetString());
            Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("items")[0].GetProperty("actorId").ValueKind);
        }
        foreach (var query in new[] { "sort=occurredatdesc", "sort=OccurredAtDesc&sort=OccurredAtAsc", "sort=Unknown", "sort=", "unknown=x" })
        {
            using var invalid = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}/lifecycle-events?{query}", ct);
            await AssertReadProblem(invalid, "invalid-incident-query", HttpStatusCode.BadRequest, ct);
        }
        foreach (var path in new[] { $"/api/v1/incidents/{Guid.NewGuid():D}/comments", $"/api/v1/incidents/{Guid.NewGuid():D}/lifecycle-events" })
        {
            using var missing = await client.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        Assert.Equal(baseline, await SnapshotAsync(factory, ct));
    }

    private static async Task<string[]> ReadAllAsync(HttpClient client, string route, string? sort, string idProperty, CancellationToken ct)
    {
        var values = new List<string>();
        string? cursor = null;
        do
        {
            using var response = await client.GetAsync(route + "?pageSize=1" + (sort is null ? string.Empty : "&sort=" + sort) +
                (cursor is null ? string.Empty : "&cursor=" + cursor), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            values.AddRange(json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(idProperty).GetString()!));
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        return values.ToArray();
    }

    [Fact]
    public async Task IncidentReadHeadersAndQueriesAreValidatedBeforeDatabaseCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        var observer = new RuntimeCommandObserver();
        await using var factory = new WebApplicationFactory<Program>()
            .WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing()).WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IStartupFilter, EmptyHeaderFilter>();
                    services.AddDbContext<EePulseDbContext>(options =>
                        options.UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Timeout=1").AddInterceptors(observer));
                }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
        var paths = new[] { "/api/v1/incidents", $"/api/v1/incidents/{Guid.NewGuid():D}", $"/api/v1/devices/{Guid.NewGuid():D}/incidents" };
        foreach (var path in paths)
        {
            foreach (var role in new[] { "Viewer", "Operator", "Engineer", "Administrator", "Auditor" })
            {
                foreach (var ifMatch in new[] { "", "bad", "\"tag\"" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    request.Headers.Add("X-EE-Pulse-Role", role);
                    request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                    if (ifMatch.Length == 0) request.Headers.Add("X-Test-Empty-Header", "If-Match");
                    request.Headers.TryAddWithoutValidation("If-None-Match", MalformedConditionalValues);
                    using var response = await client.SendAsync(request, ct);
                    await AssertReadProblem(response, "if-match-unsupported", HttpStatusCode.BadRequest, ct);
                }
            }
            foreach (var role in new[] { "", "UnrecognizedRole" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path + "?unknown=bad");
                if (role.Length != 0) request.Headers.Add("X-EE-Pulse-Role", role);
                request.Headers.TryAddWithoutValidation("If-Match", "bad");
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(role.Length == 0 ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            }
            using var invalidQuery = new HttpRequestMessage(HttpMethod.Get, path + "?unknown=bad");
            invalidQuery.Headers.Add("X-EE-Pulse-Role", "Viewer");
            invalidQuery.Headers.TryAddWithoutValidation("If-Match", "bad");
            using var invalidResponse = await client.SendAsync(invalidQuery, ct);
            await AssertReadProblem(invalidResponse, "invalid-incident-query", HttpStatusCode.BadRequest, ct);
        }
        foreach (var value in new[] { "bad", "", ",\"tag\"", "\"tag\",", "*,\"tag\"" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, paths[1]);
            request.Headers.Add("X-EE-Pulse-Role", "Viewer");
            request.Headers.TryAddWithoutValidation("If-None-Match", value);
            request.Headers.Add("X-Test-Raw-Conditional", "raw:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)));
            if (value.Length == 0) request.Headers.Add("X-Test-Empty-Header", "If-None-Match");
            using var response = await client.SendAsync(request, ct);
            await AssertReadProblem(response, "invalid-if-none-match", HttpStatusCode.BadRequest, ct);
        }
        foreach (var pair in new[] { ("/api/v1/incidents/INVALID", "invalid-incident-id"), ("/api/v1/devices/INVALID/incidents", "invalid-device-id") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pair.Item1);
            request.Headers.Add("X-EE-Pulse-Role", "Viewer");
            request.Headers.TryAddWithoutValidation("If-Match", "bad");
            using var response = await client.SendAsync(request, ct);
            await AssertReadProblem(response, pair.Item2, HttpStatusCode.BadRequest, ct);
        }
        Assert.Empty(observer.Commands);
    }

    [Fact]
    public async Task LifecycleAndCommentReadsEnforceRolesAndRejectEarlyInputsWithoutDatabaseCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, null, observer);
        var seed = await SeedAsync(factory, ct);
        var baseline = await SnapshotAsync(factory, ct);
        using var client = factory.CreateClient();
        foreach (var suffix in new[] { "lifecycle-events", "comments" })
        {
            var path = $"/api/v1/incidents/{seed.IncidentId:D}/{suffix}";
            async Task AssertNoDatabaseAsync(string target, string? role, HttpStatusCode status, string? code)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                if (role is not null)
                {
                    request.Headers.Add("X-EE-Pulse-Role", role);
                    request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
                }
                observer.Clear();
                using var response = await client.SendAsync(request, ct);
                if (code is null) Assert.Equal(status, response.StatusCode);
                else await AssertReadProblem(response, code, status, ct);
                Assert.Empty(observer.Commands);
                Assert.Equal(baseline, await SnapshotAsync(factory, ct));
            }

            await AssertNoDatabaseAsync(path + "?unknown=1", null, HttpStatusCode.Unauthorized, null);
            await AssertNoDatabaseAsync(path + "?unknown=1", "UnrecognizedRole", HttpStatusCode.Forbidden, null);
            await AssertNoDatabaseAsync(path + "?unknown=1", "Viewer", HttpStatusCode.BadRequest, "invalid-incident-query");
            await AssertNoDatabaseAsync($"/api/v1/incidents/INVALID/{suffix}", "Viewer", HttpStatusCode.BadRequest, "invalid-incident-id");
            await AssertNoDatabaseAsync(path + "?cursor=!", "Viewer", HttpStatusCode.BadRequest, "invalid-cursor");

            foreach (var role in new[] { "Viewer", "Operator", "Engineer", "Administrator", "Auditor" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("X-EE-Pulse-Role", role);
                request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Null(response.Headers.ETag);
                Assert.Equal(baseline, await SnapshotAsync(factory, ct));
            }
        }
    }

    [Fact]
    public async Task IncidentReadPagesDetailAndConditionalReadsPreserveDatabaseSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        Guid deviceId;
        Guid secondId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            deviceId = (await db.Probes.SingleAsync(probe => probe.Id == seed.ProbeId, ct)).DeviceId;
            var resolved = new AvailabilityIncident(secondId, seed.ProbeId, Now.AddDays(-1));
            resolved.ResolveForConfirmedRecovery(Now.AddDays(-1).AddSeconds(3.9));
            db.Add(resolved);
            await db.SaveChangesAsync(ct);
        }
        var snapshot = await SnapshotAsync(factory, ct);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-EE-Pulse-Role", "Viewer");
        foreach (var path in new[] { "/api/v1/incidents", $"/api/v1/devices/{deviceId:D}/incidents" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path + "?pageSize=1");
            request.Headers.TryAddWithoutValidation("If-None-Match", MalformedConditionalValues);
            using var first = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Null(first.Headers.ETag);
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct));
            Assert.Equal(seed.IncidentId.ToString("D"), firstJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
            var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
            using var second = await client.GetAsync(path + "?pageSize=1&cursor=" + cursor, ct);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync(ct));
            Assert.Equal(secondId.ToString("D"), secondJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.Equal(3, secondJson.RootElement.GetProperty("items")[0].GetProperty("totalDowntimeSeconds").GetInt64());
            Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("nextCursor").ValueKind);
            using var mismatch = await client.GetAsync(path + "?status=Resolved&cursor=" + cursor, ct);
            await AssertReadProblem(mismatch, "cursor-filter-mismatch", HttpStatusCode.BadRequest, ct);
            using var invalid = await client.GetAsync(path + "?cursor=!", ct);
            await AssertReadProblem(invalid, "invalid-cursor", HttpStatusCode.BadRequest, ct);
        }
        using var detail = await client.GetAsync($"/api/v1/incidents/{seed.IncidentId:D}", ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.NotNull(detail.Headers.ETag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/incidents/{seed.IncidentId:D}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "W/" + detail.Headers.ETag);
        using var notModified = await client.SendAsync(conditional, ct);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(detail.Headers.ETag, notModified.Headers.ETag);
        Assert.Equal("private, max-age=0, must-revalidate", notModified.Headers.NonValidated["Cache-Control"].ToString());
        foreach (var role in new[] { "Viewer", "Operator", "Engineer", "Administrator", "Auditor" })
        {
            foreach (var path in new[] { "/api/v1/incidents", $"/api/v1/incidents/{seed.IncidentId:D}", $"/api/v1/devices/{deviceId:D}/incidents" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("X-EE-Pulse-Role", role);
                request.Headers.Add("X-EE-Pulse-Actor", Guid.NewGuid().ToString("D"));
                request.Headers.Add("X-Correlation-ID", "caller-untrusted-value");
                using var success = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, success.StatusCode);
                Assert.NotEqual("caller-untrusted-value", success.Headers.GetValues("X-Correlation-ID").Single());
            }
        }
        foreach (var path in new[] { $"/api/v1/incidents/{Guid.NewGuid():D}", $"/api/v1/devices/{Guid.NewGuid():D}/incidents" })
        {
            using var missing = await client.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        foreach (var filter in new[] { "status=Resolved", "probeId=" + seed.ProbeId.ToString("D") + "&status=Resolved",
            "deviceId=" + deviceId.ToString("D") + "&status=Resolved", "openedFrom=" + Now.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) +
            "&openedTo=" + Now.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) })
        {
            using var response = await client.GetAsync("/api/v1/incidents?" + filter, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(secondId.ToString("D"), Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        }
        using (var empty = await client.GetAsync("/api/v1/incidents?siteId=" + Guid.NewGuid().ToString("D"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var json = JsonDocument.Parse(await empty.Content.ReadAsStringAsync(ct));
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nextCursor").ValueKind);
        }
        Assert.Equal(snapshot, await SnapshotAsync(factory, ct));
    }

    private static async Task AssertReadProblem(HttpResponseMessage response, string code, HttpStatusCode status, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.True(Guid.TryParseExact(correlation, "N", out _));
        Assert.Equal(correlation, body.RootElement.GetProperty("correlationId").GetString());
        Assert.False(body.RootElement.TryGetProperty("traceId", out _));
    }

    private static async Task AssertCommandProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.True(Guid.TryParseExact(correlation, "N", out _));
        Assert.Equal(correlation, body.RootElement.GetProperty("correlationId").GetString());
    }

    private sealed class EmptyHeaderFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                // HttpClient removes empty conditional values before TestServer receives them.
                var name = context.Request.Headers["X-Test-Empty-Header"].ToString();
                if (name is "If-Match" or "If-None-Match" or "Idempotency-Key") context.Request.Headers[name] = string.Empty;
                var raw = context.Request.Headers["X-Test-Raw-Conditional"].ToString();
                // HttpClient also normalizes empty list members in known header types.
                if (raw.StartsWith("raw:", StringComparison.Ordinal))
                    context.Request.Headers.IfNoneMatch = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw[4..]));
                await continuation(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task MissingAuthoritativeIncidentReturnsNotFoundAndRollsBackGeneratedPrincipal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
            builder.UseEnvironment("Development");
        });

        var identity = new PrincipalIdentity("https://issuer.example", "operator-1");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>();
            var input = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, Guid.NewGuid(),
                "01234567-89ab-cdef-0123-456789abcdef", identity, "\"strong\"", "{}"u8,
                "0123456789abcdef0123456789abcdef", "comment");

            var result = await coordinator.ExecuteAsync(input, new ThrowingHandler(), ct);
            Assert.IsType<IncidentCommandResult<string>.NotFound>(result);
        }

        await using var verification = factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.False(await db.HumanPrincipals.AsNoTracking().AnyAsync(x =>
            x.Issuer == identity.Issuer && x.Subject == identity.Subject, ct));
    }

    [Fact]
    public async Task FreshSuccessUsesLockOrderAndReceiptLastTwoFlushes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, null, observer);
        var seed = await SeedAsync(factory, ct);
        observer.Clear();
        var etag = new IncidentEtagV1().Create(seed.IncidentId, 1);
        var input = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId,
            "01234567-89ab-cdef-0123-456789abcdef", seed.Identity, etag, "{\"Comment\":\"x\"}"u8,
            "0123456789abcdef0123456789abcdef", "x");
        var handler = new CommentHandler();

        StoredIncidentHttpResponse fresh;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>()
                .ExecuteAsync(input, handler, ct);
            fresh = Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(result).Response;
        }

        Assert.Equal(1, handler.ApplyCount);
        Assert.Equal(1, handler.ResponseCount);
        await using (var verification = factory.Services.CreateAsyncScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var receipt = await db.IdempotencyReceipts.AsNoTracking().SingleAsync(x => x.IdempotencyKey == input.IdempotencyKey, ct);
            Assert.Equal(fresh.StatusCode, receipt.ResponseStatus);
            Assert.Equal(fresh.BodyUtf8.ToArray(), receipt.ResponseBody);
            Assert.Equal(fresh.ContentType, receipt.ResponseContentType);
            Assert.Equal(fresh.Etag, receipt.ResponseEtag);
            var principal = await db.HumanPrincipals.AsNoTracking().SingleAsync(ct);
            Assert.Equal(principal.Id, receipt.ActorId);
            Assert.Equal(1, await db.IncidentComments.CountAsync(ct));
            Assert.Equal(1, await db.AuditEvents.CountAsync(ct));
        }

        AssertLockAndReceiptOrder(observer.Commands);
    }

    [Fact]
    public async Task ExactReplayReturnsStoredRepresentationWithoutHandlerOrMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(1), new IncidentEtagV1().Create(seed.IncidentId, 1));
        var handler = new CommentHandler();
        StoredIncidentHttpResponse fresh;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>().ExecuteAsync(input, handler, ct);
            fresh = Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(result).Response;
        }
        await using (var replayScope = factory.Services.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>()
                .ExecuteAsync(input, handler, ct);
            var replayResponse = Assert.IsType<IncidentCommandResult<string>.Replay>(replay).Response;
            Assert.Equal(fresh.StatusCode, replayResponse.StatusCode);
            Assert.Equal(fresh.BodyUtf8.ToArray(), replayResponse.BodyUtf8.ToArray());
            Assert.Equal(fresh.ContentType, replayResponse.ContentType);
            Assert.Equal(fresh.Etag, replayResponse.Etag);
        }
        Assert.Equal(1, handler.ApplyCount);
        Assert.Equal(1, handler.ResponseCount);
    }

    [Fact]
    public async Task SameKeyDifferentFingerprintReturnsReuseConflictWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var etag = new IncidentEtagV1().Create(seed.IncidentId, 1);
        var input = CreateInput(seed, Key(2), etag);
        await ExecuteAsync(factory, input, new CommentHandler(), ct);
        var conflicting = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId, Key(2),
            seed.Identity, etag, "{\"Comment\":\"different\"}"u8, "0123456789abcdef0123456789abcdef", "different");
        var handler = new ThrowingHandler();
        var result = await ExecuteAsync(factory, conflicting, handler, ct);
        Assert.IsType<IncidentCommandResult<string>.IdempotencyReuseConflict>(result);
        await AssertCountsAsync(factory, 1, 1, 1, ct);
    }

    [Fact]
    public async Task UnsupportedStoredFingerprintVersionReturnsDependencyFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(3), new IncidentEtagV1().Create(seed.IncidentId, 1));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var actorId = Guid.NewGuid();
            var commentId = Guid.NewGuid();
            var storedAt = Now.AddMinutes(1);
            var digest = new byte[32];
            var responseBody = "{\"stored\":true}"u8.ToArray();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL DEFERRED", ct);

            // Fixture-only: production accepts only v1. This disposable database is destroyed
            // after the test, so retained unsupported-version replay dispatch can be exercised.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE idempotency_receipts DROP CONSTRAINT ck_idempotency_receipts_request_fingerprint_version", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO human_principals (id, issuer, subject, created_at) VALUES ({actorId}, {seed.Identity.Issuer}, {seed.Identity.Subject}, {storedAt})", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO incident_comments (id, incident_id, probe_id, author_id, comment, created_at, idempotency_key) VALUES ({commentId}, {seed.IncidentId}, {seed.ProbeId}, {actorId}, {"x"}, {storedAt}, {input.IdempotencyKey})", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO idempotency_receipts (idempotency_key, route_template, incident_id, actor_id, request_fingerprint_version, request_digest, response_status, response_body, response_content_type, response_etag, outcome_kind, incident_comment_id, incident_lifecycle_action_id, created_at, completed_at) VALUES ({input.IdempotencyKey}, {"/api/v1/incidents/{id}/comments"}, {seed.IncidentId}, {actorId}, {2}, {digest}, {201}, {responseBody}, {"application/json; charset=utf-8"}, {"\"stored\""}, {"general_comment"}, {commentId}, {null}, {storedAt}, {storedAt})", ct);
            await transaction.CommitAsync(ct);
        }
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(factory, input, new ThrowingHandler(), ct));
        await AssertCountsAsync(factory, 1, 0, 1, ct);

        var differentActor = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId,
            input.IdempotencyKey, new PrincipalIdentity("https://issuer.example", "operator-2"), input.ParsedStrongIfMatch,
            input.CanonicalRequestBodyUtf8.AsSpan(), input.TrustedCorrelationId, input.Command);
        Assert.IsType<IncidentCommandResult<string>.IdempotencyReuseConflict>(
            await ExecuteAsync(factory, differentActor, new ThrowingHandler(), ct));
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(ct);
        await using var trigger = new NpgsqlCommand("SELECT COUNT(*) FROM pg_trigger WHERE tgname = 'tr_idempotency_receipts_immutable' AND tgenabled = 'O'", connection);
        Assert.Equal(1L, (long)(await trigger.ExecuteScalarAsync(ct))!);
        await using var function = new NpgsqlCommand("SELECT COUNT(*) FROM pg_proc WHERE proname = 'fn_wp07_incident_runtime_reject_mutation'", connection);
        Assert.Equal(1L, (long)(await function.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task MissingReceiptPrincipalMappingReturnsDependencyFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(4), new IncidentEtagV1().Create(seed.IncidentId, 1));
        await ExecuteAsync(factory, input, new CommentHandler(), ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE idempotency_receipts DISABLE TRIGGER ALL", ct);
            try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE idempotency_receipts SET actor_id = {Guid.NewGuid()} WHERE idempotency_key = {input.IdempotencyKey}", ct); }
            finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE idempotency_receipts ENABLE TRIGGER ALL", ct); }
        }
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(factory, input, new ThrowingHandler(), ct));
    }

    [Fact]
    public async Task MissingProjectionReturnsDependencyFailureWithoutSurvivingPrincipal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_status_projections WHERE probe_id = {seed.ProbeId}", ct);
        }
        var input = CreateInput(seed, Key(5), new IncidentEtagV1().Create(seed.IncidentId, 1));
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(factory, input, new ThrowingHandler(), ct));
        await AssertCountsAsync(factory, 0, 0, 0, ct);
    }

    [Fact]
    public async Task StaleEtagWinsBeforeHandlerAndStateConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(6), "\"stale\""), new ThrowingHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.FreshPreconditionFailed>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
    }

    [Fact]
    public async Task CurrentEtagStateConflictRollsBackStagedWork()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(7), new IncidentEtagV1().Create(seed.IncidentId, 1)), new StateConflictHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.FreshStateConflict>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
    }

    [Fact]
    public async Task ExistingPrincipalIsRereadWithoutChangingImmutableMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var existingId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            db.HumanPrincipals.Add(new HumanPrincipal(existingId, seed.Identity.Issuer, seed.Identity.Subject, Now));
            await db.SaveChangesAsync(ct);
        }
        var input = CreateInput(seed, Key(8), new IncidentEtagV1().Create(seed.IncidentId, 1));
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await ExecuteAsync(factory, input, new CommentHandler(), ct));
        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(existingId, (await verificationDb.IdempotencyReceipts.AsNoTracking().SingleAsync(ct)).ActorId);
        Assert.Equal(1, await verificationDb.HumanPrincipals.CountAsync(ct));
    }

    [Fact]
    public async Task ConcurrentSameKeyExecutesOnceAndLoserReplays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(9), new IncidentEtagV1().Create(seed.IncidentId, 1));
        var winner = new BlockingCommentHandler();
        var first = ExecuteAsync(factory, input, winner, ct);
        await winner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var second = ExecuteAsync(factory, input, new CommentHandler(), ct);
        await Task.Delay(150, ct);
        Assert.False(second.IsCompleted);
        winner.Release.TrySetResult();
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await first);
        Assert.IsType<IncidentCommandResult<string>.Replay>(await second);
        await AssertCountsAsync(factory, 1, 1, 1, ct);
    }

    [Fact]
    public async Task ForcedAdvisoryCollisionSerializesButKeepsFullKeysDistinct()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<IIncidentIdempotencyAdvisoryKeyProvider>();
            services.AddSingleton<IIncidentIdempotencyAdvisoryKeyProvider, CollidingAdvisoryKeyProvider>();
        });
        var firstSeed = await SeedAsync(factory, ct);
        var secondSeed = await SeedAsync(factory, ct);
        var firstEtag = new IncidentEtagV1().Create(firstSeed.IncidentId, 1);
        var secondEtag = new IncidentEtagV1().Create(secondSeed.IncidentId, 1);
        var winner = new BlockingCommentHandler();
        var loser = new CommentHandler();
        var first = ExecuteAsync(factory, CreateInput(firstSeed, Key(10), firstEtag), winner, ct);
        await winner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var second = ExecuteAsync(factory, CreateInput(secondSeed, Key(11), secondEtag), loser, ct);
        await WaitForAdvisoryWaitAsync(postgres.ConnectionString, IncidentIdempotencyAdvisoryKeyDeriver.NamespaceKey, 7, ct);
        Assert.Equal(0, loser.ApplyCount);
        winner.Release.TrySetResult();
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await first);
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await second);
        await AssertCountsAsync(factory, 2, 2, 2, ct);
        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(2, await verificationDb.IdempotencyReceipts.AsNoTracking()
            .CountAsync(receipt => receipt.IdempotencyKey == Key(10) || receipt.IdempotencyKey == Key(11), ct));
    }

    [Fact]
    public async Task FailureAfterFirstFlushRollsBackPrincipalChildAuditAndReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var before = await SnapshotAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(12), new IncidentEtagV1().Create(seed.IncidentId, 1)), new ResponseFailureHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
        Assert.Equal(before, await SnapshotAsync(factory, ct));
        await AssertTransactionBoundaryClassificationAsync(ct);
    }

    [Fact]
    public async Task ServerRejectedCommitIsDeterminateAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, null, observer);
        var seed = await SeedAsync(factory, ct);
        var before = await SnapshotAsync(factory, ct);
        await InstallDeferredCommitRejectionTriggerAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(13), new IncidentEtagV1().Create(seed.IncidentId, 1)), new CommentHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
        Assert.Equal(before, await SnapshotAsync(factory, ct));
        Assert.Contains(observer.Commands, command => command.Contains("incident_comments", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
        Assert.Contains(observer.Commands, command => command.Contains("idempotency_receipts", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousCommitNeverReturnsSuccessAndRetryReplaysDurableReceipt()
    {
        await AssertIndeterminateCommitCaseAsync(false, Key(14));
        await AssertIndeterminateCommitCaseAsync(true, Key(15));
    }

    private static async Task AssertTransactionBoundaryClassificationAsync(CancellationToken ct)
    {
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var identity = new PrincipalIdentity("https://issuer.example", "operator-1");
        var missingInput = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, Guid.NewGuid(), Key(90), identity,
            "\"strong\"", "{}"u8, "0123456789abcdef0123456789abcdef", "x");

        await using (var beginFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.Begin))))
        {
            Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(
                await ExecuteAsync(beginFactory, missingInput, new ThrowingHandler(), ct));
        }

        using (var callerCancellation = new CancellationTokenSource())
        await using (var callerCancelledFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.BeginCallerCancellation, callerCancellation))))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(callerCancelledFactory,
                missingInput, new ThrowingHandler(), callerCancellation.Token));
        }

        await using (var providerCancelledFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.BeginProviderCancellation))))
        {
            Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(
                await ExecuteAsync(providerCancelledFactory, missingInput, new ThrowingHandler(), ct));
        }

        await using (var rollbackFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.Rollback))))
        {
            var seed = await SeedAsync(rollbackFactory, ct);
            var before = await SnapshotAsync(rollbackFactory, ct);
            Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(rollbackFactory,
                CreateInput(seed, Key(91), new IncidentEtagV1().Create(seed.IncidentId, 1)), new ResponseFailureHandler(), ct));
            Assert.Equal(before, await SnapshotAsync(rollbackFactory, ct));
        }

        await using (var disposalFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.Dispose))))
        {
            var seed = await SeedAsync(disposalFactory, ct);
            Assert.IsType<IncidentCommandResult<string>.FreshPreconditionFailed>(await ExecuteAsync(disposalFactory,
                CreateInput(seed, Key(92), "\"stale\""), new ThrowingHandler(), ct));
        }
    }

    private static async Task AssertIndeterminateCommitCaseAsync(bool cancellation, string idempotencyKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var cleanupToken = CancellationToken.None;
        var transactionBoundary = new RecordingTransactionBoundary();
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<IIncidentCommitBoundary>();
            services.AddSingleton<IIncidentCommitBoundary>(new CommitThenIndeterminateBoundary(cancellation));
            ReplaceTransactionBoundary(services, transactionBoundary);
        });
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, idempotencyKey, new IncidentEtagV1().Create(seed.IncidentId, 1));
        var result = await ExecuteAsync(factory, input, new CommentHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.IndeterminateCommit>(result);
        Assert.Equal(0, transactionBoundary.RollbackCalls);

        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var receipt = await verificationDb.IdempotencyReceipts.AsNoTracking()
            .SingleAsync(value => value.IdempotencyKey == idempotencyKey, cleanupToken);
        var replay = Assert.IsType<IncidentCommandResult<string>.Replay>(
            await ExecuteAsync(factory, input, new ThrowingHandler(), cleanupToken));
        Assert.Equal(receipt.ResponseStatus, replay.Response.StatusCode);
        Assert.Equal(receipt.ResponseBody, replay.Response.BodyUtf8.ToArray());
        Assert.Equal(receipt.ResponseContentType, replay.Response.ContentType);
        Assert.Equal(receipt.ResponseEtag, replay.Response.Etag);
        await AssertCountsAsync(factory, 1, 1, 1, cleanupToken);
    }

    private static async Task InstallDeferredCommitRejectionTriggerAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION public.wp07_test_reject_receipt_commit()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'wp07-test-deferred-commit-rejection' USING ERRCODE = 'P0001';
            END;
            $$;
            CREATE CONSTRAINT TRIGGER tr_wp07_test_reject_receipt_commit
            AFTER INSERT ON idempotency_receipts
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION public.wp07_test_reject_receipt_commit();
            """, ct);
    }

    private static async Task WaitForAdvisoryWaitAsync(string connectionString, int namespaceKey, int derivedKey, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT COUNT(*)
                FROM pg_locks locks
                JOIN pg_stat_activity activity ON activity.pid = locks.pid
                WHERE locks.locktype = 'advisory'
                  AND locks.classid = @namespace_key::oid
                  AND locks.objid = @derived_key::oid
                  AND NOT locks.granted
                  AND activity.wait_event_type = 'Lock'
                """, connection);
            command.Parameters.AddWithValue("namespace_key", namespaceKey);
            command.Parameters.AddWithValue("derived_key", derivedKey);
            if ((long)(await command.ExecuteScalarAsync(ct))! > 0) return;
            await Task.Delay(25, ct);
        }

        throw new Xunit.Sdk.XunitException("The losing request did not wait on the forced idempotency advisory lock.");
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var incident = await db.AvailabilityIncidents.AsNoTracking().OrderBy(value => value.Id).Select(value => new
        {
            value.Id, value.ProbeId, value.Status, value.OpenedAt, value.AcknowledgedAt, value.AcknowledgedBy,
            value.AcknowledgementComment, value.ResolvedAt, value.ResolvedBy, value.ResolutionNote, value.OccurrenceCount, value.RowVersion
        }).ToListAsync(ct);
        var projection = await db.ProbeStatusProjections.AsNoTracking().OrderBy(value => value.ProbeId).Select(value => new
        {
            value.ProbeId, value.UnderlyingStatus, value.VisibleStatus, value.ConsecutiveFailureCount, value.ConsecutiveSuccessCount,
            value.OpenIncidentId, value.StateVersion, value.WatermarkAgentId, value.WatermarkResultId, value.WatermarkEventAt,
            value.LastFreshEventAt
        }).ToListAsync(ct);
        var principal = await db.HumanPrincipals.AsNoTracking().OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.Issuer, value.Subject, value.CreatedAt }).ToListAsync(ct);
        var comments = await db.IncidentComments.AsNoTracking().OrderBy(value => value.Id).Select(value => new
        {
            value.Id, value.IncidentId, value.ProbeId, value.AuthorId, value.Comment, value.CreatedAt, value.IdempotencyKey
        }).ToListAsync(ct);
        var actions = await db.IncidentLifecycleActions.AsNoTracking().OrderBy(value => value.EventId).Select(value => new
        {
            value.EventId, value.IncidentId, value.ProbeId, value.ActorId, value.ActionKind, value.ReasonCode, value.OccurredAt, value.ActionNote, value.IdempotencyKey
        }).ToListAsync(ct);
        var audits = await db.AuditEvents.AsNoTracking().OrderBy(value => value.Id).Select(value => new
        {
            value.Id, value.ActorId, value.Action, value.EntityType, value.EntityId, value.CorrelationId, value.OccurredAt
        }).ToListAsync(ct);
        var receipts = await db.IdempotencyReceipts.AsNoTracking().OrderBy(value => value.IdempotencyKey).Select(value => new
        {
            value.IdempotencyKey, value.IncidentId, value.ActorId, value.RequestFingerprintVersion, value.RequestDigest,
            value.ResponseStatus, value.ResponseBody, value.ResponseContentType, value.ResponseEtag, value.OutcomeKind,
            value.IncidentCommentId, value.IncidentLifecycleActionId, value.CreatedAt, value.CompletedAt
        }).ToListAsync(ct);
        return System.Text.Json.JsonSerializer.Serialize(new { incident, projection, principal, comments, actions, audits, receipts });
    }

    private static void ReplaceTransactionBoundary(IServiceCollection services, IIncidentTransactionBoundary boundary)
    {
        services.RemoveAll<IIncidentTransactionBoundary>();
        services.AddSingleton(boundary);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString,
        Action<IServiceCollection>? configure = null, params DbCommandInterceptor[] interceptors) =>
        new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                if (interceptors.Length > 0)
                    services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
                configure?.Invoke(services);
            });
        });

    private static WebApplicationFactory<Program> CreateMissingPostgresFactory(RuntimeCommandObserver observer) =>
        new WebApplicationFactory<Program>().WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", string.Empty);
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services => services.AddDbContext<EePulseDbContext>(options =>
                options.UseNpgsql("Host=127.0.0.1;Port=1;Database=unconfigured;Username=unconfigured;Timeout=1")
                    .AddInterceptors(observer)));
        });

    private static IncidentCommandInput<string> CreateInput((Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity) seed,
        string idempotencyKey, string etag) =>
        new(IncidentCommandRoute.AddComment, seed.IncidentId, idempotencyKey, seed.Identity, etag,
            "{\"Comment\":\"x\"}"u8, "0123456789abcdef0123456789abcdef", "x");

    private static string Key(int value) => "00000000-0000-0000-0000-" + value.ToString("D12", CultureInfo.InvariantCulture);

    // This deliberately does not call the production fingerprint encoder.  It frames
    // the six frozen fields directly, so receipt verification detects an encoder change.
    private static byte[] IndependentFingerprint(IncidentCommandRoute route, Guid incidentId, Guid actorId, string ifMatch, byte[] body)
    {
        var routeTemplate = route switch
        {
            IncidentCommandRoute.Acknowledge => "/api/v1/incidents/{id}/acknowledge",
            IncidentCommandRoute.AddComment => "/api/v1/incidents/{id}/comments",
            IncidentCommandRoute.Resolve => "/api/v1/incidents/{id}/resolve",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
        var fields = new[]
        {
            "POST"u8.ToArray(), Encoding.ASCII.GetBytes(routeTemplate), Encoding.ASCII.GetBytes(incidentId.ToString("D")),
            Encoding.ASCII.GetBytes(actorId.ToString("D")), Encoding.ASCII.GetBytes(ifMatch), body,
        };
        using var stream = new MemoryStream();
        stream.Write("EE-PULSE/WP07/REQUEST-FINGERPRINT/V1"u8);
        stream.WriteByte(0);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var field in fields)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)field.Length));
            stream.Write(length);
            stream.Write(field);
        }
        return System.Security.Cryptography.SHA256.HashData(stream.ToArray());
    }

    private sealed class NonSeekableReadStream(byte[] source) : Stream
    {
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int bufferOffset, int count) => Read(buffer.AsSpan(bufferOffset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, source.Length - offset);
            source.AsSpan(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
    }

    private static async Task<IncidentCommandResult<string>> ExecuteAsync(WebApplicationFactory<Program> factory,
        IncidentCommandInput<string> input, IIncidentLockedCommandHandler<string, string> handler, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>().ExecuteAsync(input, handler, ct);
    }

    private static async Task<(Guid Id, DateTimeOffset At)> AddCommentAsync(WebApplicationFactory<Program> factory,
        (Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity) seed, string key, string comment, CancellationToken ct)
    {
        long version;
        await using (var scope = factory.Services.CreateAsyncScope())
            version = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().AvailabilityIncidents
                .Where(incident => incident.Id == seed.IncidentId).Select(incident => incident.RowVersion).SingleAsync(ct);
        var input = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId, key, seed.Identity,
            new IncidentEtagV1().Create(seed.IncidentId, version), "{\"Comment\":\"test\"}"u8,
            "0123456789abcdef0123456789abcdef", comment);
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await ExecuteAsync(factory, input, new CommentHandler(), ct));
        await using var read = factory.Services.CreateAsyncScope();
        var row = await read.ServiceProvider.GetRequiredService<EePulseDbContext>().IncidentComments.AsNoTracking()
            .SingleAsync(value => value.IdempotencyKey == key, ct);
        return (row.Id, row.CreatedAt);
    }

    private static async Task<Guid> AddLifecycleActionAsync(WebApplicationFactory<Program> factory,
        (Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity) seed, string key, string kind, string reason, CancellationToken ct)
    {
        long version;
        await using (var scope = factory.Services.CreateAsyncScope())
            version = await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().AvailabilityIncidents
                .Where(incident => incident.Id == seed.IncidentId).Select(incident => incident.RowVersion).SingleAsync(ct);
        var input = new IncidentCommandInput<string>(IncidentCommandRoute.Acknowledge, seed.IncidentId, key, seed.Identity,
            new IncidentEtagV1().Create(seed.IncidentId, version), "{\"Comment\":\"test\"}"u8,
            "0123456789abcdef0123456789abcdef", "action-note");
        var handler = new LifecycleActionHandler(kind, reason);
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await ExecuteAsync(factory, input, handler, ct));
        return handler.EventId;
    }

    private static async Task<Guid> AddEngineLifecycleEventAsync(WebApplicationFactory<Program> factory,
        (Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity) seed, DateTimeOffset occurredAt, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var probe = await db.Probes.AsNoTracking().SingleAsync(value => value.Id == seed.ProbeId, ct);
        var agent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), probe.AgentGroupId, Guid.NewGuid(), "runtime-read-agent", "1.0", 20, occurredAt);
        var configuration = new EePulse.Domain.Agents.AgentConfigurationSnapshot(probe.AgentGroupId, 1,
            $$"""{"probes":[{"probeId":"{{seed.ProbeId:D}}","intervalSeconds":{{probe.IntervalSeconds}}}]}""", new byte[32], occurredAt, null);
        var acknowledgement = new EePulse.Domain.Agents.AgentConfigurationAcknowledgement(Guid.NewGuid(), agent.Id, 1,
            EePulse.Domain.Agents.AgentAcknowledgementStatus.Applied, occurredAt, occurredAt, occurredAt, null, 1, 1);
        var resultId = Guid.NewGuid();
        var ledger = new EePulse.Domain.Agents.ProbeResultLedgerEntry(agent.Id, resultId, seed.ProbeId, 1, occurredAt.AddSeconds(-1), occurredAt,
            1, 1, 0m, 1m, 1m, 1m, null, new byte[32], occurredAt);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, occurredAt);
        var disposition = new ProbeResultProcessingDisposition(agent.Id, resultId, seed.ProbeId, occurredAt,
            ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, occurredAt);
        var transition = new ProbeResultStatusTransition(agent.Id, resultId, seed.ProbeId, ProbeStatus.Up, ProbeStatus.Down,
            "failure-threshold-met", occurredAt, occurredAt, ProbeResultProcessingDispositionKind.StateDriving);
        var eventId = Guid.NewGuid();
        var lifecycle = new IncidentLifecycleEvent(eventId, seed.IncidentId, seed.ProbeId, agent.Id, resultId,
            ProbeStatus.Up, policy.Id, policy.PolicyVersion, occurredAt);
        db.AddRange(agent, configuration, acknowledgement, ledger, policy, disposition, transition, lifecycle);
        await db.SaveChangesAsync(ct);
        return eventId;
    }

    private static async Task AssertCountsAsync(WebApplicationFactory<Program> factory, int comments, int audits, int receipts, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(comments, await db.IncidentComments.CountAsync(ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(ct));
        Assert.Equal(receipts, await db.IdempotencyReceipts.CountAsync(ct));
    }

    private static void AssertLockAndReceiptOrder(IReadOnlyList<string> commands)
    {
        var list = commands.ToList();
        var idempotency = list.FindIndex(command => command.Contains("pg_advisory_xact_lock", StringComparison.Ordinal));
        var probe = list.FindIndex(command => command.Contains("hashtextextended", StringComparison.Ordinal));
        var projection = list.FindIndex(command => command.Contains("probe_status_projections", StringComparison.Ordinal) && command.Contains("FOR UPDATE", StringComparison.Ordinal));
        var incident = list.FindIndex(command => command.Contains("availability_incidents", StringComparison.Ordinal) && command.Contains("FOR UPDATE", StringComparison.Ordinal));
        var comment = list.FindIndex(command => command.Contains("incident_comments", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
        var receipt = list.FindIndex(command => command.Contains("idempotency_receipts", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
        Assert.True(idempotency >= 0 && probe > idempotency && projection > probe && incident > projection);
        Assert.True(comment >= 0 && receipt > comment);
    }

    private static async Task<(Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity)> SeedAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        var siteId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var probeId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var fixtureSuffix = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        db.Add(new Site(siteId, "SITE-" + fixtureSuffix[..8], "Site " + fixtureSuffix, "UTC", Now));
        db.Add(new AgentGroup(groupId, "Group " + fixtureSuffix, null, Now));
        db.Add(new Device(deviceId, siteId, "Device " + fixtureSuffix, "192.0.2.10", null, "router", null, null, Criticality.Normal, [], Now));
        db.Add(new Probe(probeId, deviceId, groupId, 60, 1_000, 1, null, null, 1, 1));
        await db.SaveChangesAsync(ct);
        db.Add(new AvailabilityIncident(incidentId, probeId, Now));
        db.Add(new ProbeStatusProjection(probeId, ProbeStatus.Up, 0, 0, null, null, null, null));
        await db.SaveChangesAsync(ct);
        return (incidentId, probeId, new PrincipalIdentity("https://issuer.example", "operator-1"));
    }

    private sealed class CommentHandler : IIncidentLockedCommandHandler<string, string>
    {
        public int ApplyCount { get; private set; }
        public int ResponseCount { get; private set; }

        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            ApplyCount++;
            var comment = new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId,
                context.ActorId, context.Command, context.OccurredAt, context.IdempotencyKey);
            context.Stage(comment);
            context.Stage(new AuditEvent(Guid.NewGuid(), context.ActorId, "incident.comment.add", "AvailabilityIncident",
                context.Incident.Id, null, null, context.TrustedCorrelationId, context.OccurredAt, null));
            return ValueTask.FromResult<LockedCommandDecision<string>>(new LockedCommandDecision<string>.Apply(
                new StagedCommandOutcome(comment.Id, "general_comment", context.OccurredAt)));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken)
        {
            ResponseCount++;
            return ValueTask.FromResult(new StoredIncidentHttpResponse(201, "{\"created\":true}"u8,
                "application/json; charset=utf-8", context.ResultingEtag));
        }
    }

    private sealed class LifecycleActionHandler(string kind, string reason) : IIncidentLockedCommandHandler<string, string>
    {
        public Guid EventId { get; private set; }

        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            EventId = Guid.NewGuid();
            context.Stage(new IncidentLifecycleAction(EventId, context.Incident.Id, context.Incident.ProbeId, context.ActorId,
                kind, reason, context.OccurredAt, context.Command, context.IdempotencyKey));
            return ValueTask.FromResult<LockedCommandDecision<string>>(new LockedCommandDecision<string>.Apply(
                new StagedCommandOutcome(EventId, kind, context.OccurredAt)));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StoredIncidentHttpResponse(200, "{\"created\":true}"u8,
                "application/json; charset=utf-8", context.ResultingEtag));
    }

    private sealed class BlockingCommentHandler : IIncidentLockedCommandHandler<string, string>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var comment = new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId,
                context.ActorId, context.Command, context.OccurredAt, context.IdempotencyKey);
            context.Stage(comment);
            context.Stage(new AuditEvent(Guid.NewGuid(), context.ActorId, "incident.comment.add", "AvailabilityIncident",
                context.Incident.Id, null, null, context.TrustedCorrelationId, context.OccurredAt, null));
            return new LockedCommandDecision<string>.Apply(new StagedCommandOutcome(comment.Id, "general_comment", context.OccurredAt));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StoredIncidentHttpResponse(201, "{\"created\":true}"u8,
                "application/json; charset=utf-8", context.ResultingEtag));
    }

    private sealed class StateConflictHandler : IIncidentLockedCommandHandler<string, string>
    {
        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            context.Stage(new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId,
                context.ActorId, context.Command, context.OccurredAt, context.IdempotencyKey));
            return ValueTask.FromResult<LockedCommandDecision<string>>(new LockedCommandDecision<string>.StateConflict("state"));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A state conflict must not construct a response.");
    }

    private sealed class ResponseFailureHandler : IIncidentLockedCommandHandler<string, string>
    {
        private readonly CommentHandler inner = new();

        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken) =>
            inner.ApplyAfterCurrentEtagAcceptedAsync(context, cancellationToken);

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("response construction failure");
    }

    private sealed class CommitThenIndeterminateBoundary(bool cancellation) : IIncidentCommitBoundary
    {
        public async Task CommitAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            if (cancellation) throw new OperationCanceledException(new CancellationToken(canceled: true));
            throw new NpgsqlException("commit acknowledgement lost");
        }
    }

    private enum TransactionFault { Begin, BeginCallerCancellation, BeginProviderCancellation, Rollback, Dispose }

    private sealed class FaultingTransactionBoundary(TransactionFault fault, CancellationTokenSource? callerCancellation = null) : IIncidentTransactionBoundary
    {
        public Task<IDbContextTransaction> BeginAsync(EePulseDbContext db, CancellationToken cancellationToken)
        {
            if (fault == TransactionFault.Begin)
                return Task.FromException<IDbContextTransaction>(new NpgsqlException("test transaction acquisition failure"));
            if (fault == TransactionFault.BeginCallerCancellation)
            {
                callerCancellation!.Cancel();
                return Task.FromException<IDbContextTransaction>(new OperationCanceledException(cancellationToken));
            }
            if (fault == TransactionFault.BeginProviderCancellation)
                return Task.FromException<IDbContextTransaction>(new OperationCanceledException("provider cancellation"));
            return db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        }

        public async Task RollbackAsync(IDbContextTransaction transaction)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (fault == TransactionFault.Rollback) throw new NpgsqlException("test rollback failure");
        }

        public async ValueTask DisposeAsync(IDbContextTransaction transaction)
        {
            await transaction.DisposeAsync();
            if (fault == TransactionFault.Dispose) throw new NpgsqlException("test disposal failure");
        }
    }

    private sealed class RecordingTransactionBoundary : IIncidentTransactionBoundary
    {
        public int RollbackCalls { get; private set; }

        public Task<IDbContextTransaction> BeginAsync(EePulseDbContext db, CancellationToken cancellationToken) =>
            db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        public async Task RollbackAsync(IDbContextTransaction transaction)
        {
            RollbackCalls++;
            await transaction.RollbackAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync(IDbContextTransaction transaction) => transaction.DisposeAsync();
    }

    private sealed class CollidingAdvisoryKeyProvider : IIncidentIdempotencyAdvisoryKeyProvider
    {
        public (int NamespaceKey, int DerivedKey) Derive(string idempotencyKey) => (IncidentIdempotencyAdvisoryKeyDeriver.NamespaceKey, 7);
    }

    private sealed class RuntimeCommandObserver : DbCommandInterceptor
    {
        private readonly List<string> commands = [];
        public IReadOnlyList<string> Commands => commands;
        public void Clear() => commands.Clear();

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result) { commands.Add(command.CommandText); return result; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result) { commands.Add(command.CommandText); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) { commands.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private sealed class FailIfCalledCoordinator : IIncidentCommandCoordinator
    {
        public int Calls { get; set; }

        public Task<IncidentCommandResult<TConflict>> ExecuteAsync<TCommand, TConflict>(IncidentCommandInput<TCommand> input,
            IIncidentLockedCommandHandler<TCommand, TConflict> handler, CancellationToken cancellationToken)
            where TCommand : notnull
            where TConflict : notnull
        {
            Calls++;
            throw new Xunit.Sdk.XunitException("A valid all-zero incident ID must not invoke the coordinator.");
        }
    }

    private sealed class ThrowingHandler : IIncidentLockedCommandHandler<string, string>
    {
        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A not-found command must not invoke the handler.");

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A not-found command must not construct a response.");
    }
}

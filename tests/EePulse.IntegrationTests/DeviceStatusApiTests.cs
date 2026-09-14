using System.Data;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EePulse.Application.Time;
using EePulse.Api.Authorization;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace EePulse.IntegrationTests;

public sealed class DeviceStatusApiTests
{
    private const string DeviceId = "00000000-0000-0000-0000-00000000d501";
    private const string MissingDeviceId = "00000000-0000-0000-0000-00000000d502";
    private const string SiteId = "00000000-0000-0000-0000-00000000d500";
    private const string ActorId = "00000000-0000-0000-0000-00000000d599";
    private const string CallerCorrelation = "caller-correlation";
    private const string PopulatedDeviceId = "00000000-0000-0000-0000-00000000b400";
    private const string PopulatedSiteId = "00000000-0000-0000-0000-00000000b300";
    private const string PopulatedProbeUpId = "00000000-0000-0000-0000-00000000b401";
    private const string PopulatedProbeDegradedId = "00000000-0000-0000-0000-00000000b402";
    private const string PopulatedProbeDownId = "00000000-0000-0000-0000-00000000b403";
    private const string PopulatedProbeRecoveringId = "00000000-0000-0000-0000-00000000b404";
    private const string PopulatedProbeUnknownId = "00000000-0000-0000-0000-00000000b405";
    private const string PopulatedProbeMissingId = "00000000-0000-0000-0000-00000000b406";
    private const string OverlayDeviceDisabledId = "00000000-0000-0000-0000-00000000b501";
    private const string OverlayDeviceDisabledProbeId = "00000000-0000-0000-0000-00000000b511";
    private const string OverlaySiteMaintenanceDeviceId = "00000000-0000-0000-0000-00000000b502";
    private const string OverlaySiteMaintenanceProbeId = "00000000-0000-0000-0000-00000000b512";
    private const string OverlayDeviceMaintenanceDeviceId = "00000000-0000-0000-0000-00000000b503";
    private const string OverlayDeviceMaintenanceProbeId = "00000000-0000-0000-0000-00000000b513";
    private const string OverlayProbeMaintenanceDeviceId = "00000000-0000-0000-0000-00000000b504";
    private const string OverlayProbeDisabledId = "00000000-0000-0000-0000-00000000b514";
    private const string OverlayProbeMaintenanceId = "00000000-0000-0000-0000-00000000b515";
    private const string OverlayStartInclusiveId = "00000000-0000-0000-0000-00000000b516";
    private const string OverlayEndExclusiveId = "00000000-0000-0000-0000-00000000b517";
    private const string OverlayExpiredId = "00000000-0000-0000-0000-00000000b518";
    private const string OverlayFutureId = "00000000-0000-0000-0000-00000000b519";
    private const string OverlayDisabledWindowId = "00000000-0000-0000-0000-00000000b520";
    private const string OverlayUnrelatedWindowId = "00000000-0000-0000-0000-00000000b521";
    private const string OverlayDisabledMissingProjectionId = "00000000-0000-0000-0000-00000000b522";
    private const string OverlayMaintenanceMissingProjectionId = "00000000-0000-0000-0000-00000000b523";
    private const string C1BrokenLedgerDeviceId = "00000000-0000-0000-0000-00000000b601";
    private const string C1MissingAgentDeviceId = "00000000-0000-0000-0000-00000000b602";
    private const string C1DuplicateTagsDeviceId = "00000000-0000-0000-0000-00000000b603";
    private const string C3MismatchedWatermarkDeviceId = "00000000-0000-0000-0000-00000000b604";
    private const string C3MismatchedWatermarkProbeId = "00000000-0000-0000-0000-00000000b614";
    private const string C2HostileMarker = "c2-hostile-malformed-tags";
    private const string C3HostileMarker = "c3-private-watermark-device";
    private static readonly DateTimeOffset EvaluationInstant = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] PersistedPopulatedTags = ["alpha", "zeta"];
    private static readonly string[] SafeProblemPropertyNames = ["code", "correlationId", "detail", "status", "title", "type"];
    private const string C1HostileMarker = "c1-hostile-device-data";

    [Fact]
    public async Task AuthorizationUsesDashboardReadRolesAndSafeCorrelation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(Request(Path(DeviceId), null), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var anonymousCorrelation = AssertSafeCorrelation(anonymous);
        await AssertDoesNotLeakCallerCorrelationAsync(anonymous, ct);
        using var forbidden = await client.SendAsync(Request(Path(DeviceId), "Guest"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var forbiddenCorrelation = AssertSafeCorrelation(forbidden);
        Assert.NotEqual(anonymousCorrelation, forbiddenCorrelation);
        await AssertDoesNotLeakCallerCorrelationAsync(forbidden, ct);

        foreach (var role in new[] { "Viewer", "Operator", "Engineer", "Administrator", "Auditor" })
        {
            using var allowed = await client.SendAsync(Request(Path(DeviceId), role), ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            AssertSafeCorrelation(allowed);
        }

        using var unauthorizedInvalid = await client.SendAsync(Request(Path("not-a-device"), null), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedInvalid.StatusCode);
        AssertSafeCorrelation(unauthorizedInvalid);
        using var forbiddenInvalid = await client.SendAsync(Request(Path("not-a-device"), "Guest"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenInvalid.StatusCode);
        AssertSafeCorrelation(forbiddenInvalid);

        foreach (var caseVariantPath in new[]
        {
            "/API/V1/DEVICES/" + DeviceId + "/STATUS",
            "/Api/V1/DeViCeS/" + DeviceId + "/StAtUs",
            "/API/V1/DEVICES/" + DeviceId + "/STATUS/"
        })
        {
            using var caseVariantAnonymous = await client.SendAsync(Request(caseVariantPath, null), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, caseVariantAnonymous.StatusCode);
            await AssertDoesNotLeakCallerCorrelationAsync(caseVariantAnonymous, ct);
            AssertSafeCorrelation(caseVariantAnonymous);

            using var caseVariantForbidden = await client.SendAsync(Request(caseVariantPath, "Guest"), ct);
            Assert.Equal(HttpStatusCode.Forbidden, caseVariantForbidden.StatusCode);
            await AssertDoesNotLeakCallerCorrelationAsync(caseVariantForbidden, ct);
            AssertSafeCorrelation(caseVariantForbidden);
        }
    }

    [Fact]
    public async Task RouteIdQueryAndIfMatchValidationAreSafeAndPreDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        var observer = new DeviceStatusCommandObserver();
        using var logs = new DeviceStatusLogCaptureSink();
        string? httpClientRawTarget = null;
        await using var factory = CreateFailureFactory(postgres.ConnectionString, logs, services =>
            services.AddSingleton<IStartupFilter>(new RawTargetCaptureStartupFilter(context =>
            {
                if (context.Request.Headers.ContainsKey("X-WP07-RawTarget-Probe"))
                    httpClientRawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            })), observer);
        using var client = factory.CreateClient();

        foreach (var invalidId in InvalidIds())
        {
            observer.Clear();
            await AssertProblemAsync(client, Path(invalidId), HttpStatusCode.BadRequest, Wp07DashboardContract.InvalidDeviceStatusIdCode, ct);
            Assert.Empty(observer.Commands);
        }

        foreach (var query in new[] { "?x=1", "?x=", "?=value", "?x=1&x=2", "?status=Down", "?x=1&y=2" })
        {
            observer.Clear();
            await AssertProblemAsync(client, Path(DeviceId) + query, HttpStatusCode.BadRequest, Wp07DashboardContract.InvalidDeviceStatusQueryCode, ct);
            Assert.Empty(observer.Commands);
        }

        observer.Clear();
        using (var bareDelimiterProbe = Request(Path(DeviceId) + "?", "Viewer"))
        {
            bareDelimiterProbe.Headers.TryAddWithoutValidation("X-WP07-RawTarget-Probe", "1");
            using var response = await client.SendAsync(bareDelimiterProbe, ct);
            var observedRawTarget = Assert.IsType<string>(httpClientRawTarget);
            if (observedRawTarget.EndsWith('?'))
            {
                Assert.Equal(Path(DeviceId) + "?", observedRawTarget);
                await AssertProblemAsync(response, HttpStatusCode.BadRequest, Wp07DashboardContract.InvalidDeviceStatusQueryCode, ct);
                Assert.Empty(observer.Commands);
            }
            else
            {
                Assert.DoesNotContain("?", observedRawTarget, StringComparison.Ordinal);
            }
        }

        observer.Clear();
        using var bareDelimiterBody = new MemoryStream();
        string? bareDelimiterRawTarget = null;
        var bareDelimiterQueryCount = -1;
        var bareDelimiter = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = PathString.FromUriComponent(Path(DeviceId));
            context.Request.QueryString = QueryString.Empty;
            context.Request.Headers[DevelopmentAuthenticationHandler.RoleHeader] = "Viewer";
            context.Request.Headers[DevelopmentAuthenticationHandler.ActorHeader] = ActorId;
            context.Request.Headers["X-Correlation-ID"] = CallerCorrelation;
            context.Response.Body = bareDelimiterBody;

            var requestFeature = context.Features.Get<IHttpRequestFeature>();
            Assert.NotNull(requestFeature);
            requestFeature.RawTarget = Path(DeviceId) + "?";
            bareDelimiterRawTarget = requestFeature.RawTarget;
            bareDelimiterQueryCount = context.Request.Query.Count;
        }, ct);

        Assert.Equal(Path(DeviceId) + "?", bareDelimiterRawTarget);
        Assert.Equal(0, bareDelimiterQueryCount);
        Assert.Equal(StatusCodes.Status400BadRequest, bareDelimiter.Response.StatusCode);
        Assert.Equal("application/problem+json", bareDelimiter.Response.ContentType);
        var bareDelimiterCorrelation = bareDelimiter.Response.Headers["X-Correlation-ID"].ToString();
        Assert.True(Guid.TryParseExact(bareDelimiterCorrelation, "N", out _));
        Assert.Equal(bareDelimiterCorrelation.ToLowerInvariant(), bareDelimiterCorrelation);
        Assert.NotEqual(CallerCorrelation, bareDelimiterCorrelation);
        var bareDelimiterProblemBody = Encoding.UTF8.GetString(bareDelimiterBody.ToArray());
        Assert.DoesNotContain(CallerCorrelation, bareDelimiterProblemBody, StringComparison.Ordinal);
        using (var bareDelimiterProblem = JsonDocument.Parse(bareDelimiterProblemBody))
        {
            var root = bareDelimiterProblem.RootElement;
            Assert.Equal(StatusCodes.Status400BadRequest, root.GetProperty("status").GetInt32());
            Assert.Equal(Wp07DashboardContract.InvalidDeviceStatusQueryCode, root.GetProperty("code").GetString());
            Assert.Equal(Wp07DashboardContract.InvalidDeviceStatusQueryCode, root.GetProperty("title").GetString());
            Assert.Equal("The device status query is invalid.", root.GetProperty("detail").GetString());
            Assert.Equal(bareDelimiterCorrelation, root.GetProperty("correlationId").GetString());
            Assert.Equal(SafeProblemPropertyNames,
                root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        }

        Assert.Empty(observer.Commands);

        foreach (var ifMatch in new[] { "\"tag\"", "W/\"tag\"", "*", "\"one\", \"two\"" })
        {
            observer.Clear();
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            using var response = await client.SendAsync(request, ct);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, Wp07DashboardContract.IfMatchUnsupportedCode, ct);
            Assert.Empty(observer.Commands);
        }

        foreach (var unmatched in new[] { "/api/v1/devices/status" })
        {
            using var response = await client.SendAsync(Request(unmatched, "Viewer"), ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        byte[] canonicalBytes;
        string canonicalEtag;
        string canonicalCorrelation;
        observer.Clear();
        using (var canonical = await client.SendAsync(Request(Path(DeviceId), "Viewer"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
            Assert.Equal(Wp07DashboardCanonicalizer.ContentType, canonical.Content.Headers.ContentType!.ToString());
            AssertCacheControl(canonical);
            canonicalCorrelation = AssertSafeCorrelation(canonical);
            canonicalBytes = await canonical.Content.ReadAsByteArrayAsync(ct);
            canonicalEtag = canonical.Headers.ETag!.Tag!;

            observer.Clear();
            using var trailingSlash = await client.SendAsync(Request(Path(DeviceId) + "/", "Viewer"), ct);
            Assert.Equal(HttpStatusCode.OK, trailingSlash.StatusCode);
            Assert.Equal(Wp07DashboardCanonicalizer.ContentType, trailingSlash.Content.Headers.ContentType!.ToString());
            AssertCacheControl(trailingSlash);
            var trailingCorrelation = AssertSafeCorrelation(trailingSlash);
            Assert.NotEqual(canonicalCorrelation, trailingCorrelation);
            Assert.Equal(canonicalBytes, await trailingSlash.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(canonicalEtag, trailingSlash.Headers.ETag!.Tag);
            Assert.NotEmpty(observer.Commands);

            var trailingPathEvents = logs.Entries
                .Where(entry => entry.State.Any(pair => pair.Key == "RequestPath" && pair.Value == Path(DeviceId) + "/"))
                .ToArray();
            Assert.NotEmpty(trailingPathEvents);
            Assert.All(trailingPathEvents.SelectMany(entry => entry.State), pair => Assert.DoesNotContain(CallerCorrelation, pair.Value, StringComparison.Ordinal));
            var trailingDownstreamCorrelations = trailingPathEvents
                .SelectMany(entry => entry.State)
                .Where(pair => pair.Key == "CorrelationId")
                .Select(pair => pair.Value)
                .ToArray();
            Assert.NotEmpty(trailingDownstreamCorrelations);
            Assert.All(trailingDownstreamCorrelations, value => Assert.Equal(trailingCorrelation, value));
        }

        foreach (var caseVariantPath in new[]
        {
            "/API/V1/DEVICES/" + DeviceId + "/STATUS",
            "/Api/V1/DeViCeS/" + DeviceId + "/StAtUs",
            "/API/V1/DEVICES/" + DeviceId + "/STATUS/"
        })
        {
            observer.Clear();
            using var caseVariant = await client.SendAsync(Request(caseVariantPath, "Viewer"), ct);
            Assert.Equal(HttpStatusCode.OK, caseVariant.StatusCode);
            Assert.Equal(Wp07DashboardCanonicalizer.ContentType, caseVariant.Content.Headers.ContentType!.ToString());
            AssertCacheControl(caseVariant);
            var caseVariantCorrelation = AssertSafeCorrelation(caseVariant);
            Assert.NotEqual(canonicalCorrelation, caseVariantCorrelation);
            Assert.Equal(canonicalBytes, await caseVariant.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(canonicalEtag, caseVariant.Headers.ETag!.Tag);
            Assert.NotEmpty(observer.Commands);
            await AssertDoesNotLeakCallerCorrelationAsync(caseVariant, ct);

            var caseVariantPathEvents = logs.Entries
                .Where(entry => entry.State.Any(pair => pair.Key == "RequestPath" && pair.Value == caseVariantPath))
                .ToArray();
            Assert.NotEmpty(caseVariantPathEvents);
            Assert.All(caseVariantPathEvents.SelectMany(entry => entry.State), pair => Assert.DoesNotContain(CallerCorrelation, pair.Value, StringComparison.Ordinal));
            var caseVariantDownstreamCorrelations = caseVariantPathEvents
                .SelectMany(entry => entry.State)
                .Where(pair => pair.Key == "CorrelationId")
                .Select(pair => pair.Value)
                .ToArray();
            Assert.NotEmpty(caseVariantDownstreamCorrelations);
            Assert.All(caseVariantDownstreamCorrelations, value => Assert.Equal(caseVariantCorrelation, value));
        }

        observer.Clear();
        await AssertProblemAsync(client, "/API/V1/DEVICES/" + DeviceId.ToUpperInvariant() + "/STATUS", HttpStatusCode.BadRequest,
            Wp07DashboardContract.InvalidDeviceStatusIdCode, ct);
        Assert.Empty(observer.Commands);

        foreach (var shapedUnmatched in new[]
        {
            Path(DeviceId) + "/extra",
            Path("not-a-device") + "/extra",
            "/API/V1/DEVICES/" + DeviceId + "/STATUS/extra",
            "/api/v1/devices//status",
            "/api/v1/devices/" + DeviceId + "//status",
            "/API/V1/DEVICES//STATUS",
            "/API/V1/DEVICES/" + DeviceId + "//STATUS",
        })
        {
            observer.Clear();
            using var response = await client.SendAsync(Request(shapedUnmatched, "Viewer"), ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            AssertSafeCorrelation(response);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain(CallerCorrelation, body, StringComparison.Ordinal);
            var responseHeaders = response.Headers
                .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value)))
                .Concat(response.Content.Headers.SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value))))
                .ToArray();
            Assert.All(responseHeaders, header => Assert.DoesNotContain(CallerCorrelation, header.Key + "=" + header.Value, StringComparison.Ordinal));
            var pathEvents = logs.Entries.Where(entry => entry.State.Any(pair => pair.Key == "RequestPath" && pair.Value == shapedUnmatched)).ToArray();
            Assert.NotEmpty(pathEvents);
            var downstreamRequestIds = pathEvents
                .SelectMany(entry => entry.State)
                .Where(pair => pair.Key == "RequestId")
                .Select(pair => pair.Value)
                .ToArray();
            Assert.NotEmpty(downstreamRequestIds);
            Assert.All(downstreamRequestIds, value => Assert.NotEqual(CallerCorrelation, value));
            Assert.All(pathEvents.SelectMany(entry => entry.State), pair => Assert.DoesNotContain(CallerCorrelation, pair.Value, StringComparison.Ordinal));
            Assert.Empty(observer.Commands);
        }

        using (var unrelated = await client.SendAsync(Request("/api/v1/sites/", "Viewer"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, unrelated.StatusCode);
            Assert.Equal(CallerCorrelation, unrelated.Headers.GetValues("X-Correlation-ID").Single());
        }

        observer.Clear();
        using (var statusHistory = await client.SendAsync(Request(Path(DeviceId).Replace("/status", "/status-history", StringComparison.Ordinal), "Viewer"), ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, statusHistory.StatusCode);
            Assert.Equal(CallerCorrelation, statusHistory.Headers.GetValues("X-Correlation-ID").Single());
            Assert.Empty(observer.Commands);
        }
    }

    [Fact]
    public async Task ExistingAndMissingDeviceResponsesAreCanonicalAndSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var missing = await client.SendAsync(Request(Path(MissingDeviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        AssertSafeCorrelation(missing);
        await AssertDoesNotLeakCallerCorrelationAsync(missing, ct);

        using var response = await client.SendAsync(Request(Path(DeviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Wp07DashboardCanonicalizer.ContentType, response.Content.Headers.ContentType!.ToString());
        AssertCacheControl(response);
        AssertSafeCorrelation(response);
        var actualBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var expectedBytes = Wp07DashboardCanonicalizer.SerializeDeviceStatus(new StatusEnrichedDeviceResponse(
            DeviceId, SiteId, "Device status", "10.20.30.40", null, "Router", null, null, "Normal", ["zeta", "alpha"], true, 1, []));
        Assert.Equal(expectedBytes, actualBytes);
        Assert.Equal(Wp07DashboardCanonicalizer.EtagFor(expectedBytes), response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task ConditionalGetAndOpenApiExclusionAreStable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var initial = await client.SendAsync(Request(Path(DeviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var etag = initial.Headers.ETag!.Tag!;
        var canonicalBytes = await initial.Content.ReadAsByteArrayAsync(ct);
        var obsTextTag = "\"\u0080\u00FF\"";
        foreach (var value in new[]
        {
            etag,
            "W/" + etag,
            "\"other\", W/" + etag,
            "\"\", " + etag,
            obsTextTag + ", " + etag,
            "*",
            etag + ",",
            ",," + etag + ",,",
            "\"other\",, " + etag + ","
        })
        {
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-None-Match", value);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(etag, response.Headers.ETag!.Tag);
            AssertCacheControl(response);
            AssertSafeCorrelation(response);
        }

        using (var request = Request(Path(DeviceId), "Viewer"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", obsTextTag);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(canonicalBytes, await response.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(etag, response.Headers.ETag!.Tag);
            AssertCacheControl(response);
            AssertSafeCorrelation(response);
        }

        using (var request = Request(Path(DeviceId), "Viewer"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"other\",,,");
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(canonicalBytes, await response.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(etag, response.Headers.ETag!.Tag);
            AssertCacheControl(response);
            AssertSafeCorrelation(response);
        }

        using (var request = Request(Path(DeviceId), "Viewer"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"\"");
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(canonicalBytes, await response.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(etag, response.Headers.ETag!.Tag);
            AssertCacheControl(response);
            AssertSafeCorrelation(response);
        }

        foreach (var malformed in new[] { "broken", "\"broken", "*, \"other\"", ",,,", "\"\u007F\"", "\"\u0100\"" })
        {
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-None-Match", malformed);
            using var response = await client.SendAsync(request, ct);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, Wp07DashboardContract.InvalidIfNoneMatchCode, ct);
        }

        using var openApi = await client.GetAsync("/openapi/v1.json", ct);
        Assert.DoesNotContain("/api/v1/devices/{id}/status", await openApi.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PopulatedProjectionWatermarkAndCanonicalRepresentationAreStable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedPopulatedProjectionFixtureAsync(postgres.ConnectionString, ct);
        var databaseBefore = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        var commands = new DeviceStatusCommandObserver();
        await using var factory = CreateFixedClockFactory(postgres.ConnectionString, EvaluationInstant, commands);
        using var client = factory.CreateClient();

        var expected = PopulatedExpectedResponse();
        var expectedBytes = Wp07DashboardCanonicalizer.SerializeDeviceStatus(expected);
        var expectedEtag = Wp07DashboardCanonicalizer.EtagFor(expectedBytes);

        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var verification = new EePulseDbContext(options))
        {
            var upProbeId = Guid.Parse(PopulatedProbeUpId);
            var watermark = await verification.ProbeStatusProjections.AsNoTracking()
                .Where(projection => projection.ProbeId == upProbeId)
                .Select(projection => new { projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId })
                .SingleAsync(ct);
            Assert.Equal(EvaluationInstant.AddSeconds(2), watermark.LastFreshEventAt);
            Assert.Equal(EvaluationInstant.AddSeconds(2), watermark.WatermarkEventAt);
            Assert.Equal(GuidFor(601), watermark.WatermarkAgentId);
            Assert.Equal(GuidFor(801), watermark.WatermarkResultId);
            var exactLedger = await verification.ProbeResultLedgerEntries.AsNoTracking()
                .Where(entry => entry.AgentId == watermark.WatermarkAgentId && entry.ResultId == watermark.WatermarkResultId && entry.ProbeId == upProbeId)
                .Select(entry => new { entry.EndedAt, entry.ReceivedAt })
                .SingleAsync(ct);
            Assert.Equal(watermark.WatermarkEventAt, exactLedger.EndedAt);
            Assert.Equal(EvaluationInstant.AddSeconds(2), exactLedger.EndedAt);
            Assert.Equal(EvaluationInstant.AddSeconds(1), exactLedger.ReceivedAt);
            Assert.True(watermark.LastFreshEventAt > exactLedger.ReceivedAt);

            var distractorReceipts = await verification.ProbeResultLedgerEntries.AsNoTracking()
                .Where(entry => entry.ProbeId == upProbeId &&
                    ((entry.AgentId == GuidFor(601) && entry.ResultId == GuidFor(899)) ||
                     (entry.AgentId == GuidFor(998) && entry.ResultId == GuidFor(801))))
                .Select(entry => entry.ReceivedAt)
                .Order()
                .ToArrayAsync(ct);
            Assert.Equal(new[] { EvaluationInstant.AddDays(-1), EvaluationInstant.AddDays(1) }, distractorReceipts);
        }

        using var first = await client.SendAsync(Request(Path(PopulatedDeviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(Wp07DashboardCanonicalizer.ContentType, first.Content.Headers.ContentType!.ToString());
        AssertCacheControl(first);
        var firstCorrelation = AssertSafeCorrelation(first);
        var firstBytes = await first.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(expectedBytes, firstBytes);
        Assert.Equal(expectedEtag, first.Headers.ETag!.Tag);
        AssertExactWatermarkSql(commands);
        commands.Clear();
        using (var json = JsonDocument.Parse(firstBytes))
        {
            var upProbe = json.RootElement.GetProperty("probes").EnumerateArray()
                .Single(probe => probe.GetProperty("probeId").GetString() == PopulatedProbeUpId);
            Assert.Equal(EvaluationInstant.AddSeconds(2), upProbe.GetProperty("lastFreshEventAt").GetDateTimeOffset());
            Assert.Equal(EvaluationInstant.AddSeconds(1), upProbe.GetProperty("lastReceivedAt").GetDateTimeOffset());
            Assert.Equal(GuidFor(601).ToString("D"), upProbe.GetProperty("agentId").GetString());
            Assert.True(upProbe.GetProperty("lastFreshEventAt").GetDateTimeOffset() > upProbe.GetProperty("lastReceivedAt").GetDateTimeOffset());
        }

        using var second = await client.SendAsync(Request(Path(PopulatedDeviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBytes = await second.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(expectedEtag, second.Headers.ETag!.Tag);
        Assert.NotEqual(firstCorrelation, AssertSafeCorrelation(second));

        foreach (var conditional in new[] { expectedEtag, "W/" + expectedEtag })
        {
            using var request = Request(Path(PopulatedDeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-None-Match", conditional);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(expectedEtag, response.Headers.ETag!.Tag);
            AssertCacheControl(response);
            AssertSafeCorrelation(response);
        }

        Assert.Equal(databaseBefore, await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct));
        await using var finalVerification = new EePulseDbContext(options);
        var persistedTags = await finalVerification.Devices.AsNoTracking()
            .Where(device => device.Id == Guid.Parse(PopulatedDeviceId))
            .Select(device => EF.Property<List<string>>(device, "_tags"))
            .SingleAsync(ct);
        Assert.Equal(PersistedPopulatedTags, persistedTags);
    }

    [Fact]
    public async Task OverlayPrecedenceUsesFixedClockAndScopedMaintenance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedOverlayFixtureAsync(postgres.ConnectionString, ct);
        var verificationOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var verification = new EePulseDbContext(verificationOptions))
        {
            var projections = await verification.ProbeStatusProjections.AsNoTracking()
                .Where(projection => projection.ProbeId == Guid.Parse(OverlayDisabledMissingProjectionId) ||
                    projection.ProbeId == Guid.Parse(OverlayMaintenanceMissingProjectionId))
                .Select(projection => projection.ProbeId)
                .ToArrayAsync(ct);
            Assert.Empty(projections);
        }

        await using var factory = CreateFixedClockFactory(postgres.ConnectionString, EvaluationInstant);
        using var client = factory.CreateClient();

        await AssertOverlayAsync(client, OverlayDeviceDisabledId,
            ct, (OverlayDeviceDisabledProbeId, DashboardProbeStatus.Disabled, DashboardProbeStatus.Up));
        await AssertOverlayAsync(client, OverlaySiteMaintenanceDeviceId,
            ct, (OverlaySiteMaintenanceProbeId, DashboardProbeStatus.Maintenance, DashboardProbeStatus.Up));
        await AssertOverlayAsync(client, OverlayDeviceMaintenanceDeviceId,
            ct,
            (OverlayDeviceMaintenanceProbeId, DashboardProbeStatus.Maintenance, DashboardProbeStatus.Degraded),
            (OverlayDisabledMissingProjectionId, DashboardProbeStatus.Disabled, DashboardProbeStatus.Unknown),
            (OverlayMaintenanceMissingProjectionId, DashboardProbeStatus.Maintenance, DashboardProbeStatus.Unknown));
        await AssertMissingProjectionOverlayAsync(client, OverlayDeviceMaintenanceDeviceId, OverlayDisabledMissingProjectionId,
            DashboardProbeStatus.Disabled, ct);
        await AssertMissingProjectionOverlayAsync(client, OverlayDeviceMaintenanceDeviceId, OverlayMaintenanceMissingProjectionId,
            DashboardProbeStatus.Maintenance, ct);
        await AssertOverlayAsync(client, OverlayProbeMaintenanceDeviceId,
            ct,
            (OverlayProbeDisabledId, DashboardProbeStatus.Disabled, DashboardProbeStatus.Down),
            (OverlayProbeMaintenanceId, DashboardProbeStatus.Maintenance, DashboardProbeStatus.Recovering),
            (OverlayStartInclusiveId, DashboardProbeStatus.Maintenance, DashboardProbeStatus.Unknown),
            (OverlayEndExclusiveId, DashboardProbeStatus.Up, DashboardProbeStatus.Up),
            (OverlayExpiredId, DashboardProbeStatus.Degraded, DashboardProbeStatus.Degraded),
            (OverlayFutureId, DashboardProbeStatus.Down, DashboardProbeStatus.Down),
            (OverlayDisabledWindowId, DashboardProbeStatus.Recovering, DashboardProbeStatus.Recovering),
            (OverlayUnrelatedWindowId, DashboardProbeStatus.Unknown, DashboardProbeStatus.Unknown));
    }

    [Fact]
    public async Task InconsistentWatermarkLedgersReturnSanitizedUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedBrokenLedgerFixtureAsync(postgres.ConnectionString, ct);
        using var logs = new DeviceStatusLogCaptureSink();
        var commands = new DeviceStatusCommandObserver();
        await using var factory = CreateFailureFactory(postgres.ConnectionString, logs, commands);
        using var client = factory.CreateClient();

        using var request = Request(Path(C1BrokenLedgerDeviceId), "Viewer");
        request.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        request.Headers.TryAddWithoutValidation("tracestate", "c1=hostile-trace-state");
        using var response = await client.SendAsync(request, ct);
        await AssertUnavailableAsync(response, logs, ct);

        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using var verification = new EePulseDbContext(options);
        var targetProbe = GuidFor(1_601);
        Assert.False(await verification.ProbeResultLedgerEntries.AsNoTracking().AnyAsync(entry =>
            entry.AgentId == GuidFor(1_701) && entry.ResultId == GuidFor(1_801) && entry.ProbeId == targetProbe, ct));
        Assert.True(await verification.ProbeResultLedgerEntries.AsNoTracking().AnyAsync(entry =>
            entry.AgentId == GuidFor(1_701) && entry.ResultId == GuidFor(1_899) && entry.ProbeId == targetProbe, ct));
        var ledgerKey = verification.Model.FindEntityType(typeof(ProbeResultLedgerEntry))!.FindPrimaryKey()!;
        Assert.Equal(["AgentId", "ResultId"], ledgerKey.Properties.Select(property => property.Name));
        Predicate<string> isLedgerQuery = command => command.Contains("probe_result_ledger", StringComparison.OrdinalIgnoreCase);
        Assert.Single(commands.Commands, isLedgerQuery);
        var ledgerSql = commands.Commands.First(command => isLedgerQuery(command));
        var normalizedSql = Regex.Replace(ledgerSql, @"\s+", " ");
        Assert.Matches(
            """ON \(?[\w".]+\."?watermark_agent_id"? = [\w".]+\."?agent_id"? AND [\w".]+\."?watermark_result_id"? = [\w".]+\."?result_id"?\)? AND [\w".]+\."?id"? = [\w".]+\."?probe_id"?""",
            normalizedSql);
    }

    [Fact]
    public async Task MismatchedWatermarkEventTimeReturnsSanitizedUnavailableWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedMismatchedWatermarkFixtureAsync(postgres.ConnectionString, ct);
        var databaseBefore = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        using var logs = new DeviceStatusLogCaptureSink();
        var commands = new DeviceStatusCommandObserver();
        await using var factory = CreateFailureFactory(postgres.ConnectionString, logs, commands);
        using var client = factory.CreateClient();

        var watermarkAt = EvaluationInstant.AddSeconds(2);
        var ledgerEndedAt = EvaluationInstant.AddSeconds(3);
        var ledgerReceivedAt = EvaluationInstant.AddSeconds(1);
        var agentId = GuidFor(1_720);
        var resultId = GuidFor(1_820);
        var probeId = Guid.Parse(C3MismatchedWatermarkProbeId);
        using var request = Request(Path(C3MismatchedWatermarkDeviceId), "Viewer");
        request.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        request.Headers.TryAddWithoutValidation("tracestate", "c1=hostile-trace-state");
        using var response = await client.SendAsync(request, ct);
        await AssertUnavailableAsync(response, logs, ct,
            C3HostileMarker,
            C3MismatchedWatermarkDeviceId,
            C3MismatchedWatermarkProbeId,
            agentId.ToString("D"),
            resultId.ToString("D"),
            "c3-private-watermark-agent",
            "C3 site",
            "C3 group",
            "10.60.0.4",
            watermarkAt.ToString("O", CultureInfo.InvariantCulture),
            ledgerEndedAt.ToString("O", CultureInfo.InvariantCulture),
            ledgerReceivedAt.ToString("O", CultureInfo.InvariantCulture));
        AssertExactWatermarkSql(commands);
        Assert.Equal(databaseBefore, await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct));

        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using var verification = new EePulseDbContext(options);
        var projection = await verification.ProbeStatusProjections.AsNoTracking()
            .Where(value => value.ProbeId == probeId)
            .Select(value => new { value.WatermarkAgentId, value.WatermarkResultId, value.WatermarkEventAt })
            .SingleAsync(ct);
        var exactLedger = await verification.ProbeResultLedgerEntries.AsNoTracking()
            .Where(value => value.AgentId == projection.WatermarkAgentId && value.ResultId == projection.WatermarkResultId && value.ProbeId == probeId)
            .Select(value => new { value.EndedAt, value.ReceivedAt })
            .SingleAsync(ct);
        Assert.Equal(agentId, projection.WatermarkAgentId);
        Assert.Equal(resultId, projection.WatermarkResultId);
        Assert.Equal(watermarkAt, projection.WatermarkEventAt);
        Assert.Equal(ledgerEndedAt, exactLedger.EndedAt);
        Assert.Equal(ledgerReceivedAt, exactLedger.ReceivedAt);
        Assert.NotEqual(projection.WatermarkEventAt, exactLedger.EndedAt);
    }

    [Fact]
    public async Task MissingWatermarkAgentAndCanonicalizationFailuresAreSanitized()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedMissingAgentAndDuplicateTagsFixtureAsync(postgres.ConnectionString, ct);
        using var logs = new DeviceStatusLogCaptureSink();
        await using var factory = CreateFailureFactory(postgres.ConnectionString, logs);
        using var client = factory.CreateClient();

        using (var missingAgent = await client.SendAsync(Request(Path(C1MissingAgentDeviceId), "Viewer"), ct))
        {
            await AssertUnavailableAsync(missingAgent, logs, ct);
        }

        logs.Clear();
        using (var duplicateTags = await client.SendAsync(Request(Path(C1DuplicateTagsDeviceId), "Viewer"), ct))
        {
            var body = await AssertUnavailableAsync(duplicateTags, logs, ct);
            Assert.DoesNotContain(C1HostileMarker, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MalformedPersistedDeviceTagsAreSanitizedAndMutationFree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        await CorruptDeviceTagsAsync(postgres.ConnectionString, ct);

        var rawBefore = await ReadRawDeviceTagsAsync(postgres.ConnectionString, ct);
        Assert.Single(rawBefore);
        var beforeTags = Assert.Single(rawBefore);
        Assert.Equal(Guid.Parse(DeviceId), beforeTags.Id);
        Assert.Equal("object", beforeTags.JsonType);
        using (var beforeDocument = JsonDocument.Parse(beforeTags.Json))
        {
            Assert.Equal(JsonValueKind.Object, beforeDocument.RootElement.ValueKind);
        }

        var before = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        using var logs = new DeviceStatusLogCaptureSink();
        await using (var factory = CreateFailureFactory(postgres.ConnectionString, logs))
        {
            using var client = factory.CreateClient();
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
            request.Headers.TryAddWithoutValidation("tracestate", "c2=hostile-trace-state");
            using var response = await client.SendAsync(request, ct);
            await AssertUnavailableAsync(response, logs, ct);
        }

        var after = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        Assert.Equal(before, after);
        var rawAfter = await ReadRawDeviceTagsAsync(postgres.ConnectionString, ct);
        Assert.Equal(rawBefore, rawAfter);
        var afterTags = Assert.Single(rawAfter);
        Assert.Equal("object", afterTags.JsonType);
    }

    [Fact]
    public async Task ProviderFailuresAreSanitizedAndUnavailableLoggingIsPrivate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        var before = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        using var logs = new DeviceStatusLogCaptureSink();
        await using (var factory = CreateFailureFactory(postgres.ConnectionString, logs, new ThrowDeviceStatusReadInterceptor()))
        {
            using var client = factory.CreateClient();
            using var response = await client.SendAsync(Request(Path(DeviceId), "Viewer"), ct);
            var body = await AssertUnavailableAsync(response, logs, ct);
            Assert.DoesNotContain("c1 provider failure", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
        }
        var after = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task UnrelatedProviderCancellationIsSanitizedAndMutationFree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        var before = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        var httpContextAccessor = new HttpContextAccessor();
        var interceptor = new UnrelatedCancellationReadInterceptor(() => httpContextAccessor.HttpContext?.RequestAborted.IsCancellationRequested ?? true);
        using var logs = new DeviceStatusLogCaptureSink();
        await using (var factory = CreateFailureFactory(
            postgres.ConnectionString,
            logs,
            services => services.AddSingleton<IHttpContextAccessor>(httpContextAccessor),
            interceptor))
        {
            using var client = factory.CreateClient();
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", CallerCorrelation);
            request.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
            request.Headers.TryAddWithoutValidation("tracestate", "c1=hostile-trace-state");
            using var response = await client.SendAsync(request, ct);
            await AssertUnavailableAsync(response, logs, ct);
        }

        Assert.True(interceptor.CommandReached);
        Assert.False(interceptor.RequestAbortedAtBoundary);
        Assert.False(interceptor.HandlerTokenAtBoundary);
        var after = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DeviceStatusUnavailableOutcomesAreMutationFree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        await SeedBrokenLedgerFixtureAsync(postgres.ConnectionString, ct);
        await SeedMissingAgentAndDuplicateTagsFixtureAsync(postgres.ConnectionString, ct);

        async Task AssertUnchangedUnavailableAsync(string path, string? hostileMarker = null, params IInterceptor[] interceptors)
        {
            var before = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
            using var logs = new DeviceStatusLogCaptureSink();
            string body;
            await using (var factory = CreateFailureFactory(postgres.ConnectionString, logs, interceptors))
            {
                using var client = factory.CreateClient();
                using var response = await client.SendAsync(Request(path, "Viewer"), ct);
                body = await AssertUnavailableAsync(response, logs, ct);
            }

            var after = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
            Assert.Equal(before, after);
            if (hostileMarker is not null)
            {
                Assert.DoesNotContain(hostileMarker, body, StringComparison.OrdinalIgnoreCase);
            }
        }

        await AssertUnchangedUnavailableAsync(Path(C1BrokenLedgerDeviceId));
        await AssertUnchangedUnavailableAsync(Path(C1MissingAgentDeviceId));
        await AssertUnchangedUnavailableAsync(Path(C1DuplicateTagsDeviceId), C1HostileMarker);
        await AssertUnchangedUnavailableAsync(Path(DeviceId), interceptors: [new ThrowDeviceStatusReadInterceptor()]);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutUnavailableOrMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        var before = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        using var logs = new DeviceStatusLogCaptureSink();
        var boundary = new CancellationReadBoundaryInterceptor();
        await using var factory = CreateFailureFactory(postgres.ConnectionString, logs, boundary);
        using var client = factory.CreateClient();
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var request = Request(Path(DeviceId), "Viewer");
        var requestTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
        await boundary.Reached.Task.WaitAsync(ct);
        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await requestTask);
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == "EePulse.Api.Dashboard.DeviceStatusEndpoints" && entry.EventId.Id == 7002);
        var after = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DeviceStatusReadOutcomesAreMutationFree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedAsync(postgres.ConnectionString, ct);
        var observer = new DeviceStatusCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, observer);
        using var client = factory.CreateClient();

        async Task AssertUnchangedAsync(Func<Task> operation)
        {
            var before = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
            await operation();
            var after = await DeviceStatusFixtureSnapshotAsync(postgres.ConnectionString, ct);
            Assert.Equal(before, after);
        }

        string etag;
        using (var initial = await client.SendAsync(Request(Path(DeviceId), "Viewer"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            etag = initial.Headers.ETag!.Tag!;
        }
        await AssertUnchangedAsync(async () =>
        {
            using var response = await client.SendAsync(Request(Path(DeviceId), "Viewer"), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        await AssertUnchangedAsync(async () =>
        {
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        });
        await AssertUnchangedAsync(async () =>
        {
            using var response = await client.SendAsync(Request(Path(MissingDeviceId), "Viewer"), ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        });

        foreach (var path in new[] { "/api/v1/devices/not-a-device/status", Path(DeviceId) + "?x=1" })
        {
            observer.Clear();
            await AssertUnchangedAsync(async () =>
            {
                using var response = await client.SendAsync(Request(path, "Viewer"), ct);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            });
            Assert.Empty(observer.Commands);
        }

        await AssertUnchangedAsync(async () =>
        {
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
        await AssertUnchangedAsync(async () =>
        {
            using var request = Request(Path(DeviceId), "Viewer");
            request.Headers.TryAddWithoutValidation("If-None-Match", "broken");
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotEmpty(observer.Commands);
        });
    }

    private static IEnumerable<string> InvalidIds()
    {
        yield return DeviceId.ToUpperInvariant();
        yield return DeviceId.Replace("-", "", StringComparison.Ordinal);
        yield return "{" + DeviceId + "}";
        yield return "(" + DeviceId + ")";
        yield return Guid.Parse(DeviceId).ToString("X");
        yield return "%20" + DeviceId;
        yield return DeviceId + "%20";
        yield return "%09" + DeviceId;
        yield return "%2F";
        yield return "not-a-device";
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString, params IInterceptor[] interceptors) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        if (interceptors.Length == 0) return;
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<EePulseDbContext>>();
            services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
        });
    });

    private static string Path(string id) => "/api/v1/devices/" + id + "/status";
    private static HttpRequestMessage Request(string path, string? role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (role is not null)
        {
            request.Headers.TryAddWithoutValidation(DevelopmentAuthenticationHandler.RoleHeader, role);
            request.Headers.TryAddWithoutValidation(DevelopmentAuthenticationHandler.ActorHeader, ActorId);
        }
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", CallerCorrelation);
        return request;
    }

    private static async Task AssertProblemAsync(HttpClient client, string path, HttpStatusCode status, string code, CancellationToken ct)
    {
        using var response = await client.SendAsync(Request(path, "Viewer"), ct);
        await AssertProblemAsync(response, status, code, ct);
    }
    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        AssertSafeCorrelation(response);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(CallerCorrelation, body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
    private static string AssertSafeCorrelation(HttpResponseMessage response)
    {
        var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.True(Guid.TryParseExact(correlation, "N", out _));
        Assert.Equal(correlation.ToLowerInvariant(), correlation);
        Assert.NotEqual(CallerCorrelation, correlation);
        return correlation;
    }
    private static async Task AssertDoesNotLeakCallerCorrelationAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.DoesNotContain(CallerCorrelation, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        var headers = response.Headers
            .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value)))
            .Concat(response.Content.Headers.SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value))))
            .ToArray();
        Assert.All(headers, header => Assert.DoesNotContain(CallerCorrelation, header.Key + "=" + header.Value, StringComparison.Ordinal));
    }
    private static void AssertCacheControl(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.Private);
        Assert.Equal(TimeSpan.Zero, cacheControl.MaxAge);
        Assert.True(cacheControl.MustRevalidate);
        Assert.False(cacheControl.Public);
        Assert.False(cacheControl.NoCache);
        Assert.False(cacheControl.NoStore);
        Assert.False(cacheControl.NoTransform);
        Assert.False(cacheControl.OnlyIfCached);
        Assert.False(cacheControl.ProxyRevalidate);
        Assert.False(cacheControl.MaxStale);
        Assert.Null(cacheControl.MaxStaleLimit);
        Assert.Null(cacheControl.MinFresh);
        Assert.Null(cacheControl.SharedMaxAge);
        Assert.Empty(cacheControl.PrivateHeaders);
        Assert.Empty(cacheControl.NoCacheHeaders);
        Assert.Empty(cacheControl.Extensions);
    }
    private static async Task SeedAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var now = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var site = new Site(Guid.Parse(SiteId), "DST", "Device status", "UTC", now);
        db.Add(site);
        db.Add(new Device(Guid.Parse(DeviceId), site.Id, "Device status", "10.20.30.40", null, "Router", null, null, Criticality.Normal, ["zeta", "alpha"], now));
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> DeviceStatusFixtureSnapshotAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        var snapshot = new
        {
            Sites = await db.Sites.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.Code, value.Timezone, value.Name, value.Enabled, value.CreatedAt, value.UpdatedAt, value.RowVersion }).ToArrayAsync(ct),
            Devices = await db.Devices.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.SiteId, value.Name, value.Address, value.Hostname, value.DeviceType, value.Area, value.Owner, value.Criticality, value.Enabled, value.RowVersion, value.CreatedAt, value.UpdatedAt }).ToArrayAsync(ct),
            DeviceTags = await ReadRawDeviceTagsAsync(db, ct),
            Groups = await db.AgentGroups.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.Name, value.Description, value.CreatedAt, value.RowVersion, value.UpdatedAt, value.Enabled, value.ConfigurationVersion }).ToArrayAsync(ct),
            Probes = await db.Probes.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.DeviceId, value.AgentGroupId, value.Type, value.IntervalSeconds, value.TimeoutMilliseconds, value.AttemptCount, value.WarningRttMilliseconds, value.CriticalRttMilliseconds, value.FailureThreshold, value.RecoveryThreshold, value.Enabled, value.ConfigVersion, value.RowVersion }).ToArrayAsync(ct),
            Projections = await db.ProbeStatusProjections.AsNoTracking().OrderBy(value => value.ProbeId).ToArrayAsync(ct),
            Agents = await db.Agents.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(ct),
            Ledgers = await db.ProbeResultLedgerEntries.AsNoTracking().OrderBy(value => value.AgentId).ThenBy(value => value.ResultId).ToArrayAsync(ct),
            Windows = await db.MaintenanceWindows.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(ct),
            Incidents = await db.AvailabilityIncidents.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.ProbeId, value.RuleKey, value.Status, value.OpenedAt, value.AcknowledgedAt, value.AcknowledgedBy, value.AcknowledgementComment, value.ResolvedAt, value.ResolvedBy, value.ResolutionNote, value.OccurrenceCount }).ToArrayAsync(ct)
        };
        return JsonSerializer.Serialize(snapshot);
    }

    private static async Task CorruptDeviceTagsAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        const string malformedTags = """{"c2-hostile-malformed-tags":"object-value"}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE devices SET tags = {malformedTags}::jsonb WHERE id = {Guid.Parse(DeviceId)}", ct);
    }

    private static async Task<RawDeviceTags[]> ReadRawDeviceTagsAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        return await ReadRawDeviceTagsAsync(db, ct);
    }

    private static async Task<RawDeviceTags[]> ReadRawDeviceTagsAsync(EePulseDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, tags::text, jsonb_typeof(tags) FROM devices ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<RawDeviceTags>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new RawDeviceTags(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }

            return rows.ToArray();
        }
        finally
        {
            if (closeConnection) await connection.CloseAsync();
        }
    }

    private static async Task SeedBrokenLedgerFixtureAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var site = new Site(GuidFor(1_500), "C1", "C1 site", "UTC", EvaluationInstant);
        var group = new AgentGroup(GuidFor(1_501), "C1 group", null, EvaluationInstant);
        var device = new Device(Guid.Parse(C1BrokenLedgerDeviceId), site.Id, C1HostileMarker, "10.60.0.1", null, "Router", null, null, Criticality.Normal, ["c1"], EvaluationInstant);
        var targetProbe = new Probe(GuidFor(1_601), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
        var agent = new EePulse.Domain.Agents.Agent(GuidFor(1_701), group.Id, GuidFor(1_702), "c1-watermark-agent", "1.0", 20, EvaluationInstant);
        var result = GuidFor(1_801);
        db.AddRange(site, group, device, targetProbe, agent,
            new ProbeStatusProjection(targetProbe.Id, ProbeStatus.Up, 0, 0, EvaluationInstant.AddMinutes(-1), EvaluationInstant.AddMinutes(-1), agent.Id, result),
            Ledger(agent.Id, GuidFor(1_899), targetProbe.Id, EvaluationInstant.AddDays(1)));
        // The ledger identity is immutable AgentId+ResultId, so PostgreSQL cannot persist the same identity for another Probe.
        // This non-watermark row proves that a same-Probe ledger cannot repair the absent exact watermark identity.
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedMismatchedWatermarkFixtureAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var site = new Site(GuidFor(1_520), "C3", "C3 site", "UTC", EvaluationInstant);
        var group = new AgentGroup(GuidFor(1_521), "C3 group", null, EvaluationInstant);
        var device = new Device(Guid.Parse(C3MismatchedWatermarkDeviceId), site.Id, C3HostileMarker, "10.60.0.4", null, "Router", null, null, Criticality.Normal, ["c3"], EvaluationInstant);
        var probe = new Probe(Guid.Parse(C3MismatchedWatermarkProbeId), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
        var agent = new EePulse.Domain.Agents.Agent(GuidFor(1_720), group.Id, GuidFor(1_721), "c3-private-watermark-agent", "1.0", 20, EvaluationInstant);
        var resultId = GuidFor(1_820);
        var watermarkAt = EvaluationInstant.AddSeconds(2);
        var ledgerEndedAt = EvaluationInstant.AddSeconds(3);
        var ledgerReceivedAt = EvaluationInstant.AddSeconds(1);
        db.AddRange(site, group, device, probe, agent,
            new ProbeStatusProjection(probe.Id, ProbeStatus.Up, 0, 0, watermarkAt, watermarkAt, agent.Id, resultId),
            Ledger(agent.Id, resultId, probe.Id, ledgerReceivedAt, ledgerEndedAt));
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedMissingAgentAndDuplicateTagsFixtureAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var site = new Site(GuidFor(1_510), "C1B", "C1B site", "UTC", EvaluationInstant);
        var group = new AgentGroup(GuidFor(1_511), "C1B group", null, EvaluationInstant);
        var missingAgentDevice = new Device(Guid.Parse(C1MissingAgentDeviceId), site.Id, "C1 missing agent", "10.60.0.2", null, "Router", null, null, Criticality.Normal, ["c1"], EvaluationInstant);
        var duplicateTagsDevice = new Device(Guid.Parse(C1DuplicateTagsDeviceId), site.Id, C1HostileMarker, "10.60.0.3", null, "Router", null, null, Criticality.Normal, ["c1"], EvaluationInstant);
        var probe = new Probe(GuidFor(1_610), missingAgentDevice.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
        var agent = new EePulse.Domain.Agents.Agent(GuidFor(1_710), group.Id, GuidFor(1_711), "c1-missing-agent", "1.0", 20, EvaluationInstant);
        var result = GuidFor(1_810);
        db.AddRange(site, group, missingAgentDevice, duplicateTagsDevice, probe, agent,
            new ProbeStatusProjection(probe.Id, ProbeStatus.Up, 0, 0, EvaluationInstant.AddMinutes(-1), EvaluationInstant.AddMinutes(-1), agent.Id, result),
            Ledger(agent.Id, result, probe.Id, EvaluationInstant));
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync("UPDATE devices SET tags = '[\"c1-hostile-device-data\",\"c1-hostile-device-data\"]'::jsonb WHERE id = {0}", [duplicateTagsDevice.Id], ct);

        // This is an isolated Testcontainers-only physical-corruption fixture. Constraints are restored before disposal;
        // it exercises the left join's missing-agent branch without changing production constraints or schema.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM agents WHERE id = {agent.Id}", ct);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = origin", ct);
            await db.Database.CloseConnectionAsync();
        }
    }

    private static WebApplicationFactory<Program> CreateFixedClockFactory(string connectionString, DateTimeOffset now, params IInterceptor[] interceptors) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUtcClock>();
            services.AddSingleton<IUtcClock>(new FixedClock(now));
            services.RemoveAll<DbContextOptions<EePulseDbContext>>();
            services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
        });
    });

    private static WebApplicationFactory<Program> CreateFailureFactory(string connectionString, DeviceStatusLogCaptureSink sink, params IInterceptor[] interceptors) =>
        CreateFailureFactory(connectionString, sink, null, interceptors);

    private static WebApplicationFactory<Program> CreateFailureFactory(string connectionString, DeviceStatusLogCaptureSink sink,
        Action<IServiceCollection>? configureServices, params IInterceptor[] interceptors) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.ConfigureTestServices(services =>
            {
                configureServices?.Invoke(services);
                services.RemoveAll<DbContextOptions<EePulseDbContext>>();
                services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
                services.RemoveAll<ILoggerFactory>();
                services.RemoveAll<ILoggerProvider>();
                services.RemoveAll<Serilog.ILogger>();
                services.AddSingleton<Serilog.ILogger>(_ => new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger());
                services.AddSingleton<ILoggerProvider>(provider => new SerilogLoggerProvider(
                    provider.GetRequiredService<Serilog.ILogger>(), dispose: false));
                services.AddSingleton<ILoggerFactory>(provider => new LoggerFactory([provider.GetRequiredService<ILoggerProvider>()]));
            });
        });

    private static async Task<string> AssertUnavailableAsync(HttpResponseMessage response, DeviceStatusLogCaptureSink logs, CancellationToken ct,
        params string[] additionalMarkers)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var correlation = AssertSafeCorrelation(response);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(Wp07DashboardContract.DeviceStatusUnavailableCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(correlation, problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(Wp07DashboardContract.DeviceStatusUnavailableCode, problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(Wp07DashboardContract.DeviceStatusUnavailableStatusCode, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("The device status is unavailable.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.6.4", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(SafeProblemPropertyNames, problem.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        var unavailableEvents = logs.Entries.Where(entry => entry.EventId.Id == 7002).ToArray();
        Assert.Single(unavailableEvents);
        var entry = unavailableEvents[0];
        Assert.Equal("EePulse.Api.Dashboard.DeviceStatusEndpoints", entry.Category);
        Assert.Contains(entry.State, pair => pair.Key == "CorrelationId" && string.Equals(pair.Value, correlation, StringComparison.Ordinal));
        Assert.Equal("LogDeviceStatusUnavailable", entry.EventId.Name);
        Assert.Null(entry.Exception);
        Assert.Equal($"Device status request \"{correlation}\" failed.", entry.Message);
        var safeProperties = new HashSet<string>(["SourceContext", "EventId", "CorrelationId", "{OriginalFormat}"], StringComparer.Ordinal);
        Assert.All(entry.State, property => Assert.Contains(property.Key, safeProperties));
        var telemetry = entry.Message + " " + string.Join(" ", entry.State.Select(pair => pair.Value));
        var responseHeaders = response.Headers
            .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value)))
            .Concat(response.Content.Headers.SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value))))
            .ToArray();
        foreach (var marker in new[] { C1HostileMarker, C2HostileMarker, CallerCorrelation, "11111111111111111111111111111111", "c1=hostile-trace-state", "c2=hostile-trace-state", "Npgsql", "SELECT", "Exception", "stack", "traceparent", "tracestate", "token", "credential", DeviceId }.Concat(additionalMarkers))
        {
            Assert.DoesNotContain(marker, telemetry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
            Assert.All(responseHeaders, header => Assert.DoesNotContain(marker, header.Key + "=" + header.Value, StringComparison.OrdinalIgnoreCase));
        }
        return body;
    }

    private static void AssertExactWatermarkSql(DeviceStatusCommandObserver commands)
    {
        Predicate<string> isLedgerQuery = command => command.Contains("probe_result_ledger", StringComparison.OrdinalIgnoreCase);
        var ledgerSql = Assert.Single(commands.Commands, isLedgerQuery);
        var normalizedSql = Regex.Replace(ledgerSql, @"\s+", " ");
        Assert.Matches(
            """ON \(?[\w".]+\."?watermark_agent_id"? = [\w".]+\."?agent_id"? AND [\w".]+\."?watermark_result_id"? = [\w".]+\."?result_id"?\)? AND [\w".]+\."?id"? = [\w".]+\."?probe_id"?""",
            normalizedSql);

        var ledgerJoin = Regex.Match(normalizedSql,
            """LEFT JOIN (?:[\w".]+\.)?"?probe_result_ledger"? AS (?<alias>"?[\w]+"?) ON""",
            RegexOptions.IgnoreCase);
        Assert.True(ledgerJoin.Success, normalizedSql);
        var from = Regex.Match(normalizedSql, @"\sFROM\s", RegexOptions.IgnoreCase);
        Assert.True(from.Success, normalizedSql);
        var selectedColumns = normalizedSql[..from.Index];
        var ledgerAlias = ledgerJoin.Groups["alias"].Value.Trim('"');
        Assert.Matches(new Regex(@"\b" + Regex.Escape(ledgerAlias) + @"\s*\.\s*""?ended_at""?", RegexOptions.IgnoreCase), selectedColumns);
    }

    private static StatusEnrichedDeviceResponse PopulatedExpectedResponse() => new(
        PopulatedDeviceId, PopulatedSiteId, "Populated device", "10.30.40.50", "populated.example", "Firewall", "Core", "Network Operations", "High",
        ["alpha", "zeta"], true, 1,
        [
            new DeviceStatusResponse(PopulatedProbeUpId, DashboardProbeStatus.Up, DashboardProbeStatus.Up, 11,
                EvaluationInstant.AddSeconds(2), EvaluationInstant.AddSeconds(1), GuidFor(601).ToString("D"), "watermark-up", GuidFor(701).ToString("D")),
            ExpectedProbe(PopulatedProbeDegradedId, DashboardProbeStatus.Degraded, 12, 2, "watermark-degraded", 2),
            ExpectedProbe(PopulatedProbeDownId, DashboardProbeStatus.Down, 13, 3, "watermark-down", 3),
            ExpectedProbe(PopulatedProbeRecoveringId, DashboardProbeStatus.Recovering, 14, 4, "watermark-recovering", 4),
            ExpectedProbe(PopulatedProbeUnknownId, DashboardProbeStatus.Unknown, 15, 5, "watermark-unknown", 5),
            new DeviceStatusResponse(PopulatedProbeMissingId, DashboardProbeStatus.Unknown, DashboardProbeStatus.Unknown, 0, null, null, null, null, null)
        ]);

    private static DeviceStatusResponse ExpectedProbe(string probeId, DashboardProbeStatus status, long version, int offset, string agentName, int suffix) => new(
        probeId, status, status, version, EvaluationInstant.AddMinutes(-offset), EvaluationInstant.AddSeconds(offset),
        GuidFor(600 + suffix).ToString("D"), agentName, GuidFor(700 + suffix).ToString("D"));

    private static async Task SeedPopulatedProjectionFixtureAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var site = new Site(Guid.Parse(PopulatedSiteId), "POP", "Populated site", "UTC", EvaluationInstant);
        var group = new AgentGroup(GuidFor(500), "Populated group", null, EvaluationInstant);
        var device = new Device(Guid.Parse(PopulatedDeviceId), site.Id, "Populated device", "10.30.40.50", "populated.example", "Firewall", "Core",
            "Network Operations", Criticality.High, ["zeta", "Alpha", "alpha"], EvaluationInstant);
        db.AddRange(site, group, device);
        var distractorAgentId = GuidFor(998);
        db.Add(new EePulse.Domain.Agents.Agent(
            distractorAgentId,
            group.Id,
            GuidFor(999),
            "distractor-agent",
            "1.0",
    20,
    EvaluationInstant));

        var definitions = new (string ProbeText, ProbeStatus? Status, int Offset, string AgentName)[]
        {
            (PopulatedProbeMissingId, (ProbeStatus?)null, 0, ""),
            (PopulatedProbeRecoveringId, (ProbeStatus?)ProbeStatus.Recovering, 4, "watermark-recovering"),
            (PopulatedProbeUpId, (ProbeStatus?)ProbeStatus.Up, 1, "watermark-up"),
            (PopulatedProbeUnknownId, (ProbeStatus?)ProbeStatus.Unknown, 5, "watermark-unknown"),
            (PopulatedProbeDownId, (ProbeStatus?)ProbeStatus.Down, 3, "watermark-down"),
            (PopulatedProbeDegradedId, (ProbeStatus?)ProbeStatus.Degraded, 2, "watermark-degraded")
        };

        foreach (var definition in definitions)
        {
            var probe = new Probe(Guid.Parse(definition.ProbeText), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
            db.Add(probe);
            if (definition.Status is null) continue;
            var agentId = GuidFor(600 + definition.Offset);
            var resultId = GuidFor(800 + definition.Offset);
            var agent = new EePulse.Domain.Agents.Agent(agentId, group.Id, GuidFor(900 + definition.Offset), definition.AgentName, "1.0", 20, EvaluationInstant);
            var failures = definition.Status == ProbeStatus.Down ? 1 : 0;
            var successes = definition.Status == ProbeStatus.Recovering ? 1 : 0;
            var incidentId = GuidFor(700 + definition.Offset);
            var projectionEventAt = definition.Status == ProbeStatus.Up
                ? EvaluationInstant.AddSeconds(2)
                : EvaluationInstant.AddMinutes(-definition.Offset);
            var projection = new ProbeStatusProjection(probe.Id, definition.Status.Value, failures, successes, projectionEventAt,
                projectionEventAt, agentId, resultId, incidentId);
            var exactLedger = Ledger(agentId, resultId, probe.Id, EvaluationInstant.AddSeconds(definition.Offset), projectionEventAt);
            db.AddRange(agent, projection, new AvailabilityIncident(incidentId, probe.Id, EvaluationInstant.AddHours(-definition.Offset)), exactLedger);
            if (definition.Status == ProbeStatus.Up)
            {
                db.Add(Ledger(agentId, GuidFor(899), probe.Id, EvaluationInstant.AddDays(-1)));
                db.Add(Ledger(distractorAgentId, resultId, probe.Id, EvaluationInstant.AddDays(1)));
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var definition in definitions.Where(item => item.Status is not null))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET state_version = {10 + definition.Offset} WHERE probe_id = {Guid.Parse(definition.ProbeText)}", ct);
        }
    }

    private static async Task SeedOverlayFixtureAsync(string connectionString, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var siteA = new Site(GuidFor(550), "OVERLAY-A", "Overlay A", "UTC", EvaluationInstant);
        var siteB = new Site(GuidFor(551), "OVERLAY-B", "Overlay B", "UTC", EvaluationInstant);
        var group = new AgentGroup(GuidFor(552), "Overlay group", null, EvaluationInstant);
        db.AddRange(siteA, siteB, group);

        var disabledDevice = Device(OverlayDeviceDisabledId, siteB.Id, "Disabled device");
        disabledDevice.Update(siteB.Id, "Disabled device", "10.40.0.1", null, "Router", null, null, Criticality.Normal, ["overlay"], false, EvaluationInstant);
        var siteDevice = Device(OverlaySiteMaintenanceDeviceId, siteA.Id, "Site maintenance");
        var deviceMaintenance = Device(OverlayDeviceMaintenanceDeviceId, siteB.Id, "Device maintenance");
        var probeMaintenance = Device(OverlayProbeMaintenanceDeviceId, siteB.Id, "Probe maintenance");
        var unrelatedDevice = Device(GuidFor(591).ToString("D"), siteB.Id, "Unrelated maintenance");
        db.AddRange(disabledDevice, siteDevice, deviceMaintenance, probeMaintenance, unrelatedDevice);

        var disabledDeviceProbe = AddProjection(db, disabledDevice, group, OverlayDeviceDisabledProbeId, ProbeStatus.Up);
        var siteProbe = AddProjection(db, siteDevice, group, OverlaySiteMaintenanceProbeId, ProbeStatus.Up);
        var deviceProbe = AddProjection(db, deviceMaintenance, group, OverlayDeviceMaintenanceProbeId, ProbeStatus.Degraded);
        var disabledProbe = AddProjection(db, probeMaintenance, group, OverlayProbeDisabledId, ProbeStatus.Down, false);
        var maintainedProbe = AddProjection(db, probeMaintenance, group, OverlayProbeMaintenanceId, ProbeStatus.Recovering);
        var startInclusiveProbe = AddProjection(db, probeMaintenance, group, OverlayStartInclusiveId, ProbeStatus.Unknown);
        var endExclusiveProbe = AddProjection(db, probeMaintenance, group, OverlayEndExclusiveId, ProbeStatus.Up);
        var expiredProbe = AddProjection(db, probeMaintenance, group, OverlayExpiredId, ProbeStatus.Degraded);
        var futureProbe = AddProjection(db, probeMaintenance, group, OverlayFutureId, ProbeStatus.Down);
        var disabledWindowProbe = AddProjection(db, probeMaintenance, group, OverlayDisabledWindowId, ProbeStatus.Recovering);
        var unrelatedWindowProbe = AddProjection(db, probeMaintenance, group, OverlayUnrelatedWindowId, ProbeStatus.Unknown);
        AddProbeWithoutProjection(db, deviceMaintenance, group, OverlayDisabledMissingProjectionId, false);
        AddProbeWithoutProjection(db, deviceMaintenance, group, OverlayMaintenanceMissingProjectionId);
        var unrelatedProbe = AddProjection(db, unrelatedDevice, group, GuidFor(590).ToString("D"), ProbeStatus.Up);

        db.AddRange(
            Window(560, siteA.Id, null, null, EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1)),
            Window(561, null, disabledDevice.Id, null, EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1)),
            Window(562, null, deviceMaintenance.Id, null, EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1)),
            Window(563, null, null, maintainedProbe.Id, EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1)),
            Window(564, null, null, startInclusiveProbe.Id, EvaluationInstant, EvaluationInstant.AddHours(1)),
            Window(565, null, null, endExclusiveProbe.Id, EvaluationInstant.AddHours(-1), EvaluationInstant),
            Window(566, null, null, expiredProbe.Id, EvaluationInstant.AddHours(-2), EvaluationInstant.AddHours(-1)),
            Window(567, null, null, futureProbe.Id, EvaluationInstant.AddHours(1), EvaluationInstant.AddHours(2)),
            Window(568, null, null, unrelatedProbe.Id, EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1)));
        var disabledWindow = Window(569, null, null, disabledWindowProbe.Id, EvaluationInstant.AddHours(-1), EvaluationInstant.AddHours(1));
        disabledWindow.Update(disabledWindow.Name, disabledWindow.StartsAt, disabledWindow.EndsAt, disabledWindow.Timezone, null, null, disabledWindow.ProbeId, false, EvaluationInstant);
        db.Add(disabledWindow);
        _ = disabledDeviceProbe;
        await db.SaveChangesAsync(ct);
    }

    private static Device Device(string id, Guid siteId, string name) => new(Guid.Parse(id), siteId, name, "10.40.0." + name.Length, null, "Router", null, null, Criticality.Normal, ["overlay"], EvaluationInstant);
    private static Probe AddProbeWithoutProjection(EePulseDbContext db, Device device, AgentGroup group, string probeId, bool enabled = true)
    {
        var probe = new Probe(Guid.Parse(probeId), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
        if (!enabled) probe.Update(group.Id, 30, 1_000, 1, null, null, 1, 1, false);
        db.Probes.Add(probe);
        return probe;
    }

    private static Probe AddProjection(EePulseDbContext db, Device device, AgentGroup group, string probeId, ProbeStatus status, bool enabled = true)
    {
        var probe = new Probe(Guid.Parse(probeId), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
        if (!enabled) probe.Update(group.Id, 30, 1_000, 1, null, null, 1, 1, false);
        db.AddRange(probe, new ProbeStatusProjection(probe.Id, status, status == ProbeStatus.Down ? 1 : 0, status == ProbeStatus.Recovering ? 1 : 0, null, null, null, null));
        return probe;
    }
    private static MaintenanceWindow Window(int id, Guid? siteId, Guid? deviceId, Guid? probeId, DateTimeOffset starts, DateTimeOffset ends) =>
        new(GuidFor(id), "Window " + id, starts, ends, "UTC", siteId, deviceId, probeId, EvaluationInstant);
    private static ProbeResultLedgerEntry Ledger(Guid agentId, Guid resultId, Guid probeId, DateTimeOffset receivedAt) =>
        Ledger(agentId, resultId, probeId, receivedAt, receivedAt);
    private static ProbeResultLedgerEntry Ledger(Guid agentId, Guid resultId, Guid probeId, DateTimeOffset receivedAt, DateTimeOffset endedAt) =>
        new(agentId, resultId, probeId, 1, endedAt.AddSeconds(-1), endedAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], receivedAt);
    private static Guid GuidFor(int value) => Guid.ParseExact(value.ToString("x12", System.Globalization.CultureInfo.InvariantCulture).PadLeft(32, '0'), "N");

    private static async Task AssertOverlayAsync(HttpClient client, string deviceId, CancellationToken ct, params (string ProbeId, DashboardProbeStatus Visible, DashboardProbeStatus Underlying)[] expected)
    {
        using var response = await client.SendAsync(Request(Path(deviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var probes = document.RootElement.GetProperty("probes").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, probes.Length);
        foreach (var expectation in expected)
        {
            var probe = probes.Single(item => item.GetProperty("probeId").GetString() == expectation.ProbeId);
            Assert.Equal(expectation.Visible.ToString(), probe.GetProperty("visibleStatus").GetString());
            Assert.Equal(expectation.Underlying.ToString(), probe.GetProperty("underlyingStatus").GetString());
        }
    }

    private static async Task AssertMissingProjectionOverlayAsync(HttpClient client, string deviceId, string probeId,
        DashboardProbeStatus expectedVisible, CancellationToken ct)
    {
        using var response = await client.SendAsync(Request(Path(deviceId), "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var probes = document.RootElement.GetProperty("probes").EnumerateArray().ToArray();
        Assert.Single(probes, probe => probe.GetProperty("probeId").GetString() == probeId);
        var missingProjection = probes.First(probe => probe.GetProperty("probeId").GetString() == probeId);

        Assert.Equal(expectedVisible.ToString(), missingProjection.GetProperty("visibleStatus").GetString());
        Assert.Equal(nameof(DashboardProbeStatus.Unknown), missingProjection.GetProperty("underlyingStatus").GetString());
        Assert.Equal(0L, missingProjection.GetProperty("stateVersion").GetInt64());
        Assert.Equal(JsonValueKind.Null, missingProjection.GetProperty("lastFreshEventAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingProjection.GetProperty("lastReceivedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingProjection.GetProperty("agentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingProjection.GetProperty("agentName").ValueKind);
        Assert.Equal(JsonValueKind.Null, missingProjection.GetProperty("openIncidentId").ValueKind);
    }

    private sealed class FixedClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RawTargetCaptureStartupFilter(Action<HttpContext> capture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                capture(context);
                await nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class DeviceStatusCommandObserver : DbCommandInterceptor
    {
        private readonly List<string> commands = [];
        public IReadOnlyList<string> Commands { get { lock (commands) return commands.ToArray(); } }
        public void Clear() { lock (commands) commands.Clear(); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (commands) commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ThrowDeviceStatusReadInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("FROM devices", StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromException<InterceptionResult<DbDataReader>>(new InvalidOperationException("c1 provider failure: SELECT devices"))
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private sealed class UnrelatedCancellationReadInterceptor(Func<bool> requestAbortedProbe) : DbCommandInterceptor
    {
        public bool CommandReached { get; private set; }
        public bool RequestAbortedAtBoundary { get; private set; }
        public bool HandlerTokenAtBoundary { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM devices", StringComparison.OrdinalIgnoreCase))
            {
                return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }

            CommandReached = true;
            RequestAbortedAtBoundary = requestAbortedProbe();
            HandlerTokenAtBoundary = cancellationToken.IsCancellationRequested;
            return ValueTask.FromException<InterceptionResult<DbDataReader>>(
                new OperationCanceledException("unrelated provider cancellation"));
        }
    }

    private sealed class CancellationReadBoundaryInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM devices", StringComparison.OrdinalIgnoreCase))
            {
                Reached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class DeviceStatusLogCaptureSink : ILogEventSink, IDisposable
    {
        private readonly ConcurrentQueue<DeviceStatusLogEntry> entries = [];
        public IReadOnlyList<DeviceStatusLogEntry> Entries => entries.ToArray();
        public void Clear()
        {
            while (entries.TryDequeue(out _)) { }
        }

        public void Emit(LogEvent logEvent)
        {
            var eventProperties = logEvent.Properties.TryGetValue("EventId", out var eventIdValue) && eventIdValue is StructureValue eventStructure
                ? eventStructure.Properties.ToDictionary(property => property.Name, property => property.Value)
                : new Dictionary<string, LogEventPropertyValue>(StringComparer.Ordinal);
            var id = eventProperties.TryGetValue("Id", out var idValue) && idValue is ScalarValue { Value: var numericId } &&
                int.TryParse(Convert.ToString(numericId, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
                ? parsedId : 0;
            var name = eventProperties.TryGetValue("Name", out var nameValue) && nameValue is ScalarValue { Value: string text } ? text : null;
            entries.Enqueue(new DeviceStatusLogEntry(
                logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) ? Text(sourceContext) : string.Empty,
                new EventId(id, name), logEvent.Exception, logEvent.RenderMessage(CultureInfo.InvariantCulture),
                logEvent.Properties.Select(property => new KeyValuePair<string, string>(property.Key, Text(property.Value))).ToArray()));
        }

        private static string Text(LogEventPropertyValue value) => value is ScalarValue { Value: string text } ? text : value.ToString().Trim('"');
        public void Dispose() { }
    }

    private sealed record RawDeviceTags(Guid Id, string Json, string JsonType);

    private sealed record DeviceStatusLogEntry(string Category, EventId EventId, Exception? Exception, string Message,
        IReadOnlyList<KeyValuePair<string, string>> State);
}

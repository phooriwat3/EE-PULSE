using System.Net;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EePulse.Application.Time;
using EePulse.Api.Authorization;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using EePulse.Infrastructure.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace EePulse.IntegrationTests;

/// <summary>HTTP-level guardrails for the frozen summary representation.</summary>
public sealed class DashboardSummaryApiTests
{
    private const string Path = Wp07DashboardContract.DashboardSummaryPath;
    private static readonly DateTimeOffset OverlayEvaluationInstant = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthorizationEmptyStateCanonicalEtagAndConditionalGetAreStable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using (var anonymous = await client.GetAsync(Path, ct)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var forbidden = await client.SendAsync(Request(Path, "Guest"), ct)) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var firstRequest = Request(Path, "Viewer");
        using var first = await client.SendAsync(firstRequest, ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(Wp07DashboardCanonicalizer.ContentType, first.Content.Headers.ContentType!.ToString());
        AssertDashboardCacheControl(first);
        var bytes = await first.Content.ReadAsByteArrayAsync(ct);
        var etag = first.Headers.ETag!.Tag!;
        Assert.Equal(etag, Wp07DashboardCanonicalizer.EtagFor(bytes));
        using (var document = JsonDocument.Parse(bytes))
        {
            var counts = document.RootElement.GetProperty("statusCounts").EnumerateArray().ToArray();
            Assert.Equal(7, counts.Length);
            Assert.All(counts, count => Assert.Equal(0, count.GetProperty("count").GetInt64()));
        }

        using var conditionalRequest = Request(Path, "Viewer");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "W/" + etag);
        using var conditional = await client.SendAsync(conditionalRequest, ct);
        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.Empty(await conditional.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(etag, conditional.Headers.ETag!.Tag);
        AssertDashboardCacheControl(conditional);
        Assert.True(Guid.TryParseExact(conditional.Headers.GetValues("X-Correlation-ID").Single(), "N", out _));
    }

    [Fact]
    public async Task MalformedConditionAndFilterReturnSafeProblemDetailsWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        using var request = Request(Path + "?area=%20%20", "Viewer");
        request.Headers.TryAddWithoutValidation("If-None-Match", "not-an-etag");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("invalid-dashboard-filter", problem.RootElement.GetProperty("code").GetString());
        Assert.True(Guid.TryParseExact(problem.RootElement.GetProperty("correlationId").GetString(), "N", out _));
    }

    [Fact]
    public async Task StatusFilterRequiresExactFrozenPascalCaseTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        using (var accepted = await client.SendAsync(Request(Path + "?status=Down", "Viewer"), ct)) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        foreach (var rejected in new[] { "3", "down", "%20Down%20", "999", "+3", "", "Downward" })
        {
            using var response = await client.SendAsync(Request(Path + "?status=" + rejected, "Viewer"), ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("invalid-dashboard-filter", problem.RootElement.GetProperty("code").GetString());
            Assert.True(Guid.TryParseExact(problem.RootElement.GetProperty("correlationId").GetString(), "N", out _));
        }
    }

    [Fact]
    public async Task ReadFailureIsSanitizedAndRouteIsExcludedFromOpenApi()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        using var logs = new DashboardLogCaptureSink();
        await using var factory = CreateFailureFactory(postgres.ConnectionString, logs, new ThrowDashboardReadInterceptor());
        using var client = factory.CreateClient();
        using (var response = await client.SendAsync(Request(Path, "Viewer"), ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(Wp07DashboardContract.DashboardSummaryUnavailableCode, problem.RootElement.GetProperty("code").GetString());
            var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
            Assert.Equal(correlation, problem.RootElement.GetProperty("correlationId").GetString());
            Assert.DoesNotContain("test dashboard provider failure", problem.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.True(Guid.TryParseExact(correlation, "N", out _));
            Predicate<DashboardLogEntry> isDashboardUnavailable = entry => entry.Category == "EePulse.Api.Dashboard.DashboardSummaryEndpoints" && entry.EventId.Id == 7001;
            Assert.Single(logs.Entries, isDashboardUnavailable);
            var entry = logs.Entries.First(candidate => isDashboardUnavailable(candidate));
            Assert.Equal("EePulse.Api.Dashboard.DashboardSummaryEndpoints", entry.Category);
            Assert.Equal(7001, entry.EventId.Id);
            Assert.Equal("LogDashboardSummaryUnavailable", entry.EventId.Name);
            Assert.Equal(correlation, entry.State.Single(pair => pair.Key == "CorrelationId").Value);
            Assert.Null(entry.Exception);
            Assert.Equal($"Dashboard summary request \"{correlation}\" failed.", entry.Message);
            var telemetry = entry.Message + " " + string.Join(" ", entry.State.Select(pair => pair.Value));
            Assert.DoesNotContain("test dashboard provider failure", telemetry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SELECT", telemetry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Npgsql", telemetry, StringComparison.OrdinalIgnoreCase);
        }
        using var openApi = await client.GetAsync("/openapi/v1.json", ct);
        openApi.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync(ct));
        Assert.False(document.RootElement.GetProperty("paths").TryGetProperty(Path, out _));
    }

    [Fact]
    public async Task PopulatedSummaryAppliesOverlaysFiltersAndStableConditionalRepresentation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedOverlayFixtureAsync(postgres.ConnectionString, ct);
        await using var factory = CreateOverlayFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Request(Path, "Viewer"), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var bytes = await first.Content.ReadAsByteArrayAsync(ct);
        var etag = first.Headers.ETag!.Tag!;
        using (var document = JsonDocument.Parse(bytes))
        {
            var counts = document.RootElement.GetProperty("statusCounts").EnumerateArray()
                .ToDictionary(value => value.GetProperty("status").GetString()!, value => value.GetProperty("count").GetInt64(), StringComparer.Ordinal);
            Assert.Equal(7, counts.Count);
            Assert.Equal(1, counts["Unknown"]);
            Assert.Equal(1, counts["Up"]);
            Assert.Equal(1, counts["Degraded"]);
            Assert.Equal(1, counts["Down"]);
            Assert.Equal(1, counts["Recovering"]);
            Assert.Equal(1, counts["Maintenance"]);
            Assert.Equal(2, counts["Disabled"]);
            Assert.Single(document.RootElement.GetProperty("recentlyDown").EnumerateArray());
            Assert.Single(document.RootElement.GetProperty("openIncidents").EnumerateArray());
        }

        using (var repeat = await client.SendAsync(Request(Path, "Viewer"), ct))
        {
            Assert.Equal(bytes, await repeat.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(etag, repeat.Headers.ETag!.Tag);
        }
        using (var conditionalRequest = Request(Path, "Viewer"))
        {
            conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "W/" + etag);
            using var conditional = await client.SendAsync(conditionalRequest, ct);
            Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
            Assert.Empty(await conditional.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(etag, conditional.Headers.ETag!.Tag);
            AssertDashboardCacheControl(conditional);
        }
        using (var tag = await client.SendAsync(Request(Path + "?tag=%20production%20", "Viewer"), ct))
        {
            using var document = JsonDocument.Parse(await tag.Content.ReadAsStringAsync(ct));
            var total = document.RootElement.GetProperty("statusCounts").EnumerateArray().Sum(value => value.GetProperty("count").GetInt64());
            Assert.Equal(1, total);
        }
        using (var status = await client.SendAsync(Request(Path + "?status=Maintenance", "Viewer"), ct))
        {
            using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
            var counts = document.RootElement.GetProperty("statusCounts").EnumerateArray().ToArray();
            Assert.Equal(1, counts.Single(value => value.GetProperty("status").GetString() == "Maintenance").GetProperty("count").GetInt64());
            Assert.Equal(0, counts.Where(value => value.GetProperty("status").GetString() != "Maintenance").Sum(value => value.GetProperty("count").GetInt64()));
        }
    }

    [Fact]
    public async Task PopulatedListsAreSqlLimitedAndCommandCountIsFixed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedCapFixtureAsync(postgres.ConnectionString, ct);
        var observer = new DashboardCommandObserver();
        await using var factory = CreateOverlayFactory(postgres.ConnectionString, observer);
        using var client = factory.CreateClient();
        observer.Clear();
        using var response = await client.SendAsync(Request(Path + "?tag=cap", "Viewer"), ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(20, document.RootElement.GetProperty("recentlyDown").GetArrayLength());
        Assert.Equal(20, document.RootElement.GetProperty("offlineAgents").GetArrayLength());
        Assert.Equal(20, document.RootElement.GetProperty("openIncidents").GetArrayLength());
        Assert.Equal(new string[] { Id(3020).ToString("D", CultureInfo.InvariantCulture), Id(3019).ToString("D", CultureInfo.InvariantCulture), Id(3018).ToString("D", CultureInfo.InvariantCulture), Id(3017).ToString("D", CultureInfo.InvariantCulture), Id(3016).ToString("D", CultureInfo.InvariantCulture), Id(3015).ToString("D", CultureInfo.InvariantCulture), Id(3014).ToString("D", CultureInfo.InvariantCulture), Id(3013).ToString("D", CultureInfo.InvariantCulture), Id(3012).ToString("D", CultureInfo.InvariantCulture), Id(3011).ToString("D", CultureInfo.InvariantCulture), Id(3010).ToString("D", CultureInfo.InvariantCulture), Id(3009).ToString("D", CultureInfo.InvariantCulture), Id(3008).ToString("D", CultureInfo.InvariantCulture), Id(3007).ToString("D", CultureInfo.InvariantCulture), Id(3006).ToString("D", CultureInfo.InvariantCulture), Id(3005).ToString("D", CultureInfo.InvariantCulture), Id(3004).ToString("D", CultureInfo.InvariantCulture), Id(3003).ToString("D", CultureInfo.InvariantCulture), Id(3002).ToString("D", CultureInfo.InvariantCulture), Id(3001).ToString("D", CultureInfo.InvariantCulture) },
            document.RootElement.GetProperty("recentlyDown").EnumerateArray().Select(item => item.GetProperty("probeId").GetString()).ToArray());
        Assert.Equal(new string[] { Id(4001).ToString("D", CultureInfo.InvariantCulture), Id(4002).ToString("D", CultureInfo.InvariantCulture), Id(4003).ToString("D", CultureInfo.InvariantCulture), Id(4004).ToString("D", CultureInfo.InvariantCulture), Id(4005).ToString("D", CultureInfo.InvariantCulture), Id(4006).ToString("D", CultureInfo.InvariantCulture), Id(4007).ToString("D", CultureInfo.InvariantCulture), Id(4008).ToString("D", CultureInfo.InvariantCulture), Id(4009).ToString("D", CultureInfo.InvariantCulture), Id(4010).ToString("D", CultureInfo.InvariantCulture), Id(4011).ToString("D", CultureInfo.InvariantCulture), Id(4012).ToString("D", CultureInfo.InvariantCulture), Id(4013).ToString("D", CultureInfo.InvariantCulture), Id(4014).ToString("D", CultureInfo.InvariantCulture), Id(4015).ToString("D", CultureInfo.InvariantCulture), Id(4016).ToString("D", CultureInfo.InvariantCulture), Id(4017).ToString("D", CultureInfo.InvariantCulture), Id(4018).ToString("D", CultureInfo.InvariantCulture), Id(4019).ToString("D", CultureInfo.InvariantCulture), Id(4020).ToString("D", CultureInfo.InvariantCulture) },
            document.RootElement.GetProperty("offlineAgents").EnumerateArray().Select(item => item.GetProperty("agentId").GetString()).ToArray());
        Assert.Equal(new string[] { Id(6020).ToString("D", CultureInfo.InvariantCulture), Id(6019).ToString("D", CultureInfo.InvariantCulture), Id(6018).ToString("D", CultureInfo.InvariantCulture), Id(6017).ToString("D", CultureInfo.InvariantCulture), Id(6016).ToString("D", CultureInfo.InvariantCulture), Id(6015).ToString("D", CultureInfo.InvariantCulture), Id(6014).ToString("D", CultureInfo.InvariantCulture), Id(6013).ToString("D", CultureInfo.InvariantCulture), Id(6012).ToString("D", CultureInfo.InvariantCulture), Id(6011).ToString("D", CultureInfo.InvariantCulture), Id(6010).ToString("D", CultureInfo.InvariantCulture), Id(6009).ToString("D", CultureInfo.InvariantCulture), Id(6008).ToString("D", CultureInfo.InvariantCulture), Id(6007).ToString("D", CultureInfo.InvariantCulture), Id(6006).ToString("D", CultureInfo.InvariantCulture), Id(6005).ToString("D", CultureInfo.InvariantCulture), Id(6004).ToString("D", CultureInfo.InvariantCulture), Id(6003).ToString("D", CultureInfo.InvariantCulture), Id(6002).ToString("D", CultureInfo.InvariantCulture), Id(6001).ToString("D", CultureInfo.InvariantCulture) },
            document.RootElement.GetProperty("openIncidents").EnumerateArray().Select(item => item.GetProperty("incidentId").GetString()).ToArray());
        var commands = observer.Commands;
        Assert.Equal(5, commands.Count);
        Assert.Contains(commands, command => command.Contains("tags", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, commands.Count(command => command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(commands, command => command.Contains("last_heartbeat_at IS NOT NULL", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PopulatedSummaryAppliesEveryFilterAndExcludesPartialMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await SeedFilterFixtureAsync(postgres.ConnectionString, ct);
        await using var factory = CreateOverlayFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        var siteId = Id(8_000).ToString("D", CultureInfo.InvariantCulture);

        foreach (var query in new[]
        {
            "siteId=" + siteId, "area=%20Ops%20", "deviceType=Router", "criticality=High", "tag=%20prod%20", "status=Down"
        })
        {
            using var individual = await client.SendAsync(Request(Path + "?" + query, "Viewer"), ct);
            individual.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await individual.Content.ReadAsStringAsync(ct));
            Assert.Equal(6L, document.RootElement.GetProperty("statusCounts").EnumerateArray().Sum(item => item.GetProperty("count").GetInt64()));
        }

        const string combined = "?siteId=00000000-0000-0000-0000-000000001f40&area=%20Ops%20&deviceType=Router&criticality=High&tag=%20prod%20&status=Down";
        using var response = await client.SendAsync(Request(Path + combined, "Viewer"), ct);
        response.EnsureSuccessStatusCode();
        using var summary = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var applied = summary.RootElement.GetProperty("appliedFilter");
        Assert.Equal(siteId, applied.GetProperty("siteId").GetString());
        Assert.Equal("Ops", applied.GetProperty("area").GetString());
        Assert.Equal("Router", applied.GetProperty("deviceType").GetString());
        Assert.Equal("High", applied.GetProperty("criticality").GetString());
        Assert.Equal("prod", applied.GetProperty("tag").GetString());
        Assert.Equal("Down", applied.GetProperty("status").GetString());
        var counts = summary.RootElement.GetProperty("statusCounts").EnumerateArray().ToDictionary(item => item.GetProperty("status").GetString()!, item => item.GetProperty("count").GetInt64());
        Assert.Equal(1, counts["Down"]);
        Assert.Equal(0, counts.Where(pair => pair.Key != "Down").Sum(pair => pair.Value));
        Assert.Equal(new string[] { Id(9_000).ToString("D", CultureInfo.InvariantCulture) }, summary.RootElement.GetProperty("recentlyDown").EnumerateArray().Select(item => item.GetProperty("probeId").GetString()).ToArray());
        Assert.Equal(new string[] { Id(10_000).ToString("D", CultureInfo.InvariantCulture) }, summary.RootElement.GetProperty("offlineAgents").EnumerateArray().Select(item => item.GetProperty("agentId").GetString()).ToArray());
        Assert.Equal(new string[] { Id(11_000).ToString("D", CultureInfo.InvariantCulture) }, summary.RootElement.GetProperty("openIncidents").EnumerateArray().Select(item => item.GetProperty("incidentId").GetString()).ToArray());
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString, params IInterceptor[] interceptors) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            if (interceptors.Length == 0) return;
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<EePulseDbContext>>();
                services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
            });
        });

    private static WebApplicationFactory<Program> CreateFailureFactory(string connectionString, DashboardLogCaptureSink sink, params IInterceptor[] interceptors) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<EePulseDbContext>>();
                services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
                services.RemoveAll<ILoggerFactory>();
                services.RemoveAll<ILoggerProvider>();
                services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(
                    new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .WriteTo.Console(new CompactJsonFormatter())
                        .WriteTo.Sink(sink)
                        .CreateLogger(),
                    dispose: true));
                services.AddSingleton<ILoggerFactory>(serviceProvider =>
                    new LoggerFactory([serviceProvider.GetRequiredService<ILoggerProvider>()]));
            });
        });

    private static WebApplicationFactory<Program> CreateOverlayFactory(string connectionString, params IInterceptor[] interceptors) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUtcClock>();
                services.AddSingleton<IUtcClock>(new FixedClock(OverlayEvaluationInstant));
                if (interceptors.Length == 0) return;
                services.RemoveAll<DbContextOptions<EePulseDbContext>>();
                services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
            });
        });

    private static HttpRequestMessage Request(string path, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(DevelopmentAuthenticationHandler.RoleHeader, role);
        request.Headers.TryAddWithoutValidation(DevelopmentAuthenticationHandler.ActorHeader, "dashboard-summary-test");
        return request;
    }

    private static void AssertDashboardCacheControl(HttpResponseMessage response)
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

    private static async Task SeedOverlayFixtureAsync(string connectionString, CancellationToken ct)
    {
        var now = OverlayEvaluationInstant;
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var site = new Site(Id(1), "DASH", "Dashboard", "UTC", now);
        var group = new AgentGroup(Id(2), "Dashboard group", null, now);
        db.AddRange(site, group);
        var definitions = new[]
        {
            (ProbeStatus.Up, false, false, false, "production"), (ProbeStatus.Up, false, true, false, "probe-disabled"),
            (ProbeStatus.Down, false, false, true, "maintenance"), (ProbeStatus.Degraded, false, false, false, "degraded"),
            (ProbeStatus.Down, false, false, false, "down"),
            (ProbeStatus.Recovering, false, false, false, "recovering"), (ProbeStatus.Unknown, true, false, false, "disabled-device"),
            ((ProbeStatus?)null, false, false, false, "missing")
        };
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var device = new Device(Id(100 + index), site.Id, "Device " + index, "10.0.0." + (index + 1), null, "Router", "Area " + index,
                null, Criticality.Normal, [definition.Item5], now);
            if (definition.Item2) device.Update(site.Id, device.Name, device.Address, null, device.DeviceType, device.Area, null, Criticality.Normal, device.Tags, false, now);
            var probe = new Probe(Id(200 + index), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
            if (definition.Item3) probe.Update(group.Id, 30, 1_000, 1, null, null, 1, 1, false);
            db.AddRange(device, probe);
            if (definition.Item4) db.Add(new MaintenanceWindow(Id(300 + index), "Maintenance " + index, now.AddHours(-1), now.AddHours(1), "UTC", null, null, probe.Id, now));
            if (definition.Item1 is ProbeStatus status)
            {
                Guid? incidentId = null;
                if (status == ProbeStatus.Down && !definition.Item4)
                {
                    var incident = new AvailabilityIncident(Id(400 + index), probe.Id, now.AddMinutes(-index));
                    db.Add(incident);
                    incidentId = incident.Id;
                }
                db.Add(new ProbeStatusProjection(probe.Id, status, status == ProbeStatus.Down ? 1 : 0, status == ProbeStatus.Recovering ? 1 : 0,
                    now, null, null, null, incidentId));
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedCapFixtureAsync(string connectionString, CancellationToken ct)
    {
        var now = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var site = new Site(Id(1_000), "CAP", "Cap site", "UTC", now);
        var group = new AgentGroup(Id(1_001), "Cap group", null, now);
        db.AddRange(site, group);
        for (var index = 0; index < 21; index++)
        {
            var device = new Device(Id(2_000 + index), site.Id, "Cap device " + index, "10.1.0." + (index + 1), null, "Router", "Cap", null, Criticality.High, ["cap"], now);
            var probe = new Probe(Id(3_000 + index), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
            var agent = new EePulse.Domain.Agents.Agent(Id(4_000 + index), group.Id, Id(5_000 + index), "cap-agent-" + index, "1.0", 20, now.AddHours(-2));
            var heartbeat = now.AddHours(-2);
            agent.Heartbeat("1.0", agent.MachineName, 0, AgentSelfHealth.Healthy, 0, heartbeat, heartbeat);
            agent.MarkOffline(now);
            var incident = new AvailabilityIncident(Id(6_000 + index), probe.Id, now.AddMinutes(-1));
            db.AddRange(device, probe, agent, incident,
                new ProbeStatusProjection(probe.Id, ProbeStatus.Down, 1, 0, now, now, agent.Id, Id(7_000 + index), incident.Id));
        }
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agents SET last_heartbeat_at = NULL, last_reported_at = NULL WHERE id <> {Id(4_000)}", ct);
    }

    private static async Task SeedFilterFixtureAsync(string connectionString, CancellationToken ct)
    {
        var now = OverlayEvaluationInstant;
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EePulseDbContext(options);
        await db.Database.MigrateAsync(ct);
        var targetSite = new Site(Id(8_000), "FILTER", "Filter site", "UTC", now);
        var otherSite = new Site(Id(8_001), "OTHER", "Other site", "UTC", now);
        var group = new AgentGroup(Id(8_100), "Filter group", null, now);
        db.AddRange(targetSite, otherSite, group);
        var definitions = new[]
        {
            (targetSite, "Ops", "Router", Criticality.High, "prod", ProbeStatus.Down),
            (otherSite, "Ops", "Router", Criticality.High, "prod", ProbeStatus.Down),
            (targetSite, "Other", "Router", Criticality.High, "prod", ProbeStatus.Down),
            (targetSite, "Ops", "Switch", Criticality.High, "prod", ProbeStatus.Down),
            (targetSite, "Ops", "Router", Criticality.Normal, "prod", ProbeStatus.Down),
            (targetSite, "Ops", "Router", Criticality.High, "other", ProbeStatus.Down),
            (targetSite, "Ops", "Router", Criticality.High, "prod", ProbeStatus.Up)
        };
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var device = new Device(Id(8_500 + index), definition.Item1.Id, "Filter device " + index, "10.2.0." + (index + 1), null,
                definition.Item3, definition.Item2, null, definition.Item4, [definition.Item5], now);
            var probe = new Probe(Id(9_000 + index), device.Id, group.Id, 30, 1_000, 1, null, null, 1, 1);
            var agent = new EePulse.Domain.Agents.Agent(Id(10_000 + index), group.Id, Id(10_500 + index), "filter-agent-" + index, "1.0", 20, now.AddHours(-2));
            var heartbeat = now.AddHours(-2);
            agent.Heartbeat("1.0", agent.MachineName, 0, AgentSelfHealth.Healthy, 0, heartbeat, heartbeat);
            agent.MarkOffline(now);
            var incident = new AvailabilityIncident(Id(11_000 + index), probe.Id, now.AddMinutes(-1));
            db.AddRange(device, probe, agent, incident,
                new ProbeStatusProjection(probe.Id, definition.Item6, definition.Item6 == ProbeStatus.Down ? 1 : 0, 0,
                    now, now, agent.Id, Id(11_500 + index), incident.Id));
        }
        await db.SaveChangesAsync(ct);
    }

    private static Guid Id(int value) => Guid.ParseExact(value.ToString("x12", CultureInfo.InvariantCulture).PadLeft(32, '0'), "N");

    private sealed class FixedClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ThrowDashboardReadInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("FROM probes", StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromException<InterceptionResult<DbDataReader>>(new InvalidOperationException("test dashboard provider failure"))
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private sealed class DashboardCommandObserver : DbCommandInterceptor
    {
        private readonly List<string> commands = [];
        public IReadOnlyList<string> Commands { get { lock (commands) return commands.ToArray(); } }
        public void Clear() { lock (commands) commands.Clear(); }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { lock (commands) commands.Add(command.CommandText); return base.ReaderExecuting(command, eventData, result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            { lock (commands) commands.Add(command.CommandText); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
    }

    private sealed class DashboardLogCaptureSink : ILogEventSink, IDisposable
    {
        private readonly ConcurrentQueue<DashboardLogEntry> entries = [];
        public IReadOnlyList<DashboardLogEntry> Entries => entries.ToArray();
        public void Emit(LogEvent logEvent)
        {
            var eventId = logEvent.Properties.TryGetValue("EventId", out var eventIdValue) && eventIdValue is StructureValue eventIdStructure
                ? eventIdStructure.Properties.ToDictionary(pair => pair.Name, pair => pair.Value)
                : new Dictionary<string, LogEventPropertyValue>(StringComparer.Ordinal);
            var id = eventId.TryGetValue("Id", out var idValue) && idValue is ScalarValue { Value: var numericId } &&
                int.TryParse(Convert.ToString(numericId, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
                ? parsedId
                : 0;
            var name = eventId.TryGetValue("Name", out var nameValue) && nameValue is ScalarValue { Value: string eventName }
                ? eventName
                : null;
            var state = logEvent.Properties
                .Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value.ToString().Trim('"')))
                .ToArray();
            var entry = new DashboardLogEntry(
                logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
                    ? PropertyText(sourceContext)
                    : string.Empty,
                new EventId(id, name),
                logEvent.Exception,
                logEvent.RenderMessage(CultureInfo.InvariantCulture),
                state);
            entries.Enqueue(entry);
        }

        private static string PropertyText(LogEventPropertyValue value) => value is ScalarValue { Value: string text }
            ? text
            : value.ToString().Trim('"');

        public void Dispose() { }
    }

    private sealed record DashboardLogEntry(string Category, EventId EventId, Exception? Exception, string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State);
}

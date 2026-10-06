using System.Net;
using System.Net.Http.Json;
using System.Text;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.IntegrationTests;

public sealed class DevelopmentDeviceImportReadinessTests
{
    private static readonly Guid ActorId = Guid.Parse("e6d36dfa-69d2-4961-bc2d-a12bb0b71673");
    private const string Header = "siteCode,name,address,hostname,deviceType,area,owner,criticality,tags\n";

    [Fact]
    public async Task ReviewedSyntheticRowsCanBeImportedAndDeferredRowsCorrectedWithoutCreatingProbesOrStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>()
            .WithIncidentCursorKeyRing(IncidentCursorKeyRingTestHost.CreateRing())
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
            });
        using var client = factory.CreateClient();

        _ = await Send<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", "Administrator",
            new CreateSiteRequest("LAB", "Synthetic lab", "UTC"), HttpStatusCode.Created, ct);
        var firstCsv = Header +
            "LAB,Reviewed router,192.0.2.10,,Router,,,High,reviewed\n" +
            "LAB,Type deferred,192.0.2.11,,,,,Normal,\n" +
            "LAB,Criticality deferred,198.51.100.20,,Switch,,,,\n";
        var first = await Preview(client, firstCsv, ct);
        Assert.Equal(3, first.TotalRows);
        Assert.Equal(1, first.ValidRows);
        Assert.Equal(2, first.InvalidRows);
        Assert.Equal("Router", first.Rows[0].Normalized?.DeviceType);
        Assert.Equal("High", first.Rows[0].Normalized?.Criticality);
        Assert.Contains(first.Rows[1].Errors, error => error.Field == "deviceType" && error.Code == "validation");
        Assert.Null(first.Rows[1].Normalized);
        Assert.Contains(first.Rows[2].Errors, error => error.Field == "criticality" && error.Code == "validation");
        Assert.Null(first.Rows[2].Normalized);

        var initialCommit = await Send<CsvImportCommitResponse>(client, HttpMethod.Post, "/api/v1/devices/import/commit",
            "Engineer", new CsvImportCommitRequest(first.PreviewToken), HttpStatusCode.OK, ct);
        Assert.Equal(1, initialCommit.Created);
        Assert.Equal(2, initialCommit.Skipped);
        Assert.Equal(2, initialCommit.Errors.Count);
        var reviewedId = Guid.Parse(Assert.Single(initialCommit.DeviceIds));
        await AssertImportState(factory, [reviewedId], 1, ct);

        // The second batch retains the already imported row to prove the Site/address duplicate guard.
        var correctedCsv = Header +
            "LAB,Reviewed router,192.0.2.10,,Router,,,High,reviewed\n" +
            "LAB,Type deferred,192.0.2.11,,Firewall,,,Normal,\n" +
            "LAB,Criticality deferred,198.51.100.20,,Switch,,,Critical,\n";
        var corrected = await Preview(client, correctedCsv, ct);
        Assert.Equal(2, corrected.ValidRows);
        Assert.Equal(1, corrected.InvalidRows);
        Assert.Contains(corrected.Rows[0].Errors, error => error.Code == "duplicate_site_address");
        Assert.Equal("Firewall", corrected.Rows[1].Normalized?.DeviceType);
        Assert.Equal("Normal", corrected.Rows[1].Normalized?.Criticality);
        Assert.Equal("Switch", corrected.Rows[2].Normalized?.DeviceType);
        Assert.Equal("Critical", corrected.Rows[2].Normalized?.Criticality);
        var correctionCommit = await Send<CsvImportCommitResponse>(client, HttpMethod.Post, "/api/v1/devices/import/commit",
            "Engineer", new CsvImportCommitRequest(corrected.PreviewToken), HttpStatusCode.OK, ct);
        Assert.Equal(2, correctionCommit.Created);
        Assert.Equal(1, correctionCommit.Skipped);
        var allIds = correctionCommit.DeviceIds.Select(Guid.Parse).Append(reviewedId).ToArray();
        Assert.Equal(3, allIds.Distinct().Count());
        await AssertImportState(factory, allIds, 3, ct);
        await using (var verified = factory.Services.CreateAsyncScope())
        {
            var verifyDb = verified.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var imported = await verifyDb.Devices.AsNoTracking().ToDictionaryAsync(device => device.Address, ct);
            Assert.Equal(("Router", "High"), (imported["192.0.2.10"].DeviceType, imported["192.0.2.10"].Criticality.ToString()));
            Assert.Equal(("Firewall", "Normal"), (imported["192.0.2.11"].DeviceType, imported["192.0.2.11"].Criticality.ToString()));
            Assert.Equal(("Switch", "Critical"), (imported["198.51.100.20"].DeviceType, imported["198.51.100.20"].Criticality.ToString()));
        }

        var group = await Send<AgentGroupResponse>(client, HttpMethod.Post, "/api/v1/agent-groups", "Administrator",
            new CreateAgentGroupRequest("Synthetic agents", null), HttpStatusCode.Created, ct);
        _ = await Send<AgentNetworkPolicyResponse>(client, HttpMethod.Put,
            $"/api/v1/agent-groups/{group.Id}/allowed-networks", "Administrator",
            new UpdateAgentGroupAllowedNetworksRequest(1, ["192.0.2.0/24"], group.RowVersion), HttpStatusCode.OK, ct);

        var probeRequest = new CreateProbeRequest(reviewedId.ToString(), group.Id, 30, 2_000, 3, null, null, 1, 1);
        using (var viewer = Request(HttpMethod.Post, "/api/v1/probes", "Viewer", JsonContent.Create(probeRequest)))
        using (var denied = await client.SendAsync(viewer, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var outsideId = (await FindDeviceIdByAddress(factory, "198.51.100.20", ct)).ToString();
        Assert.Contains(outsideId, correctionCommit.DeviceIds);
        using (var outside = Request(HttpMethod.Post, "/api/v1/probes", "Engineer",
                   JsonContent.Create(probeRequest with { DeviceId = outsideId })))
        using (var denied = await client.SendAsync(outside, ct))
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await AssertImportState(factory, allIds, 3, ct);

        var probe = await Send<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", "Engineer",
            probeRequest, HttpStatusCode.Created, ct);
        Assert.Equal(reviewedId.ToString(), probe.DeviceId);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Single(await db.Probes.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.ProbeStatusProjections.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.ProbeResultLedgerEntries.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.Agents.AsNoTracking().ToListAsync(ct));
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(x => x.Action == "inventory.probe.created").ToListAsync(ct));
    }

    private static async Task AssertImportState(WebApplicationFactory<Program> factory, Guid[] ids, int count, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        Assert.Equal(count, devices.Count);
        Assert.Equal(ids.Order(), devices.Select(device => device.Id).Order());
        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.DeviceType));
            Assert.True(Enum.IsDefined(device.Criticality));
        });
        var importEvents = await db.AuditEvents.AsNoTracking()
            .Where(x => x.Action == "inventory.device.imported" && x.ActorId == ActorId).ToListAsync(ct);
        Assert.Equal(count, importEvents.Count);
        Assert.Equal(ids.Order(), importEvents.Select(audit => Assert.IsType<Guid>(audit.EntityId)).Order());
        Assert.Empty(await db.Probes.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.ProbeStatusProjections.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.ProbeResultLedgerEntries.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await db.Agents.AsNoTracking().ToListAsync(ct));
    }

    private static async Task<Guid> FindDeviceIdByAddress(WebApplicationFactory<Program> factory, string address, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EePulseDbContext>().Devices.AsNoTracking()
            .Where(device => device.Address == address).Select(device => device.Id).SingleAsync(ct);
    }

    private static async Task<CsvImportPreviewResponse> Preview(HttpClient client, string csv, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "/api/v1/devices/import/preview", "Engineer",
            new StringContent(csv, Encoding.UTF8, "text/csv"));
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CsvImportPreviewResponse>(ct))!;
    }

    private static async Task<T> Send<T>(HttpClient client, HttpMethod method, string path, string role,
        object body, HttpStatusCode expected, CancellationToken ct)
    {
        using var request = Request(method, path, role, JsonContent.Create(body));
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string role, HttpContent content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-EE-Pulse-Role", role);
        request.Headers.Add("X-EE-Pulse-Actor", ActorId.ToString());
        return request;
    }
}

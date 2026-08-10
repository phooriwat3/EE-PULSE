using System.Net;
using System.Net.Http.Json;
using System.Text;
using EePulse.Api.Inventory;
using EePulse.Contracts.Inventory;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.IntegrationTests;

public sealed class InventoryApiTests
{
    private static readonly Guid ActorId = Guid.Parse("2f8190f2-30ec-4bac-b4ab-79f671e561b4");

    [Fact]
    public async Task InventoryCrudAuthorizationPaginationSearchCsvAndAuditWorkEndToEnd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(cancellationToken);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        using var client = factory.CreateClient();

        var readiness = await client.GetAsync("/health/ready", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);

        var anonymous = await client.GetAsync("/api/v1/devices", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var malformedActor = Request(HttpMethod.Post, "/api/v1/sites", "Administrator", null,
            JsonContent.Create(new CreateSiteRequest("BKK-01", "Bangkok", "Asia/Bangkok")));
        var malformedActorResponse = await client.SendAsync(malformedActor, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, malformedActorResponse.StatusCode);

        var site = await SendJson<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", "Administrator",
            new CreateSiteRequest("BKK-01", "Bangkok", "Asia/Bangkok"), HttpStatusCode.Created, cancellationToken);
        var group = await SendJson<AgentGroupResponse>(client, HttpMethod.Post, "/api/v1/agent-groups", "Administrator",
            new CreateAgentGroupRequest("Bangkok Agents", null), HttpStatusCode.Created, cancellationToken);

        var first = await CreateDevice(client, site.Id, "Same name", "192.168.1.10", cancellationToken);
        var second = await CreateDevice(client, site.Id, "Same name", "192.168.1.11", cancellationToken);
        var third = await CreateDevice(client, site.Id, "Same name", "192.168.1.12", cancellationToken);
        Assert.Equal(first.Hostname, second.Hostname);
        Assert.Equal(second.Hostname, third.Hostname);

        var pageOne = await SendGet<PagedResponse<DeviceResponse>>(
            client, "/api/v1/devices?page=1&pageSize=2", "Viewer", cancellationToken);
        var pageTwo = await SendGet<PagedResponse<DeviceResponse>>(
            client, "/api/v1/devices?page=2&pageSize=2", "Viewer", cancellationToken);
        Assert.Equal(3, pageOne.TotalCount);
        Assert.Equal(2, pageOne.Items.Count);
        Assert.Single(pageTwo.Items);
        Assert.Empty(pageOne.Items.Select(item => item.Id).Intersect(pageTwo.Items.Select(item => item.Id)));

        var addressSearch = await SendGet<PagedResponse<DeviceResponse>>(
            client, "/api/v1/devices?search=192.168.1.11", "Viewer", cancellationToken);
        Assert.Single(addressSearch.Items);
        Assert.Equal(second.Id, addressSearch.Items[0].Id);

        var duplicate = new CreateDeviceRequest(site.Id, "Duplicate", first.Address, null, "PLC", null, null, "Normal", []);
        using (var duplicateRequest = Request(HttpMethod.Post, "/api/v1/devices", "Engineer", ActorId, JsonContent.Create(duplicate)))
        {
            var duplicateResponse = await client.SendAsync(duplicateRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
            Assert.Equal("application/problem+json", duplicateResponse.Content.Headers.ContentType?.MediaType);
        }

        var update = new UpdateDeviceRequest(first.SiteId, "Updated", first.Address, first.Hostname, first.DeviceType,
            first.Area, first.Owner, first.Criticality, first.Tags, true, first.RowVersion);
        var updated = await SendJson<DeviceResponse>(client, HttpMethod.Put, $"/api/v1/devices/{first.Id}", "Engineer",
            update, HttpStatusCode.OK, cancellationToken);
        Assert.True(updated.RowVersion > first.RowVersion);
        using (var staleRequest = Request(HttpMethod.Put, $"/api/v1/devices/{first.Id}", "Engineer", ActorId,
                   JsonContent.Create(update with { Name = "Stale" })))
        {
            var stale = await client.SendAsync(staleRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        var disabledFirst = await SendJson<DeviceResponse>(client, HttpMethod.Put, $"/api/v1/devices/{first.Id}", "Engineer",
            new UpdateDeviceRequest(updated.SiteId, updated.Name, updated.Address, updated.Hostname, updated.DeviceType,
                updated.Area, updated.Owner, updated.Criticality, updated.Tags, false, updated.RowVersion),
            HttpStatusCode.OK, cancellationToken);
        Assert.False(disabledFirst.Enabled);
        var replacement = await CreateDevice(client, site.Id, "Replacement", first.Address, cancellationToken);
        Assert.Equal(first.Address, replacement.Address);

        using (var reenableRequest = Request(HttpMethod.Put, $"/api/v1/devices/{first.Id}", "Engineer", ActorId,
                   JsonContent.Create(new UpdateDeviceRequest(disabledFirst.SiteId, disabledFirst.Name, disabledFirst.Address,
                       disabledFirst.Hostname, disabledFirst.DeviceType, disabledFirst.Area, disabledFirst.Owner,
                       disabledFirst.Criticality, disabledFirst.Tags, true, disabledFirst.RowVersion))))
        {
            var reenable = await client.SendAsync(reenableRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, reenable.StatusCode);
        }

        var disabledSecond = await SendJson<DeviceResponse>(client, HttpMethod.Put, $"/api/v1/devices/{second.Id}", "Engineer",
            new UpdateDeviceRequest(second.SiteId, second.Name, second.Address, second.Hostname, second.DeviceType,
                second.Area, second.Owner, second.Criticality, second.Tags, false, second.RowVersion),
            HttpStatusCode.OK, cancellationToken);
        Assert.False(disabledSecond.Enabled);

        var otherSite = await SendJson<SiteResponse>(client, HttpMethod.Post, "/api/v1/sites", "Administrator",
            new CreateSiteRequest("CNX-01", "Chiang Mai", "Asia/Bangkok"), HttpStatusCode.Created, cancellationToken);
        var crossSite = await CreateDevice(client, otherSite.Id, "Cross-site reuse", third.Address, cancellationToken);
        Assert.Equal(third.Address, crossSite.Address);

        var concurrentBody = new CreateDeviceRequest(site.Id, "Concurrent", "192.168.1.99", null, "PLC",
            null, null, "Normal", []);
        using (var concurrentRequestA = Request(HttpMethod.Post, "/api/v1/devices", "Engineer", ActorId,
                   JsonContent.Create(concurrentBody with { Name = "Concurrent A" })))
        using (var concurrentRequestB = Request(HttpMethod.Post, "/api/v1/devices", "Engineer", ActorId,
                   JsonContent.Create(concurrentBody with { Name = "Concurrent B" })))
        {
            var concurrentResponses = await Task.WhenAll(
                client.SendAsync(concurrentRequestA, cancellationToken),
                client.SendAsync(concurrentRequestB, cancellationToken));
            try
            {
                Assert.Contains(concurrentResponses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Contains(concurrentResponses, response => response.StatusCode == HttpStatusCode.Conflict);
            }
            finally
            {
                foreach (var response in concurrentResponses) response.Dispose();
            }
        }

        _ = await SendJson<ProbeResponse>(client, HttpMethod.Post, "/api/v1/probes", "Engineer",
            new CreateProbeRequest(third.Id, group.Id, 30, 2_000, 3, 100, 200, 3, 2),
            HttpStatusCode.Created, cancellationToken);

        const string csv = "siteCode,name,address,hostname,deviceType,area,owner,criticality,tags\n" +
            "BKK-01,Imported 1,192.168.1.20,imported.example.local,PLC,Line B,EE,High,production|line-b\n" +
            "BKK-01,Duplicate enabled,192.168.1.10,,PLC,,,Normal,\n" +
            "BKK-01,Reuse disabled,192.168.1.11,shared.example.local,PLC,,,Normal,\n" +
            "BKK-01,Duplicate preview row,192.168.1.20,,PLC,,,Normal,\n";
        using var csvContent = new StringContent(csv, Encoding.UTF8, "text/csv");
        using var previewRequest = Request(HttpMethod.Post, "/api/v1/devices/import/preview", "Engineer", ActorId, csvContent);
        var previewResponse = await client.SendAsync(previewRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<CsvImportPreviewResponse>(cancellationToken);
        Assert.NotNull(preview);
        Assert.Equal(2, preview.ValidRows);
        Assert.Equal(2, preview.InvalidRows);
        Assert.Contains(preview.Rows, row => row.Normalized?.Address == "192.168.1.11" && row.Errors.Count == 0);
        Assert.Contains(preview.Rows, row => row.Normalized?.Address == "192.168.1.10" &&
            row.Errors.Any(error => error.Code == "duplicate_site_address"));
        Assert.Contains(preview.Rows, row => row.Normalized?.Address == "192.168.1.20" &&
            row.Errors.Any(error => error.Code == "duplicate_site_address"));

        using (var crossActor = Request(HttpMethod.Post, "/api/v1/devices/import/commit", "Engineer", Guid.NewGuid(),
                   JsonContent.Create(new CsvImportCommitRequest(preview.PreviewToken))))
        {
            var forbiddenCommit = await client.SendAsync(crossActor, cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenCommit.StatusCode);
        }

        var committed = await SendJson<CsvImportCommitResponse>(client, HttpMethod.Post, "/api/v1/devices/import/commit",
            "Engineer", new CsvImportCommitRequest(preview.PreviewToken), HttpStatusCode.OK, cancellationToken);
        var repeated = await SendJson<CsvImportCommitResponse>(client, HttpMethod.Post, "/api/v1/devices/import/commit",
            "Engineer", new CsvImportCommitRequest(preview.PreviewToken), HttpStatusCode.OK, cancellationToken);
        Assert.Equal(2, committed.Created);
        Assert.True(repeated.AlreadyCommitted);
        Assert.Equal(committed.DeviceIds, repeated.DeviceIds);

        using (var oversized = Request(HttpMethod.Post, "/api/v1/devices/import/preview", "Engineer", ActorId,
                   new UnknownLengthContent(new byte[DeviceCsvImportService.MaximumBytes + 1])))
        {
            oversized.Content!.Headers.ContentType = new("text/csv");
            var tooLarge = await client.SendAsync(oversized, cancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        }

        using (var malformed = Request(HttpMethod.Post, "/api/v1/devices/import/preview", "Engineer", ActorId,
                   new UnknownLengthContent([0xff, 0xfe])))
        {
            malformed.Content!.Headers.ContentType = new("text/csv");
            var invalidEncoding = await client.SendAsync(malformed, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalidEncoding.StatusCode);
        }

        const string headerOnly = "siteCode,name,address,hostname,deviceType,area,owner,criticality,tags\n";
        for (var index = 1; index < DeviceCsvImportService.MaximumCachedPreviews; index++)
        {
            using var capacityContent = new StringContent(headerOnly, Encoding.UTF8, "text/csv");
            using var capacityRequest = Request(HttpMethod.Post, "/api/v1/devices/import/preview", "Engineer", ActorId, capacityContent);
            var admitted = await client.SendAsync(capacityRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using (var capacityContent = new StringContent(headerOnly, Encoding.UTF8, "text/csv"))
        using (var capacityRequest = Request(HttpMethod.Post, "/api/v1/devices/import/preview", "Engineer", ActorId, capacityContent))
        {
            var full = await client.SendAsync(capacityRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        }

        var replayAfterCapacity = await SendJson<CsvImportCommitResponse>(client, HttpMethod.Post,
            "/api/v1/devices/import/commit", "Engineer", new CsvImportCommitRequest(preview.PreviewToken),
            HttpStatusCode.OK, cancellationToken);
        Assert.True(replayAfterCapacity.AlreadyCommitted);

        using (var viewerDelete = Request(HttpMethod.Delete, $"/api/v1/devices/{committed.DeviceIds[0]}", "Viewer", null))
        {
            var forbidden = await client.SendAsync(viewerDelete, cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using (var adminDelete = Request(HttpMethod.Delete, $"/api/v1/devices/{committed.DeviceIds[0]}", "Administrator", ActorId))
        {
            var deleted = await client.SendAsync(adminDelete, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.True(await db.AuditEvents.CountAsync(cancellationToken) >= 9);
        Assert.All(await db.AuditEvents.ToListAsync(cancellationToken), audit => Assert.Equal(ActorId, audit.ActorId));
    }

    private static Task<DeviceResponse> CreateDevice(
        HttpClient client, string siteId, string name, string address, CancellationToken cancellationToken,
        string? hostname = "shared.example.local") =>
        SendJson<DeviceResponse>(client, HttpMethod.Post, "/api/v1/devices", "Engineer",
            new CreateDeviceRequest(siteId, name, address, hostname, "PLC", "Line A", "EE", "Normal", ["production"]),
            HttpStatusCode.Created, cancellationToken);

    private static async Task<T> SendGet<T>(HttpClient client, string path, string role, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path, role, null);
        var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
    }

    private static async Task<T> SendJson<T>(
        HttpClient client, HttpMethod method, string path, string role, object body,
        HttpStatusCode expected, CancellationToken cancellationToken)
    {
        using var request = Request(method, path, role, ActorId, JsonContent.Create(body));
        var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
    }

    private static HttpRequestMessage Request(
        HttpMethod method, string path, string role, Guid? actorId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-EE-Pulse-Role", role);
        if (actorId.HasValue) request.Headers.Add("X-EE-Pulse-Actor", actorId.Value.ToString());
        return request;
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

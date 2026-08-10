using EePulse.Domain.Common;
using EePulse.Domain.Inventory;

namespace EePulse.UnitTests;

public sealed class InventoryDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeviceNormalizesIpv4HostnameAndTags()
    {
        var device = new Device(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "PLC 1",
            "192.168.1.10",
            "PLC-1.EXAMPLE.LOCAL.",
            "PLC",
            "Line A",
            "EE",
            Criticality.High,
            ["Production", "production", " Line-A "],
            Now);

        Assert.Equal("192.168.1.10", device.Address);
        Assert.Equal("plc-1.example.local", device.Hostname);
        Assert.Equal(["line-a", "production"], device.Tags);
        Assert.True(device.Enabled);
        Assert.Equal(TimeSpan.Zero, device.CreatedAt.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.local")]
    [InlineData("2001:db8::1")]
    [InlineData("999.1.1.1")]
    [InlineData("192.168.001.010")]
    public void DeviceRejectsNonIpv4Address(string address)
    {
        Assert.Throws<DomainValidationException>(() => new Device(
            Guid.NewGuid(), Guid.NewGuid(), "Device", address, null, "PLC", null, null,
            Criticality.Normal, [], Now));
    }

    [Theory]
    [InlineData("https://plc.example.local")]
    [InlineData("-plc.example.local")]
    [InlineData("plc-.example.local")]
    [InlineData("plc name.example.local")]
    [InlineData("192.168.1.10")]
    [InlineData("plc_example.local")]
    [InlineData("plc\nname.example.local")]
    [InlineData("plc.例子.local")]
    public void DeviceRejectsInvalidHostname(string hostname)
    {
        Assert.Throws<DomainValidationException>(() => new Device(
            Guid.NewGuid(), Guid.NewGuid(), "Device", "192.168.1.10", hostname, "PLC", null, null,
            Criticality.Normal, [], Now));
    }

    [Fact]
    public void DisablingDeviceRetainsIdentityAndHistoryTimestamps()
    {
        var device = new Device(
            Guid.NewGuid(), Guid.NewGuid(), "Device", "192.168.1.10", "device.example.local", "PLC",
            null, null, Criticality.Normal, [], Now);
        var id = device.Id;
        var createdAt = device.CreatedAt;

        device.Update(device.SiteId, device.Name, device.Address, device.Hostname, device.DeviceType,
            device.Area, device.Owner, device.Criticality, device.Tags, false, Now.AddMinutes(1));

        Assert.False(device.Enabled);
        Assert.Equal(id, device.Id);
        Assert.Equal(createdAt, device.CreatedAt);
        Assert.Equal(Now.AddMinutes(1), device.UpdatedAt);
    }

    [Fact]
    public void SiteNormalizesCodeAndRejectsNonUtcTimestamp()
    {
        var site = new Site(Guid.NewGuid(), " bkk-01 ", "Bangkok", "Asia/Bangkok", Now);

        Assert.Equal("BKK-01", site.Code);
        Assert.Throws<DomainValidationException>(() => new Site(
            Guid.NewGuid(), "BKK-02", "Bangkok 2", "Asia/Bangkok", Now.ToOffset(TimeSpan.FromHours(7))));
    }

    [Fact]
    public void ProbeUsesMvpDefaultsAndIncrementsConfigurationVersionOnUpdate()
    {
        var probe = new Probe(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 30, 2_000, 3, 100, 200, 3, 2);

        Assert.Equal(ProbeType.Icmp, probe.Type);
        Assert.Equal(1, probe.ConfigVersion);

        probe.Update(probe.AgentGroupId, 60, 2_500, 2, 150, 300, 4, 2, false);

        Assert.Equal(2, probe.ConfigVersion);
        Assert.False(probe.Enabled);
    }

    [Theory]
    [InlineData(4, 2_000, 3, 3, 2)]
    [InlineData(30, 30_001, 3, 3, 2)]
    [InlineData(30, 2_000, 0, 3, 2)]
    [InlineData(30, 2_000, 3, 0, 2)]
    [InlineData(30, 2_000, 3, 3, 0)]
    public void ProbeRejectsUnsafeConfiguration(
        int intervalSeconds,
        int timeoutMilliseconds,
        int attemptCount,
        int failureThreshold,
        int recoveryThreshold)
    {
        Assert.Throws<DomainValidationException>(() => new Probe(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), intervalSeconds, timeoutMilliseconds, attemptCount,
            null, null, failureThreshold, recoveryThreshold));
    }

    [Fact]
    public void ProbeRequiresCriticalRttToExceedWarningRtt()
    {
        Assert.Throws<DomainValidationException>(() => new Probe(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 30, 2_000, 3, 200, 100, 3, 2));
    }

    [Fact]
    public void MaintenanceWindowRequiresExactlyOneScopeAndUtcRange()
    {
        var siteId = Guid.NewGuid();
        var window = new MaintenanceWindow(
            Guid.NewGuid(), "Planned work", Now, Now.AddHours(1), "Asia/Bangkok", siteId, null, null, Now);

        Assert.Equal(siteId, window.SiteId);
        Assert.Throws<DomainValidationException>(() => new MaintenanceWindow(
            Guid.NewGuid(), "No scope", Now, Now.AddHours(1), "UTC", null, null, null, Now));
        Assert.Throws<DomainValidationException>(() => new MaintenanceWindow(
            Guid.NewGuid(), "Multiple scopes", Now, Now.AddHours(1), "UTC", siteId, Guid.NewGuid(), null, Now));
        Assert.Throws<DomainValidationException>(() => new MaintenanceWindow(
            Guid.NewGuid(), "Bad range", Now, Now, "UTC", siteId, null, null, Now));
    }
}

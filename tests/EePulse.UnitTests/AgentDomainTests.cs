using EePulse.Application.Agents;
using EePulse.Domain.Agents;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EePulse.UnitTests;

public sealed class AgentDomainTests
{
    [Fact]
    public void HeartbeatCannotAdvanceCentralEffectiveConfiguration()
    {
        var now = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var agent = new Agent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "probe-01", "1.2.3", 20, now);
        agent.SetDesiredConfiguration(7);
        agent.Heartbeat("1.2.3", "probe-01", 0, AgentSelfHealth.Healthy, 7, now, now);
        Assert.Equal(0, agent.LastAppliedConfigurationVersion);
        agent.AcknowledgeApplied(7, now);
        Assert.Equal(7, agent.LastAppliedConfigurationVersion);
    }

    [Theory]
    [InlineData(15, 60)]
    [InlineData(20, 60)]
    [InlineData(30, 90)]
    public void OfflineBoundaryUsesServerReceiptAndRevocationPrecedesStatus(int interval, int expiry)
    {
        var now = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var agent = new Agent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "probe-01", "1.2.3", interval, now);
        agent.Heartbeat("1.2.3", "probe-01", 0, AgentSelfHealth.Healthy, 0, now.AddHours(-4), now);
        Assert.False(agent.MarkOffline(now.AddSeconds(expiry - 1)));
        Assert.True(agent.MarkOffline(now.AddSeconds(expiry)));
        agent.Revoke("Administrative", now.AddSeconds(expiry + 1));
        Assert.Equal(AgentStatus.Revoked, agent.Status);
    }

    [Fact]
    public void NetworkPolicyNormalizesContainsAndRejectsBroadcastScope()
    {
        Assert.Equal(["192.0.2.0/24"], Ipv4NetworkPolicy.Normalize(["192.0.2.99/24"], false));
        Assert.True(Ipv4NetworkPolicy.ContainsAddress(["192.0.2.0/24"], "192.0.2.1"));
        Assert.False(Ipv4NetworkPolicy.ContainsAddress(["192.0.2.0/24"], "192.0.2.0"));
        Assert.False(Ipv4NetworkPolicy.ContainsAddress(["192.0.2.0/24"], "192.0.2.255"));
        Assert.True(Ipv4NetworkPolicy.ContainsAddress(["192.0.2.0/31"], "192.0.2.0"));
        Assert.True(Ipv4NetworkPolicy.ContainsAddress(["192.0.2.1/32"], "192.0.2.1"));
        Assert.ThrowsAny<Exception>(() => Ipv4NetworkPolicy.Normalize(["0.0.0.0/8"], false));
        Assert.ThrowsAny<Exception>(() => Ipv4NetworkPolicy.Normalize(["192.0.2.0/24", "192.0.2.1/32"], false));
    }

    [Fact]
    public void NetworkPolicyEnforcesSixtyFourEntryCeiling()
    {
        var sixtyFour = Enumerable.Range(1, 64).Select(value => $"192.0.2.{value}/32").ToArray();
        Assert.Equal(64, Ipv4NetworkPolicy.Normalize(sixtyFour, false).Count);
        Assert.ThrowsAny<Exception>(() => Ipv4NetworkPolicy.Normalize([.. sixtyFour, "192.0.2.65/32"], false));
    }

    [Fact]
    public void PendingCredentialExpiresAfterTwentyFourHoursWhileWireCredentialLifetimeIsNinetyDays()
    {
        var now=new DateTimeOffset(2026,8,11,0,0,0,TimeSpan.Zero);
        var credential=new AgentCredential(Guid.NewGuid(),Guid.NewGuid(),new byte[32],AgentCredentialState.Pending,now.AddDays(90),now.AddDays(75),now);
        Assert.Equal(now.AddHours(24),credential.PendingExpiresAt);
        Assert.Equal(now.AddDays(90),credential.ExpiresAt);
    }

    [Fact]
    public void SnapshotsAndAcknowledgementsAreApplicationImmutable()
    {
        var options=new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none").Options;
        using var db=new EePulseDbContext(options);
        var snapshot=new AgentConfigurationSnapshot(Guid.NewGuid(),1,"{}",System.Security.Cryptography.SHA256.HashData("{}"u8.ToArray()),DateTimeOffset.UtcNow,null);
        db.Attach(snapshot);db.Entry(snapshot).State=EntityState.Modified;
        Assert.Throws<InvalidOperationException>(()=>db.SaveChanges());
        db.ChangeTracker.Clear();
        var acknowledgement=new AgentConfigurationAcknowledgement(Guid.NewGuid(),Guid.NewGuid(),1,AgentAcknowledgementStatus.Rejected,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"configuration-invalid",0,1);
        db.Attach(acknowledgement);db.Entry(acknowledgement).State=EntityState.Deleted;
        Assert.Throws<InvalidOperationException>(()=>db.SaveChanges());
    }
}

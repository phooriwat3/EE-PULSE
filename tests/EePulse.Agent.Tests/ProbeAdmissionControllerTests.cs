using EePulse.Agent.Core.Execution;

namespace EePulse.Agent.Tests;

public sealed class ProbeAdmissionControllerTests
{
    [Fact]
    public void AdmissionIsBoundedAndReleasesInReverseOrder()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 1, targetConcurrency: 1);
        var firstProbe = Guid.NewGuid();
        Assert.True(admission.TryAcquire(firstProbe, "10.0.0.8", out var first));
        Assert.Equal(1, admission.ActiveTargetGuardCount);
        Assert.False(admission.TryAcquire(Guid.NewGuid(), "10.0.0.9", out _));
        first!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.0.0.9", out var second));
        second!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Fact]
    public void PerTargetAndPerProbeGuardsRejectOverlappingAdmission()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 2, targetConcurrency: 1);
        var probe = Guid.NewGuid();
        Assert.True(admission.TryAcquire(probe, "10.0.0.8", out var first));
        Assert.False(admission.TryAcquire(Guid.NewGuid(), "10.0.0.8", out _));
        Assert.False(admission.TryAcquire(probe, "10.0.0.9", out _));
        Assert.Equal(1, admission.ActiveTargetGuardCount);
        first!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Fact]
    public void CanonicallyEquivalentTargetsShareOnePerTargetGuard()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 2, targetConcurrency: 1);

        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.8", out var first));
        Assert.False(admission.TryAcquire(Guid.NewGuid(), "10.0.0.8", out _));
        Assert.Equal(1, admission.ActiveTargetGuardCount);

        first!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Fact]
    public void DifferentCanonicalTargetsUseIndependentPerTargetGuards()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 2, targetConcurrency: 1);

        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.8", out var first));
        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.0.0.9", out var second));
        Assert.Equal(2, admission.ActiveTargetGuardCount);

        first!.Dispose();
        Assert.Equal(1, admission.ActiveTargetGuardCount);
        second!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host.example")]
    [InlineData("::1")]
    [InlineData("999.1.1.1")]
    public void InvalidTargetsAreRejectedBeforeAdmission(string target)
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 1, targetConcurrency: 1);

        Assert.Throws<ArgumentException>(() => admission.TryAcquire(Guid.NewGuid(), target, out _));
        Assert.Equal(0, admission.ActiveTargetGuardCount);
        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.0.0.8", out var lease));

        lease!.Dispose();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(257, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    public void LimitsRemainWithinFrozenRanges(int global, int target)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProbeAdmissionController(global, target));
    }

    [Fact]
    public void FailedTargetAndProbeAdmissionReleaseAttemptReferences()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 2, targetConcurrency: 1);
        var probe = Guid.NewGuid();
        Assert.True(admission.TryAcquire(probe, "10.0.0.8", out var first));
        Assert.False(admission.TryAcquire(Guid.NewGuid(), "10.0.0.8", out _));
        Assert.False(admission.TryAcquire(probe, "10.0.0.9", out _));
        Assert.Equal(1, admission.ActiveTargetGuardCount);

        first!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Fact]
    public void MultipleReferencesKeepTheSameTargetGuardAliveUntilFinalRelease()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 2, targetConcurrency: 2);
        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.0.0.8", out var first));
        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.8", out var second));
        Assert.Equal(1, admission.ActiveTargetGuardCount);

        first!.Dispose();
        Assert.Equal(1, admission.ActiveTargetGuardCount);
        second!.Dispose();
        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Fact]
    public void GlobalFailureDoesNotCreateTargetGuardAndTargetChurnEvictsAllEntries()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 1, targetConcurrency: 1);
        Assert.True(admission.TryAcquire(Guid.NewGuid(), "10.0.0.8", out var held));
        Assert.False(admission.TryAcquire(Guid.NewGuid(), "10.0.0.9", out _));
        Assert.Equal(1, admission.ActiveTargetGuardCount);
        held!.Dispose();

        for (var host = 1; host <= 100; host++)
        {
            Assert.True(admission.TryAcquire(Guid.NewGuid(), $"192.0.2.{host}", out var lease));
            lease!.Dispose();
        }

        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    [Fact]
    public async Task ConcurrentAcquireReleaseRaceNeverUsesDisposedTargetGuard()
    {
        using var admission = new ProbeAdmissionController(globalConcurrency: 2, targetConcurrency: 1);
        using var start = new Barrier(2);
        var first = Task.Run(() => AcquireAndReleaseRepeatedly(admission, start, "10.0.0.8"), TestContext.Current.CancellationToken);
        var second = Task.Run(() => AcquireAndReleaseRepeatedly(admission, start, "10.8"), TestContext.Current.CancellationToken);

        await Task.WhenAll(first, second);

        Assert.Equal(0, admission.ActiveTargetGuardCount);
    }

    private static void AcquireAndReleaseRepeatedly(ProbeAdmissionController admission, Barrier start, string target)
    {
        start.SignalAndWait();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (admission.TryAcquire(Guid.NewGuid(), target, out var lease)) lease!.Dispose();
        }
    }
}

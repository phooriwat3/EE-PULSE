using EePulse.Agent.Core.Probing;

namespace EePulse.Agent.Core.Execution;

/// <summary>Non-queuing bounded admission: global, normalized target, then probe guard.</summary>
public sealed class ProbeAdmissionController : IDisposable
{
    public const int DefaultGlobalConcurrency = 64;
    public const int DefaultTargetConcurrency = 1;
    private readonly SemaphoreSlim global;
    private readonly int perTargetLimit;
    private readonly object targetGate = new();
    private readonly Dictionary<string, TargetEntry> targets = new(StringComparer.Ordinal);
    private readonly NonOverlappingExecutionGate<Guid> probes = new();

    internal int ActiveTargetGuardCount { get { lock (targetGate) return targets.Count; } }

    public ProbeAdmissionController(int globalConcurrency = DefaultGlobalConcurrency, int targetConcurrency = DefaultTargetConcurrency)
    {
        if (globalConcurrency is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(globalConcurrency));
        }

        if (targetConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(targetConcurrency));
        }
        global = new SemaphoreSlim(globalConcurrency, globalConcurrency);
        perTargetLimit = targetConcurrency;
    }

    public bool TryAcquire(Guid probeId, string normalizedTarget, out IDisposable? lease)
    {
        lease = null;
        if (!Ipv4ProbeTarget.TryNormalize(normalizedTarget, out var canonicalTarget))
        {
            throw new ArgumentException("The probe target must be an IPv4 literal.", nameof(normalizedTarget));
        }

        if (!global.Wait(0)) return false;

        var target = AddTargetReference(canonicalTarget!);
        var targetPermitAcquired = false;
        try
        {
            if (!target.Semaphore.Wait(0)) return false;
            targetPermitAcquired = true;
            if (!probes.TryEnter(probeId, out var probeLease)) return false;

            lease = new Lease(this, global, target, probeLease!);
            targetPermitAcquired = false;
            return true;
        }
        finally
        {
            if (lease is null)
            {
                if (targetPermitAcquired) target.Semaphore.Release();
                ReleaseTargetReference(target);
                global.Release();
            }
        }
    }

    public void Dispose()
    {
        global.Dispose();
        lock (targetGate)
        {
            foreach (var target in targets.Values) target.Semaphore.Dispose();
            targets.Clear();
        }
    }

    private TargetEntry AddTargetReference(string canonicalTarget)
    {
        lock (targetGate)
        {
            if (!targets.TryGetValue(canonicalTarget, out var target))
            {
                target = new TargetEntry(canonicalTarget, new SemaphoreSlim(perTargetLimit, perTargetLimit));
                targets.Add(canonicalTarget, target);
            }

            target.ReferenceCount++;
            return target;
        }
    }

    private void ReleaseTargetReference(TargetEntry target)
    {
        var dispose = false;
        lock (targetGate)
        {
            if (--target.ReferenceCount == 0 &&
                targets.TryGetValue(target.CanonicalTarget, out var current) &&
                ReferenceEquals(current, target))
            {
                targets.Remove(target.CanonicalTarget);
                dispose = true;
            }
        }

        if (dispose) target.Semaphore.Dispose();
    }

    private sealed class Lease(ProbeAdmissionController owner, SemaphoreSlim global, TargetEntry target, IDisposable probeLease) : IDisposable
    {
        private IDisposable? probeLease = probeLease;
        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref probeLease, null);
            if (lease is null) return;
            lease.Dispose();
            target.Semaphore.Release();
            owner.ReleaseTargetReference(target);
            global.Release();
        }
    }

    private sealed class TargetEntry(string canonicalTarget, SemaphoreSlim semaphore)
    {
        public string CanonicalTarget { get; } = canonicalTarget;
        public SemaphoreSlim Semaphore { get; } = semaphore;
        public int ReferenceCount { get; set; }
    }
}

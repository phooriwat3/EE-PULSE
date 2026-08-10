using System.Collections.Concurrent;

namespace EePulse.Agent.Core.Execution;

public sealed class NonOverlappingExecutionGate<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, byte> activeKeys = new();

    public bool TryEnter(TKey key, out IDisposable? lease)
    {
        if (!activeKeys.TryAdd(key, 0))
        {
            lease = null;
            return false;
        }

        lease = new Lease(activeKeys, key);
        return true;
    }

    private sealed class Lease(ConcurrentDictionary<TKey, byte> activeKeys, TKey key) : IDisposable
    {
        private ConcurrentDictionary<TKey, byte>? owner = activeKeys;

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref owner, null);
            currentOwner?.TryRemove(key, out _);
        }
    }
}

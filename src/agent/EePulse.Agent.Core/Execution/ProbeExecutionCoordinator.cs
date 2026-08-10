namespace EePulse.Agent.Core.Execution;

/// <summary>
/// Enforces a global concurrency limit and prevents simultaneous work for the same probe.
/// </summary>
public sealed class ProbeExecutionCoordinator : IDisposable
{
    private readonly NonOverlappingExecutionGate<Guid> nonOverlap = new();
    private readonly SemaphoreSlim concurrency;

    public ProbeExecutionCoordinator(int maximumConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrency, 1);
        concurrency = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public async ValueTask<bool> TryExecuteAsync(
        Guid probeId,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!nonOverlap.TryEnter(probeId, out var lease))
        {
            return false;
        }

        using (lease)
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                return true;
            }
            finally
            {
                concurrency.Release();
            }
        }
    }

    public void Dispose() => concurrency.Dispose();
}

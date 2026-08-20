namespace EePulse.Agent.Core.Probing;

/// <summary>Runs sequential local attempts and creates no result when cancellation interrupts lifecycle work.</summary>
public sealed class LocalProbeRunner(IProbeTransport transport, IProbeExecutionClock clock)
{
    public async ValueTask<LocalProbeResult?> RunAsync(LocalProbeExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        Validate(execution);

        var startedAt = clock.GetUtcNow();
        var startedTimestamp = clock.GetTimestamp();
        var successes = new List<decimal>(execution.AttemptCount);
        ProbeErrorCategory? lastError = null;

        try
        {
            for (var attempt = 0; attempt < execution.AttemptCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 0)
                {
                    await clock.DelayAsync(execution.InterAttemptDelay, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var reply = await transport.SendAsync(
                    new ProbeTransportRequest(execution.NormalizedTarget, execution.Timeout), cancellationToken).ConfigureAwait(false);
                if (reply.Status == ProbeTransportStatus.Succeeded && reply.RoundTripTime is { } rtt)
                {
                    successes.Add((decimal)rtt.TotalMilliseconds);
                }
                else
                {
                    lastError = ToErrorCategory(reply.Status);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (NetworkPolicyViolationException)
        {
            lastError = ProbeErrorCategory.InvalidTarget;
        }
        catch (UnauthorizedAccessException)
        {
            lastError = ProbeErrorCategory.PermissionDenied;
        }
        catch
        {
            lastError = ProbeErrorCategory.TransportError;
        }

        var elapsed = clock.GetElapsedTime(startedTimestamp, clock.GetTimestamp());
        if (elapsed < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The probe execution clock returned a negative elapsed duration.");
        }

        var successfulAttemptCount = successes.Count;
        return new LocalProbeResult(
            execution.ConfigurationVersion, execution.ProbeId, startedAt, startedAt.Add(elapsed),
            execution.AttemptCount, successfulAttemptCount,
            (decimal)(execution.AttemptCount - successfulAttemptCount) / execution.AttemptCount,
            successfulAttemptCount == 0 ? null : successes.Min(),
            successfulAttemptCount == 0 ? null : successes.Average(),
            successfulAttemptCount == 0 ? null : successes.Max(),
            successfulAttemptCount == execution.AttemptCount ? null : lastError ?? ProbeErrorCategory.TransportError);
    }

    private static void Validate(LocalProbeExecution execution)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.ConfigurationVersion, 1);
        ArgumentOutOfRangeException.ThrowIfEqual(execution.ProbeId, Guid.Empty);
        if (!Ipv4ProbeTarget.TryNormalize(execution.NormalizedTarget, out var normalized) || normalized != execution.NormalizedTarget)
        {
            throw new ArgumentException("The probe target must be a normalized IPv4 literal.", nameof(execution));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(execution.AttemptCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(execution.Timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(execution.InterAttemptDelay, TimeSpan.Zero);
    }

    private static ProbeErrorCategory ToErrorCategory(ProbeTransportStatus status) => status switch
    {
        ProbeTransportStatus.TimedOut => ProbeErrorCategory.Timeout,
        ProbeTransportStatus.Unreachable => ProbeErrorCategory.Unreachable,
        ProbeTransportStatus.PermissionDenied => ProbeErrorCategory.PermissionDenied,
        ProbeTransportStatus.NetworkUnavailable => ProbeErrorCategory.NetworkUnavailable,
        ProbeTransportStatus.InvalidTarget => ProbeErrorCategory.InvalidTarget,
        _ => ProbeErrorCategory.TransportError,
    };
}

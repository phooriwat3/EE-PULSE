using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Transport;

namespace EePulse.Agent.Core.Outbox;

public interface IProbeResultDeliveryDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IProbeResultDeliveryRandom
{
    double NextDouble();
}

public sealed class ProbeResultDeliveryDelay(TimeProvider timeProvider) : IProbeResultDeliveryDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, timeProvider, cancellationToken));
}

public sealed class ProbeResultDeliveryRandom : IProbeResultDeliveryRandom
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed record ProbeResultDeliveryOptions(int MaximumBatchCount = 1000, int MaximumBatchBytes = 900 * 1024,
    int MinimumRetrySeconds = 1, int MaximumRetrySeconds = 30)
{
    public void Validate()
    {
        if (MaximumBatchCount is < 1 or > 1000 || MaximumBatchBytes is < 1 or > 1024 * 1024 ||
            MinimumRetrySeconds < 1 || MaximumRetrySeconds < MinimumRetrySeconds)
        {
            throw new InvalidOperationException("Probe-result delivery options are invalid.");
        }
    }
}

public sealed record ProbeResultDeliveryCycle(bool Delivered, bool HasPendingResults, TimeSpan NextDelay);

/// <summary>Delivers FIFO outbox batches with durable per-result acknowledgement and quarantine outcomes.</summary>
public sealed class ProbeResultDeliveryCoordinator(
    IProbeResultOutbox outbox,
    AgentApiClient apiClient,
    TimeProvider timeProvider,
    IProbeResultDeliveryRandom random,
    ProbeResultDeliveryOptions? configuredOptions = null)
{
    private readonly ProbeResultDeliveryOptions options = configuredOptions ?? new();
    private int consecutiveFailures;

    public async ValueTask<ProbeResultDeliveryCycle> DeliverOnceAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        options.Validate();
        var records = await outbox.ReadPendingAsync(
            new ProbeResultOutboxReadLimit(options.MaximumBatchCount, options.MaximumBatchBytes), cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
        {
            consecutiveFailures = 0;
            return new(false, false, TimeSpan.FromSeconds(options.MinimumRetrySeconds));
        }

        EePulse.Contracts.Agents.ProbeResultIngestionBatchResponse response;
        try
        {
            var batchId = Guid.NewGuid();
            response = await apiClient.SendProbeResultBatchAsync(identity, batchId, records.Select(record => record.Envelope).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            ValidateResponse(batchId, records, response);
        }
        catch (AgentApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            return new(false, true, Timeout.InfiniteTimeSpan);
        }
        catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, true, NextRetryDelay());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, true, NextRetryDelay());
        }
        catch (AgentApiException exception) when (!cancellationToken.IsCancellationRequested &&
            exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await apiClient.RecoverAuthenticationAsync(cancellationToken).ConfigureAwait(false);
            return new(false, true, NextRetryDelay());
        }
        catch (AgentApiException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests ||
             (int)exception.StatusCode >= 500))
        {
            return new(false, true, NextRetryDelay());
        }
        catch (InvalidDataException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, true, NextRetryDelay());
        }

        await outbox.ApplyDeliveryOutcomeAsync(
            response.AcceptedResultIds,
            response.Rejections.Select(rejection => new ProbeResultPermanentRejection(rejection.ResultId, rejection.Code)).ToArray(),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        consecutiveFailures = 0;
        return new(true, true, TimeSpan.Zero);
    }

    private static void ValidateResponse(Guid batchId, IReadOnlyList<ProbeResultOutboxRecord> records, EePulse.Contracts.Agents.ProbeResultIngestionBatchResponse response)
    {
        if (response.BatchId != batchId)
        {
            throw new InvalidDataException("Probe-result acknowledgement batch identity is invalid.");
        }

        var requested = records.Select(record => record.Envelope.ResultId).ToHashSet();
        var accepted = response.AcceptedResultIds.ToHashSet();
        var rejected = response.Rejections.Select(rejection => rejection.ResultId).ToHashSet();
        if (accepted.Count != response.AcceptedResultIds.Count || rejected.Count != response.Rejections.Count ||
            accepted.Overlaps(rejected) || !accepted.IsSubsetOf(requested) || !rejected.IsSubsetOf(requested) ||
            response.Rejections.Any(rejection => string.IsNullOrWhiteSpace(rejection.Code)))
        {
            throw new InvalidDataException("Probe-result acknowledgement is invalid.");
        }
    }

    private TimeSpan NextRetryDelay()
    {
        var exponent = Math.Min(consecutiveFailures++, 30);
        var cap = Math.Min(options.MaximumRetrySeconds, options.MinimumRetrySeconds * (1L << Math.Min(exponent, 20)));
        return TimeSpan.FromMilliseconds(cap * 1000d * random.NextDouble());
    }
}

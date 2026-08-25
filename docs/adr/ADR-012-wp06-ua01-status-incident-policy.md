# ADR-012: Approved WP-06 UA-01 status and incident policy

Status: Accepted
Date: 2026-08-25 (Asia/Bangkok)

## Context

WP-06 requires deterministic operational policy for status classification, incidents,
late data, configuration lineage, flapping, maintenance, and availability. The PRD
probe values were proposals until this decision.

## Decision

The following MVP policy is binding.

1. Per-Probe defaults/ranges preserve the frozen v1 configuration: interval 30 seconds
   (5-3,600), timeout 2,000 milliseconds (100-60,000), attempts 3 (1-10), failure
   threshold 3 (1-100), and recovery threshold 2 (1-100). The existing v1
   `WarningRttMilliseconds` field remains the configurable RTT warning threshold;
   newly configured Probes default it to 500 ms, it retains its existing positive-value
   validation, and it is compared with result `AverageRttMilliseconds` without
   renaming the public contract. `warningPacketLossRatio` is an internal nullable
   policy input in the same decimal-ratio representation as the frozen result
   contract: 5% is `0.05m`, a configured value is greater than 0 and at most 1, and
   `null` means packet loss is not evaluated. Any future additive configuration
   contract field for it requires Lead compatibility review in a later wave. Compare
   result measurements only with their configured warning thresholds. An
   unconfigured dimension is ignored; a success with no configured quality
   thresholds is `UP`. Comparison is inclusive: `AverageRttMilliseconds >=
   WarningRttMilliseconds` and `PacketLossRatio >= warningPacketLossRatio` are
   `DEGRADED`. A null `AverageRttMilliseconds` cannot breach the RTT threshold but
   does not prevent normal evaluation of a configured packet-loss threshold. If no
   available measurement breaches a configured threshold, the successful result is
   `UP`. Critical-quality incidents are disabled for MVP. Result-driven transition
   reasons are fixed, bounded codes: `bootstrap-success`, `quality-degraded`,
   `quality-restored`, `failure-threshold-met`, `recovery-pending`,
   `recovery-threshold-met`, and `recovery-failed`.
2. The first confirmed DOWN that opens an availability incident emits an `opened`
   IncidentLifecycleEvent with NotificationSuppressionContext
   `eligible/availability-down`. Outside maintenance and active flapping, confirmed
   recovery auto-resolves the active incident using actor `system-policy` and reason
   `confirmed-recovery`, emitting a `resolved` IncidentLifecycleEvent with
   NotificationSuppressionContext `eligible/confirmed-recovery`.
   Disable resolves an active incident with reason `probe-disabled`, producing a
   `resolved` IncidentLifecycleEvent with a suppressed
   NotificationSuppressionContext (`probe-disabled`).
3. Counters are bounded: consecutive failures saturate at `failureThreshold` and
   consecutive recovery/successes saturate at `recoveryThreshold`; a failure resets
   success to zero and a success resets failure to zero, so counters never overflow.
   Any `Failure` during `RECOVERING` returns immediately to `DOWN`, sets success to
   zero and failure to one, retains the active incident, and increments occurrence
   exactly once. Its transition reason is `recovery-failed`; it emits one
   `occurrence` IncidentLifecycleEvent with a suppressed
   NotificationSuppressionContext (`recovery-failed`). A failure while already
   `DOWN` remains `DOWN` and only updates the bounded failure counter. A success
   while `UP` or `DEGRADED` applies quality classification and transitions only when
   the quality-derived state changes: `UP` to `DEGRADED` is `quality-degraded` and
   `DEGRADED` to `UP` is `quality-restored`. A `recoveryThreshold` of one allows
   `DOWN` to transition directly to quality-derived `UP`/`DEGRADED` with
   `recovery-threshold-met`.
4. Heartbeat/freshness `UNKNOWN` leaves an active incident unchanged and produces
   no IncidentLifecycleEvent or NotificationSuppressionContext.
5. Flapping activates on the third confirmed `DOWN` in a rolling 15-minute window,
   using persisted state-transition `eventAt` after the lateness/skew checks. The
   preceding DOWN transitions must be separated by confirmed recoveries. Before
   activation normal auto-recovery applies. On activation, retain the current active
   incident. If absent, open it and give its `opened` event a suppressed context
   (`flapping-activated`); if already active, retain it without a second opening.
   While flapping, recovery does not resolve; each subsequent confirmed DOWN adds
   exactly one occurrence and emits an `occurrence` event with a suppressed context
   (`flapping-active`). A durable synthetic cause, using the database clock, resolves
   only after 30 continuous healthy (`UP`/`DEGRADED`) minutes; its `resolved` event
   has an eligible context (`flapping-stable-recovery`).
6. A result may drive state only when
   `receivedAt - 5 minutes <= eventAt <= receivedAt + 60 seconds`. Older results are
   `beyond-approved-lateness`; future results are `future-or-skew-suspect`; both
   remain auditable historical-only data. A cursor-lower result is always
   `late-order`.
7. Policy lineage has two durable concepts. Immutable policy content is mapped as
   `(probeId, configurationVersion) -> policySnapshotId`. Separately, each Agent's
   effective boundary is mapped as `(agentId, configurationVersion) ->
   appliedAcknowledgementReceivedAt`, the database timestamp at which the Server
   durably persists that Agent's `Applied` acknowledgement. A ledger result resolves
   content by its Probe/version and eligibility by its Agent/version; do not assume
   one Agent per Agent Group. Compare PostgreSQL `ledger.receivedAt` to the per-Agent
   boundary; Agent-reported time does not determine it. Results received earlier are
   `config-not-effective` historical-only; later results use the immutable mapped
   snapshot. Configuration causes use their persisted source boundary; maintenance,
   disable, and expiry causes retain the snapshot at their persisted database source
   boundary. Policy changes are not retroactive.
8. Eligible availability time is enabled, scheduled time outside maintenance.
   Maintenance and disabled time are excluded and reported separately. Coverage is
   `(UP + DEGRADED + DOWN + RECOVERING) / eligible`; availability is
   `(UP + DEGRADED) / (UP + DEGRADED + DOWN + RECOVERING)`. UNKNOWN duration is
   explicit, RECOVERING is unavailable, and flapping is reported separately.
9. Maintenance persists transitions/counters but they do not contribute to flapping
   activation or exit. It creates no incident/event/context for a DOWN first
   confirmed during maintenance, and recovery during maintenance does not resolve.
   At maintenance end: UP/DEGRADED resolves an active incident only when flapping is
   inactive; DOWN opens an incident if absent or retains it if active; RECOVERING
   retains it; UNKNOWN follows item 4. If flapping is active, retain the incident and
   restart the 30-minute stable-recovery timer from the maintenance-end database
   boundary. Lifecycle events created at maintenance end have an eligible context.

WP-08 owns delivery interpretation of every IncidentLifecycleEvent and its paired
NotificationSuppressionContext; this ADR does not prescribe providers or delivery.
Operator acknowledgement and authorized manual-resolution public-action lifecycle
contracts are outside this WP-06 engine checkpoint and require later compatibility
review.

## Consequences

WP-06 may implement the approved deterministic policy and acceptance matrix. WP-08
consumes the durable lifecycle handoff only. WP-09 must apply the approved coverage
and availability semantics. Results received more than five minutes after `eventAt`
remain historical/auditable but cannot affect current state or incidents.

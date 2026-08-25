# ADR-012: Approved WP-06 UA-01 status and incident policy

Status: Accepted
Date: 2026-08-25 (Asia/Bangkok)

## Context

WP-06 requires deterministic operational policy for status classification, incidents,
late data, configuration lineage, flapping, maintenance, and availability. The PRD
probe values were proposals until this decision.

## Decision

The following MVP policy is binding.

1. Per-Probe defaults/ranges are: interval 30 seconds (10-300), timeout 2 seconds
   (1-10), attempts 3 (1-5), failure threshold 3 (1-10), and recovery threshold 2
   (1-10). Default configurable warning thresholds are `warningAverageRttMs = 500 ms`
   and `warningPacketLossRatio = 5%`; permitted values are 1-60,000 ms and 0-100%,
   respectively. Compare result measurements `averageRttMs` and `packetLossRatio`
   only with their configured warning thresholds. An unconfigured dimension is
   ignored; a success with no configured quality thresholds is `UP`. Critical-quality
   incidents are disabled for MVP.
2. The first confirmed DOWN that opens an availability incident emits an `opened`
   IncidentLifecycleEvent with NotificationSuppressionContext
   `eligible/availability-down`. Outside maintenance and active flapping, confirmed
   recovery auto-resolves the active incident using actor `system-policy` and reason
   `confirmed-recovery`, emitting a `resolved` IncidentLifecycleEvent with
   NotificationSuppressionContext `eligible/confirmed-recovery`.
   Disable resolves an active incident with reason `probe-disabled`, producing a
   `resolved` IncidentLifecycleEvent with a suppressed
   NotificationSuppressionContext (`probe-disabled`).
3. A failure during `RECOVERING` returns immediately to `DOWN`, resets recovery
   success to zero, retains the active incident, and increments occurrence exactly
   once. It emits one `occurrence` IncidentLifecycleEvent with a suppressed
   NotificationSuppressionContext (`recovery-failed`).
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
7. A configuration version becomes effective at the database timestamp when the
   Server durably persists its `Applied` acknowledgement. Compare PostgreSQL
   `ledger.receivedAt` to that timestamp; Agent-reported time does not determine the
   boundary. Results received earlier are `config-not-effective` historical-only;
   later results use the immutable snapshot mapped to their configuration version.
   Configuration causes use that boundary; maintenance, disable, and expiry causes
   retain the snapshot at their persisted database source boundary. Policy changes
   are not retroactive.
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

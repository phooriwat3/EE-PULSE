# WP-06 Status and Incident Engine design

Status: UA-01 policy approved; implementation not started
Date: 2026-08-25 (Asia/Bangkok)
Owner: Lead/Integration Agent

## Scope and authority

This document turns FR-04/FR-05, ADR-004, ADR-005, ADR-012, the implemented WP-05 immutable-result ledger, and the existing maintenance/Agent configuration model into the approved WP-06 backend design. It does not change the frozen v1 ingestion contract or generated OpenAPI. PostgreSQL is authoritative for state, incidents, channel-neutral incident lifecycle outbox events, and suppression context; the WP-05 ledger remains the immutable input and VictoriaMetrics remains a projection only.

ADR-012 records the approved UA-01 MVP policy. The approved defaults are a 30-second interval, 2,000-millisecond timeout, three attempts, failure threshold three, and recovery threshold two, subject to the frozen v1 per-Probe ranges: interval 5-3,600 seconds, timeout 100-60,000 milliseconds, attempts 1-10, and failure/recovery thresholds 1-100. Critical-quality incidents remain disabled for MVP.

## Terms and persisted model

- `eventAt` is the accepted result's `endedAt`; `receivedAt` is the Server receipt time stored by WP-05.
- `resultKey` is `(agentId, resultId)`, the WP-05 immutable identity. It replaces the older, ambiguous `runId` wording for status-processing idempotency.
- `stateCursorKey` is `(eventAt, agentId, resultId)`, ordered lexicographically ascending. `eventAt` is `endedAt`; `(agentId, resultId)` is the immutable WP-05 result identity. This is the ADR-005 event-time-plus-identity total order for one Probe. The persisted cursor is exactly `watermarkEventAt`, `watermarkAgentId`, and `watermarkResultId` (all non-null after the first state-driving result).
- `stateOrderKey` is the same `stateCursorKey`. `receivedAt` is excluded from state ordering and tie-breaking. It is used only for receipt/audit time, the expiry cutoff, and the configuration-effective boundary.
- A state-driving result is an accepted ledger result that has not already received a processing disposition and passes the watermark/skew eligibility rules below. Every other accepted result is historical-only.
- The state projection needs the exact cursor columns above, `lastFreshEventAt`, counters, visible/underlying status, active-maintenance marker, `openIncidentId`, and optimistic `stateVersion`. It also needs a durable per-ledger-row processing disposition, unique on `resultKey`.
- A transition is an append-only record with the result key (when result-driven), `from`, `to`, `eventAt`, `receivedAt`, and a fixed reason code. Timer/configuration transitions have a deterministic synthetic cause key so retry cannot duplicate them.

The precise table/column names and migrations are implementation work, owned by Backend. They must preserve the existing WP-05 ledger and use the repository ownership protocol for any shared contract change.

## Deterministic per-Probe processing stream

All state-affecting work is serialized by one transaction-scoped lock for the Probe (`ProbeId` is the lock key). WP-06 implementation must make ingestion acquire that same Probe lock before committing a ledger row; a multi-Probe batch must acquire Probe locks in ascending `ProbeId` order. WP-05 ingestion does not yet provide that per-Probe lock and must not be represented as doing so. A worker first claims the lock, then selects the smallest undisposed committed ledger row by `stateOrderKey`; it may not process a later row while an earlier committed row for that Probe is undisposed. The claim and disposition insert occur in the same transaction, so competing workers either serialize behind the lock or retry; worker timing cannot choose a different order for rows already committed when the lock is acquired.

Result rows have priority over synthetic causes. Expiry, maintenance start/end, enable/disable, and configuration-effective changes are persisted as idempotent `ProbeStateCause` records with a unique `(probeId, causeType, sourceVersionOrBoundary)` key. Under the same Probe lock, the coordinator drains every earlier committed ledger row before it evaluates the oldest due cause. It then re-evaluates the cause from the committed state/configuration and writes one applied/no-op cause disposition. A later-arriving ledger row may be historical-only under the already advanced cursor, but it cannot reorder a committed row or make a previously committed timer outcome race-dependent.

The state processor does not use a best-effort in-memory queue as authority. Recovery after a process crash selects the same undisposed ledger row/cause from PostgreSQL and resumes the serial stream.

## Policy snapshot and configuration lineage

WP-06 evaluates a result against the immutable policy snapshot associated with that result's acknowledged `configurationVersion`, never against unrelated current configuration. The durable immutable policy-content mapping is `(probeId, configurationVersion) -> policySnapshotId`. The snapshot contains failure/recovery values, approved quality classification, lateness/skew settings, maintenance/flapping/incident policy references, and policy version. A buffered result resolves content through its own ledger `probeId` and `configurationVersion`; the disposition stores the resolved snapshot ID/version.

A configuration version is effective separately for each Agent. The durable per-Agent effective-boundary mapping is `(agentId, configurationVersion) -> appliedAcknowledgementReceivedAt`, where the value is the database timestamp at which the Server durably persists that Agent's `Applied` acknowledgement. The state processor compares PostgreSQL ledger `receivedAt`, not Agent-reported time, to the ledger row's Agent/version boundary: a result received earlier is `historical-other: config-not-effective`; a later result uses the immutable policy-content mapping for its Probe/version. This does not assume one Agent per Agent Group. A configuration-effective synthetic cause carries the exact source configuration version and its snapshot ID. Maintenance, enable/disable, and expiry causes carry the policy snapshot effective at their persisted database source boundary and store it when the cause is created. They must not be re-evaluated under a later policy version after retry. If a ledger row or cause cannot resolve its required immutable lineage, it receives `historical-other: policy-lineage-unresolved`, has no state/incident/lifecycle-event effect, and remains auditable for recovery.

## Precedence, freshness, and watermark

Evaluation always uses UTC and this precedence order:

1. A disabled Device, Probe, Agent Group, or otherwise unschedulable Probe is `DISABLED`. It is not scheduled and cannot open an incident.
2. For an enabled Probe with an active applicable maintenance window, the visible state is `MAINTENANCE`; results and underlying counters continue.
3. Outside maintenance, an expired heartbeat authority Agent or expired result freshness makes the visible state `UNKNOWN`, never `DOWN`.
4. Otherwise expose the underlying result-driven state: `UP`, `DEGRADED`, `DOWN`, or `RECOVERING`.

`agentHeartbeatExpiresAt` is the WP-03/ADR-008 Server-time boundary: `lastHeartbeatReceivedAt + max(60 seconds, 3 × heartbeatInterval)`. Its authority is exclusively `ProbeStatusProjection.WatermarkAgentId`: the Agent of the latest accepted `StateDriving` result that owns the projection watermark. It is not an inventory assignment and must never be inferred or backfilled from Agent Group membership. A later accepted `StateDriving` result replaces authority through the existing watermark update; non-`StateDriving` results do not replace it. A missing projection, null watermark Agent ID, or null authority heartbeat produces no heartbeat-expiry candidate. `freshUntil` is `lastFreshEventAt + max(2 × probeInterval, heartbeatGrace)`, where `heartbeatGrace` is the same approved Agent-expiry duration unless an approved configuration explicitly separates it. Expiry is evaluated by a deterministic periodic worker and at result processing. Expiry preserves success/failure counters. An expiry-triggered `UNKNOWN` leaves an active incident unchanged and creates no `IncidentLifecycleEvent` or `NotificationSuppressionContext`.

Phase 1 freezes the heartbeat-expiry persistence contract only. A heartbeat cause binds the complete accepted projection watermark (`sourceCursorEventAt`, authority/watermark Agent ID, and `sourceResultId`) plus the authority heartbeat generation (`sourceLastHeartbeatReceivedAt`, `sourceHeartbeatIntervalSeconds`). This deterministic source identity distinguishes heartbeat generations and interval changes. The cause’s due time is derived internally as `sourceLastHeartbeatReceivedAt + max(60 seconds, 3 × sourceHeartbeatIntervalSeconds)`; callers supply neither arbitrary due time nor requested time. Phase 2 will validate the exact mutable heartbeat timestamp/interval values in PostgreSQL; Phase 3 will implement the coordinator and locking.

The Phase-3 runtime protocol is normative. Every public result, freshness, heartbeat-receipt (H2), and heartbeat-expiry (H1) operation uses one PostgreSQL `ReadCommitted` transaction and one commit. Its global lock order is the canonical Agent row set, the canonical Probe advisory-lock set, then projection rows. Both sets are sorted by lowercase UUID-D using ordinal comparison. No path may acquire an Agent row after acquiring a Probe lock. Before a Probe lock, a coordinator pre-scans its complete required Agent set, locks that set (`FOR SHARE`, or `FOR UPDATE` for the H2 writer), acquires Probe locks, re-reads the required set, and explicitly rolls back then retries with a fresh transaction if it changed; it performs no writes before that stable-set decision.

H2 locks its receiving Agent `FOR UPDATE`, updates its heartbeat and receipt in that same transaction, acquires all affected projection Probe locks in canonical order, locks and revalidates each projection, and materializes one exact successor heartbeat cause for every qualifying watermark owned by that Agent. Receipt, Agent update, and all successor causes commit together; duplicate receipt replay returns the stored response and creates no cause.

Result/freshness/H1 coordinators lock every pre-scanned source/authority Agent `FOR SHARE` before their Probe lock. H1 selects its candidate optimistically without a due-time predicate; after stable Agent and Probe locking it captures exactly one `date_trunc('microseconds', clock_timestamp())` cutoff, drains rows received at or before it in frozen state order, and processes at most one due heartbeat cause. Candidate replacement never produces a false `NoPending`/`NoDueCause`: it rolls back and retries. Projection watermark and authority heartbeat generation are revalidated while all locks remain held. H1-first makes H2 wait and may apply `UNKNOWN`; H2 then creates its successor. H2-first makes H1 observe the advanced generation and record `authority-heartbeat-advanced`. A result/H1 race is resolved by the same cutoff drain: a pre-cutoff state-driving result supersedes H1, while post-cutoff work remains for the next transaction.

Before an expiry cause can transition a Probe to `UNKNOWN`, the locked coordinator captures `expiryCutoffReceivedAt` from the database clock after it holds the Probe lock. Because ingestion uses the same lock, every ledger row committed before that capture is visible and every competing ingestion commits after the expiry transaction releases the lock. The coordinator drains all undisposed rows with `receivedAt <= expiryCutoffReceivedAt` in `stateOrderKey` order, then evaluates expiry against that exact cutoff. A row is a `freshnessCandidate` only when it is valid for the Probe lineage, its `endedAt` is later than the cursor, and it passes the approved future/skew eligibility rule. Thus a fresh committed result awaiting processing prevents a false `UNKNOWN`; a result committing after the cutoff is deterministically evaluated by the next result/cause transaction.

For every accepted ledger result, record clock-skew metadata from `abs(eventAt - receivedAt)`. A row is state-driving only when `receivedAt - 5 minutes <= eventAt <= receivedAt + 60 seconds` and its `stateCursorKey` is strictly greater than the persisted cursor. An older row receives `beyond-approved-lateness`; a future-dated row receives `future-or-skew-suspect`; both are historical-only. Thus a result received more than five minutes after `eventAt` remains auditable history but cannot affect current state or incidents. Distinct eligible results with the same `eventAt` are processed in `(agentId, resultId)` order. A later-arriving row whose cursor key is lower receives `late-order` and is historical-only. A state-driving result advances all three cursor columns in the same transaction as its projection.

Every retained ledger row receives exactly one immutable processing disposition in the transaction that claims it: `state-driving`, `late-order`, `future-or-skew-suspect`, `beyond-approved-lateness`, `disabled`, or `historical-other` with a bounded reason code. The record stores its `resultKey`, `ProbeId`, `stateCursorKey`, resolved policy snapshot ID/version, decided-at time, and any transition/incident/cause references. WP-05 delivery duplicates create no second ledger row: they resolve to the existing row and its already durable disposition. A retry after a crash reads that disposition rather than emitting another transition or lifecycle event. This disposition record is required even for rows that do not change state.

## Result classification and state machine

`Failure` means an accepted WP-04 non-success outcome. The existing v1 `WarningRttMilliseconds` configuration field is the RTT warning threshold: newly configured Probes default it to 500 ms, it retains existing positive-value validation, and it is compared with result `AverageRttMilliseconds` without renaming the public contract. `warningPacketLossRatio` is an internal nullable policy input using the frozen result contract's decimal-ratio representation: 5% is `0.05m`; configured values are greater than 0 and at most 1; and `null` means that quality dimension is not evaluated. A future additive configuration-contract field requires Lead compatibility review in a later wave. `DegradedSuccess` means a success whose available `AverageRttMilliseconds` or `PacketLossRatio` measurement is greater than or equal to its configured warning threshold. A null `AverageRttMilliseconds` cannot breach the RTT threshold, but a configured packet-loss threshold is still evaluated normally. Compare only configured thresholds: an unconfigured dimension is ignored, and a success with no available measurement that breaches a configured threshold is `HealthySuccess` and therefore `UP`. Critical latency/loss incident rules are disabled for MVP.

Counters change only for a state-driving result:

| Input | Counter update | Resulting underlying state |
| --- | --- | --- |
| `Failure` while `RECOVERING` | `failure = 1`; `success = 0` | Immediately `DOWN`, with `recovery-failed`. |
| `Failure` while not `RECOVERING` | `failure = min(failure + 1, failureThreshold)`; `success = 0` | Keep `UNKNOWN`, `UP`, or `DEGRADED` below threshold; enter `DOWN` at threshold; an already `DOWN` Probe remains `DOWN`. |
| `HealthySuccess` or `DegradedSuccess` while not `DOWN`/`RECOVERING` | `success = min(success + 1, recoveryThreshold)`; `failure = 0` | `UP` or `DEGRADED` immediately. |
| Success after `DOWN` or while `RECOVERING` | `success = min(success + 1, recoveryThreshold)`; `failure = 0` | `RECOVERING` while `success < recoveryThreshold`; otherwise quality-derived `UP` or `DEGRADED`. |

The first state-driving success after bootstrap makes an `UNKNOWN` Probe `UP`/`DEGRADED`; recovery threshold applies only after a confirmed `DOWN`. Counters saturate at their respective thresholds and therefore never overflow. A newly opened `availability-down` incident starts with `OccurrenceCount = 1`: its opening failure is the first outage occurrence. A failure while `RECOVERING` returns immediately to `DOWN`, sets recovery success to zero and failure count to one, retains the active incident, and increments its `OccurrenceCount` exactly once. Its transition reason is `recovery-failed`; it emits one `occurrence` `IncidentLifecycleEvent` with paired `NotificationSuppressionContext` `suppressed` for `recovery-failed`; WP-08 owns delivery interpretation. Its immutable `lifecycleEventKey` is exactly `occurrence:<sourceResultId.ToString("D").ToLowerInvariant()>` (47 characters), which is unique per source result under `(incidentId, lifecycleEventKey, policyVersion)`. Retry/replay of that source result cannot increment the count or create another event/context. A failure while already `DOWN` remains `DOWN` and only updates its bounded failure counter; it does not increment `OccurrenceCount`. A success while `UP`/`DEGRADED` applies quality classification and records a transition only when that quality-derived state changes: `UP` to `DEGRADED` is `quality-degraded`, and `DEGRADED` to `UP` is `quality-restored`. A `recoveryThreshold` of one lets `DOWN` transition directly to quality-derived `UP`/`DEGRADED` with `recovery-threshold-met`. A result that is historical-only, duplicate, disabled, or suppressed by the cursor changes no counter. Re-entering the same visible state produces no transition record.

The transition reasons are fixed, bounded codes: `bootstrap-success`, `quality-degraded`, `quality-restored`, `failure-threshold-met`, `recovery-pending`, `recovery-threshold-met`, `recovery-failed`, `agent-heartbeat-expired`, `result-freshness-expired`, `maintenance-started`, `maintenance-ended`, `disabled`, and `enabled-awaiting-result`. A maintenance end recomputes the visible state from the persisted underlying state/freshness; it does not replay buffered results.

## Maintenance, disabled, and flapping policy

Maintenance takes visual precedence only. It never stops result persistence, cursor advancement, or underlying counters. Maintenance-time transitions and counters are durable history but do not contribute to flapping activation or exit. A `DOWN` first confirmed during maintenance creates no incident, `IncidentLifecycleEvent`, or `NotificationSuppressionContext`; recovery during maintenance does not resolve an active incident. At maintenance end, `UP`/`DEGRADED` resolves an active incident only if flapping is inactive; `DOWN` opens an incident if absent or retains the active incident; `RECOVERING` retains the active incident; and `UNKNOWN` leaves it unchanged. If flapping is active, retain the incident and restart the 30-minute stable-recovery timer from the maintenance-end database boundary. Lifecycle events created at maintenance end have an `eligible` paired `NotificationSuppressionContext`; WP-08 owns delivery interpretation.

Disabling immediately gives `DISABLED`, clears no history, and does not open a new incident. It resolves an existing active incident with actor `system-policy` and reason `probe-disabled`, producing one `resolved` `IncidentLifecycleEvent` with paired `NotificationSuppressionContext` `suppressed` for `probe-disabled`. Re-enabling starts at `UNKNOWN` and waits for fresh, state-driving evidence; it does not reuse prior counters to manufacture a `DOWN`.

Flapping is an incident-lifecycle policy, not a suppression of history or current-state accuracy. It activates on the third confirmed `DOWN` in a rolling 15-minute window, using persisted qualifying state-transition `eventAt`; the prior DOWN transitions must be separated by confirmed recoveries. Before activation, normal confirmed recovery auto-resolves. On activation, retain the current active incident. If absent, open it and give its `opened` `IncidentLifecycleEvent` a paired `NotificationSuppressionContext` `suppressed` for `flapping-activated`; if already active, retain it without a second opening. While flapping, confirmed recovery does not resolve; every subsequent confirmed `DOWN` increments occurrence exactly once and emits an `occurrence` event with a `suppressed` context for `flapping-active`. A durable synthetic cause uses the database clock and resolves only after 30 continuous healthy (`UP`/`DEGRADED`) minutes; its `resolved` event has an `eligible` context for `flapping-stable-recovery`.

## Incident lifecycle and invariants

An incident is keyed by `(probeId, ruleKey)`, where initial `ruleKey` is `availability-down`; later critical quality rules require a separately versioned rule key. At most one incident in `OPEN` or `ACKNOWLEDGED` exists for that key. The database must enforce that partial uniqueness, not rely only on worker serialization.

| Trigger | Incident action | Invariant |
| --- | --- | --- |
| Enter `DOWN` from a non-`DOWN` underlying state | Open `OPEN` incident if none exists; otherwise apply the UA-01-approved occurrence/disposition rule for the active incident. | A first confirmed availability DOWN opening is idempotent by result/cause key and emits `opened` with paired `NotificationSuppressionContext` `eligible/availability-down`. |
| Operator acknowledgement | `OPEN -> ACKNOWLEDGED`; persist actor, UTC time, and required bounded comment. | Never creates a second incident. Its public-action lifecycle contract, including any event/context, is outside this WP-06 engine checkpoint and requires later compatibility review. |
| Confirmed recovery | Outside maintenance and active flapping, resolve automatically with actor `system-policy` and reason `confirmed-recovery`. | Emits `resolved` with paired `NotificationSuppressionContext` `eligible/confirmed-recovery`; maintenance/flapping precedence is defined above. |
| Authorized manual resolution | `OPEN`/`ACKNOWLEDGED -> RESOLVED`; persist actor, UTC time, and required note. | It does not alter Probe state or counters. Its public-action lifecycle contract, including any event/context, is outside this WP-06 engine checkpoint and requires later compatibility review. |
| Later confirmed `DOWN` after a resolved incident | Open a new incident. | A resolved incident is immutable apart from allowed comments/audit. |

`OpenedAt <= AcknowledgedAt <= ResolvedAt` when present. An acknowledged/resolved actor and comment/note are mandatory for user actions; system resolution uses a bounded `system-policy` actor/reason. `openIncidentId` is null exactly when no active incident for `availability-down` is linked to the Probe. Downtime is derived from transition/incident timestamps; ADR-012 defines the WP-09 availability, coverage, UNKNOWN, maintenance, disabled, and flapping policy.

Heartbeat/freshness `UNKNOWN` leaves an active incident unchanged and creates no `IncidentLifecycleEvent` or `NotificationSuppressionContext`. Failure during `RECOVERING` follows the approved immediate-DOWN, retained-incident, single-occurrence policy above.

## Incident lifecycle outbox and suppression context

`IncidentLifecycleEvent` is the ADR-004 channel-neutral transactional outbox record. For each incident lifecycle event, WP-06 writes one immutable event, unique on `(incidentId, lifecycleEventKey, policyVersion)`, in the same PostgreSQL transaction as the state transition and incident mutation that caused it. The record carries a stable `eventId`, incident ID, bounded lifecycle event type, immutable lifecycle-event key, policy snapshot ID/version, occurred-at UTC time, and references to the transition/cause and processing disposition. Its paired `NotificationSuppressionContext`, with the same unique key, records `eligible`, `suppressed`, or `policy-unapproved`, a bounded reason code, evaluated-at UTC time, and the same policy/causality references.

The committed `IncidentLifecycleEvent` is the durable, idempotent WP-06-to-WP-08 handoff. WP-08 consumes by stable `eventId`; replay/claim is safe because the event remains immutable and consumer handoff is durably deduplicated by `eventId`. A hard-suppressed or policy-unapproved context is part of that handoff and must be preserved. WP-06 creates neither provider work nor channel-specific records. Channel fan-out, providers, channels, retries, claims, and delivery are exclusively WP-08 scope.

## Atomic processing and contract boundary

WP-05 acknowledges a result after its immutable ledger transaction. WP-06 consumes committed ledger rows asynchronously; it never changes acknowledgement semantics or calls a notification provider.

For every claimed ledger row or synthetic cause, a single PostgreSQL transaction must:

1. Claim the unprocessed result/cause and lock the Probe state row (`FOR UPDATE` or equivalent optimistic-concurrency retry).
2. Resolve and persist the immutable policy lineage, then evaluate enablement, maintenance, Agent expiry, freshness, skew, cursor, and classification from committed data.
3. Insert the immutable processing disposition for the claimed input in all cases. A historical-only ledger row (late-order, future/skew, disabled, or other historical reason) performs this step only; it changes no state, incident, lifecycle event, or suppression context.
4. For a state-driving result or applicable cause only, update the state cursor/counters and append a transition when the visible state changes.
5. Create or mutate the incident and append its audit record when applicable; atomically insert its channel-neutral `IncidentLifecycleEvent` and `NotificationSuppressionContext` per `(incidentId, lifecycleEventKey, policyVersion)`.
6. Commit all of the above, or commit none of it.

WP-08 performs channel fan-out and provider calls outside this transaction. PostgreSQL failure leaves the ledger eligible for retry; retry sees the processed marker/unique constraints and produces no duplicate transition, incident, lifecycle event, or suppression context. VictoriaMetrics projection failure does not roll back this transaction. No WP-06 public route is proposed: existing incident action routes, response DTOs, SignalR events, and OpenAPI additions require Lead-owned v1 compatibility review before implementation.

## Table-driven acceptance matrix

| ID | Setup/input | Required outcome |
| --- | --- | --- |
| ST-01 | Bootstrap `UNKNOWN`; first fresh success with approved healthy classification | `UP`, one `bootstrap-success` transition, no incident mutation or `IncidentLifecycleEvent`. |
| ST-02 | `UP`; failures below configured threshold; then success | No `DOWN`/incident; failure counter resets and state returns/remains quality-derived. |
| ST-03 | Failure reaches threshold | One `DOWN` transition, exactly one active availability incident, and one `opened` event with `eligible/availability-down` context. |
| ST-04 | `DOWN`; successes below then at recovery threshold outside maintenance/flapping | `RECOVERING` then quality-derived `UP`/`DEGRADED`; resolve once with `resolved` event and `eligible/confirmed-recovery` context. |
| ST-05 | Failure during `RECOVERING` | Immediately return to `DOWN` with `recovery-failed`, set success to zero and failure to one, retain the active incident, increment `OccurrenceCount` once, and emit one suppressed `recovery-failed` occurrence event/context keyed `occurrence:<lowercase-source-result-id>`. Retry/replay creates neither a second increment nor a second event/context; ordinary failures already `DOWN` do not increment. |
| ST-06 | Two workers contend for one Probe with multiple already committed rows, including distinct rows sharing `endedAt` | Rows receive dispositions in lexicographic `(endedAt, agentId, resultId)` order; each distinct cursor key is evaluated once, and results/timers/configuration causes are serial. |
| ST-07 | Exact duplicate delivery / retry of a result key | No new WP-05 ledger row; the existing durable processing disposition, transition, incident mutation, `IncidentLifecycleEvent`, and `NotificationSuppressionContext` are reused at most once. |
| ST-08 | Out-of-order/lower-cursor, disabled, beyond-lateness, future/skew-suspect, or lineage-unresolved row | Every retained ledger row has exactly one durable disposition/reason and resolved snapshot reference where available; historical-only rows do not mutate cursor/counters/state/incidents/lifecycle context. |
| ST-09 | Result commit races an expiry cause | Shared Probe lock produces an exact `expiryCutoffReceivedAt`: a pre-cutoff committed freshness candidate is drained before expiry; a post-cutoff commit is handled by the next transaction. |
| ST-10 | Heartbeat crosses WP-03 expiry or fresh-result age crosses `max(2I, grace)` with no freshness candidate at cutoff | `UNKNOWN` transition once and counters preserved; active incident remains unchanged and no lifecycle event/context is created. |
| ST-11 | Buffered result spans a configuration/policy change; synthetic cause retries | Result/cause uses and records its immutable resolved policy snapshot, never the unrelated current policy. |
| ST-12 | Active maintenance with failures/recovery | Results/counters persist and visible state is `MAINTENANCE`; no maintenance-time incident event/context occurs. Maintenance end applies the approved underlying-state and flapping rules. |
| ST-13 | Disable with fresh data or an active incident; re-enable | `DISABLED` immediately, history preserved, no new incident opens, and any active incident resolves once with suppressed `probe-disabled` event/context; re-enable is `UNKNOWN` until new fresh evidence. |
| ST-14 | Alternating down/recovery crosses the third qualifying DOWN in 15 minutes | Transitions/history are retained; activation and subsequent occurrences receive the approved suppressed contexts, and the database-clock stable-recovery cause resolves after 30 healthy minutes. Delivery interpretation remains WP-08 scope. |
| ST-15 | Crash/failure before commit and after commit | No partial state/incident/lifecycle-event/suppression-context; retry is safe and WP-06 never invokes a provider. |
| ST-16 | Successful result at exactly configured RTT or packet-loss warning threshold; null RTT with configured packet-loss threshold | Equality is `DEGRADED`; null RTT cannot breach RTT but packet loss is evaluated normally; if no available measurement breaches a configured threshold, result is `UP`. |
| ST-17 | Repeated failures while already `DOWN`; repeated successes while `UP`/`DEGRADED`, including a quality change | Counters saturate and never overflow; an already `DOWN` Probe emits no transition; same quality-derived state emits no transition; `UP` to `DEGRADED` is `quality-degraded` and `DEGRADED` to `UP` is `quality-restored`. |
| ST-18 | `DOWN` with `recoveryThreshold = 1`; first healthy or degraded success | Transition directly to quality-derived `UP`/`DEGRADED` with `recovery-threshold-met`, without an intermediate `RECOVERING` transition. |

## UA-01 approved policy

ADR-012 is the binding record for all nine UA-01 operational decisions: threshold and
quality classification; automatic recovery and disable; failure during `RECOVERING`;
heartbeat/freshness `UNKNOWN`; flapping; lateness/skew; policy-snapshot boundaries;
availability; and maintenance. WP-06 implementation must use the approved policy
snapshot/version and add deterministic acceptance coverage; it must not substitute a
later current policy for a persisted snapshot.

## Explicit exclusions

This checkpoint excludes UI/dashboard and SignalR work; notification delivery providers, retries, quiet hours, and escalation delivery; reporting and availability calculation; packaging/deployment and Windows-Service operational proof; real ICMP or network probing; migrations/projects/packages/tests/production code; and unrelated Agent changes. WP-05 delivery/ingestion remains unchanged.

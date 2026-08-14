# WP-04 Agent probe-runtime contract

Status: Approved and frozen - design only
Date: 2026-08-14 (Asia/Bangkok)
Owner: Lead/Integration Agent

## Goals and non-goals

WP-04 turns an acknowledged WP-03 configuration snapshot into bounded local ICMP execution and immutable local results. It has no HTTP API, OpenAPI, PostgreSQL migration, SQLite queue, batch/upload, central ingestion, status calculation, incidents, notifications, frontend, packaging, or deployment work.

## Configuration-to-schedule mapping

Only an `Applied` immutable WP-03 snapshot is schedulable. Every enabled ICMP Probe maps to one schedule keyed by `(ProbeId, probeConfigVersion)`. Its target must be a normalized IPv4 literal and must pass the configured Probe scope plus the locally persisted AllowedNetworks ceiling; hostname/DNS input, invalid IPv4, or any scope failure rejects the whole snapshot and preserves last-known-good schedules. Disabled Probes map to no schedule.

For interval `I`, the initial offset is a stable hash of installation identity, Probe ID, and configuration version reduced to `[0, I)`. Future slots follow the monotonic cadence. A wall-clock adjustment cannot move cadence. After a suspend, delay, or blocked admission, only the next future slot is considered; missed slots are counted and coalesced, never replayed.

## Lifecycle and concurrency

`NotApplied -> Validating -> Scheduled -> Admitted -> Running -> ResultProduced -> Scheduled` is the normal lifecycle. Invalid configuration transitions to `Rejected` while the last-known-good schedule remains active. Shutdown/revocation transitions work to `Cancelling -> Stopped` and creates no target-failure result.

Admission is bounded: (1) acquire global permit; (2) acquire normalized-target permit; (3) acquire the per-Probe non-overlap guard; then start transport. Release is reverse order in a `finally` path. If any acquisition cannot proceed within bounded scheduler policy, release prior permits, increment skipped/admission metrics, and do not enqueue work. Defaults: global 64, range 1–256; per normalized target 1, range 1–8.

## Run and result semantics

Attempts are sequential. Each has the configured timeout; the default delay between attempts is 250 ms and is cancellation-aware. A run has UTC `startedAt` and `endedAt`, measured duration from a monotonic source, and:

```text
LocalProbeResult
  configurationVersion: int64
  probeId: UUID
  startedAt: RFC 3339 UTC
  endedAt: RFC 3339 UTC
  attemptCount: integer
  successfulAttemptCount: integer
  packetLossRatio: decimal [0,1]
  minRttMilliseconds: decimal|null
  averageRttMilliseconds: decimal|null
  maxRttMilliseconds: decimal|null
  errorCategory: Timeout|Unreachable|PermissionDenied|InvalidTarget|
                 NetworkUnavailable|Cancelled|TransportError|null
```

RTT aggregates are null when there is no successful reply; packet loss is `(attemptCount - successfulAttemptCount) / attemptCount`. The result is immutable once produced. `Cancelled` records local lifecycle telemetry only and is not emitted as a target-failure result; no attempt result exists if cancellation occurs before transport begins.

Windows transport mapping must be deterministic: elapsed timeout maps to `Timeout`; ICMP destination/network/host unreachable replies map to `Unreachable`; access-denied/privilege failures map to `PermissionDenied`; local IPv4/scope validation maps to `InvalidTarget`; known local network-adapter-unavailable errors map to `NetworkUnavailable`; caller/host cancellation maps to `Cancelled`; all other platform/socket/ICMP errors map to `TransportError`. The implementation must preserve only a sanitized diagnostic code for logs, not arbitrary exception text.

## Security, privacy, observability, and health

- No credentials, protected raw configuration, or unnecessary raw target addresses appear in logs. Use Probe ID and an installation-scoped non-reversible target hash.
- Metric labels exclude Probe ID, target address, hostname, exception text, and unbounded platform codes. Required cardinality-safe metrics: scheduled runs; outcomes by fixed category; latency histogram; packet-loss histogram; global/target in-flight gauges; bounded queue-depth gauge; skipped-run counter by fixed reason; scheduler-lag histogram.
- Individual target failures leave Agent self-health healthy. Scheduler loop failure, invalid applied configuration, or unusable transport changes self-health to degraded or unhealthy. Alert delivery is WP-08 scope.

## Ownership and review gates

| Agent | Responsibility and gate |
| --- | --- |
| A — Backend | Confirm no API/contract/migration expansion and that WP-03 configuration invariants remain intact; Lead review required. |
| B — Probe Agent | Implement the local scheduler, transport adapter, lifecycle, bounded admission, metrics/logging, and deterministic Agent tests. |
| C — Frontend | No implementation work; confirm no frontend/API impact. |
| D — QA/Security | Own the test matrix and review network containment, redaction, boundedness, cancellation, and race coverage. |

Any change to target type, scope enforcement, ranges/defaults, result fields/categories, health semantics, or WP-04 boundary returns to Lead approval. WP-05+ owns delivery durability, ingestion and all central outcomes.

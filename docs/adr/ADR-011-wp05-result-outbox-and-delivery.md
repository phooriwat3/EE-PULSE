# ADR-011: Durable Probe-result outbox and idempotent delivery

Status: Accepted and implemented in WP-05 (merged `2c22766`, PR #5)
Date: 2026-08-20 (Asia/Bangkok)

## Context

WP-04 produces immutable local Probe results but deliberately does not persist or deliver them. Agents can be disconnected, restarted, or crash between producing a result and receiving a Backend response. The system therefore needs a bounded durable hand-off without making central status, incidents, notifications, real ICMP, service packaging, or deployment part of WP-05.

ADR-006 already selects a local SQLite WAL queue and requires deletion only after server acknowledgement. ADR-002 assigns PostgreSQL to authoritative workflow/idempotency records and VictoriaMetrics to raw and aggregate time-series data.

## Decision

- The Agent writes every completed, deliverable WP-04 result to a SQLite outbox before attempting HTTP delivery. The database uses WAL mode, transactional writes, foreign keys, and a crash-safe synchronous setting selected and verified during implementation. A result is visible to the sender only after its insert transaction commits.
- Each result receives an Agent-generated, immutable UUID `resultId` in the same transaction as its payload. The payload has an explicit `resultSchemaVersion` (initially `1`), Agent ID, Probe ID, configuration version, UTC event timestamps, and the immutable WP-04 outcome fields. A retry serializes the identical logical result and identity.
- The sender selects pending records in insertion sequence, with bounded batches by both item count and serialized bytes. It does not overtake an earlier pending record. One successful acknowledgement transaction marks or checkpoints only the explicitly acknowledged records; a crash before that transaction leaves them eligible for retry. Acknowledged records are not redelivered and are physically removed only by cleanup within 24 hours.
- Delivery is at-least-once. The Backend deduplicates durably on `(agent_id, result_id)` and returns acknowledgements that are safe to replay. A lost response therefore creates a harmless duplicate request, not a duplicate accepted result.
- The Agent applies bounded exponential retry with jitter for retryable transport, authentication-transient, throttling, and server failures. It respects a server retry hint only when bounded by the local maximum. Authentication configuration refresh is coordinated with the existing WP-03 credential/configuration mechanisms; credentials and authorization headers are never persisted in result payloads or logs.
- PostgreSQL commits the accepted immutable ingestion record and deduplication ledger atomically before the Backend acknowledges the Agent. VictoriaMetrics is an asynchronous projection of that durable record. A VictoriaMetrics outage must leave the PostgreSQL record pending for projection and must not cause an acknowledged result to be silently lost.
- The approved MVP outbox quota defaults to 5 GB per Agent. The Agent reserves the greater of 2 GB or 10% of the hosting volume. At 80% of quota it reports degraded storage health. At 95% of quota, or when the reserve would be breached, it stops new Probe-result production and scheduling while continuing delivery and recovery operations; it resumes production and scheduling automatically only below 70% of quota and after the reserve is no longer breached.
- Acknowledged records may be removed by the documented cleanup process within 24 hours. Unacknowledged records are never removed based only on age, and silent result loss is prohibited. Corrupt outbox files are quarantined and preserved for operator recovery.

## Consequences

- Agent and Backend restarts recover from committed SQLite/PostgreSQL state; no in-memory acknowledgement is authoritative.
- FIFO constrains backlog drain ordering, while the Backend must still accept duplicate and late historical data. Event-time state/watermark decisions remain WP-06.
- PostgreSQL is the authoritative ingestion boundary and recovery source. VictoriaMetrics holds query-optimized samples only and does not decide idempotency, acknowledgements, status, incidents, or retention policy.
- Corrupt SQLite files are quarantined rather than overwritten. The Agent stops delivery, exposes a non-secret recovery state, and follows the WP-05 recovery runbook. Recovery must not invent acknowledgements.
- This ADR proposes an additive result-ingestion contract only. It does not modify frozen WP-03/WP-04 contracts or generated OpenAPI; implementation requires Lead-owned contract/version review.

## Explicit exclusions

WP-05 excludes status and incident calculation, notifications, frontend/dashboard work, IP discovery or scanning, real ICMP implementation, Windows Service packaging, production deployment, OIDC/TLS implementation, and high availability or multi-region behavior.

## Approval record

UA-11 approved the MVP quota, reserve, thresholds, suspension/resumption behavior, acknowledged-record cleanup, corruption preservation, and prohibition on silent loss on 2026-08-20. Any change to those values or to the behavior of unacknowledged records requires renewed Product/Data/Operations approval.

# WP-05 delivery acceptance plan

Status: Proposed - design only
Date: 2026-08-20 (Asia/Bangkok)

## Test method

All tests use deterministic fake time, HTTP transport, SQLite fault seams, PostgreSQL test storage, and a VictoriaMetrics writer seam. No real ICMP, network scanning, Windows Service packaging, OIDC/TLS, deployment, notification, status, incident, or frontend test is part of this plan. Random jitter is seedable and recorded by test name.

## Acceptance matrix

| ID | Failure or scenario | Required evidence |
| --- | --- | --- |
| OUT-01 | Result production followed by process crash before/after SQLite commit. | Only committed result is sent after restart; exactly one immutable `resultId` per committed record. |
| OUT-02 | Crash after HTTP send and before local acknowledgement transaction. | Retry occurs; Backend retains one `(agentId, resultId)` ledger record; repeated acknowledgement is safe. |
| OUT-03 | Crash after local acknowledgement transaction and cleanup. | Acknowledged row is not redelivered after restart and is physically removed by cleanup within 24 hours; no unacknowledged row is deleted. |
| OUT-04 | FIFO backlog crosses item and byte batch limits. | Batches are bounded and ordered; no later pending row overtakes an earlier one. |
| OUT-05 | Timeout, 429, 5xx, offline interval, and recovery. | Bounded exponential full-jitter retry; no busy loop; backlog drains after recovery. |
| OUT-06 | 401/403 and 410 responses. | Pending data remains; credential recovery is attempted only for 401/403; 410 stops delivery without deletion. |
| OUT-07 | Duplicate request, duplicate envelope, and same identity with altered payload. | Duplicates are acknowledged without duplicate ledger rows; altered identity collision is rejected and surfaced safely. |
| OUT-08 | Partial acknowledgement and permanent per-record rejection. | Only named accepted rows are removed; rejection moves durably to quarantine and later FIFO delivery continues. |
| OUT-09 | 80%, 95%, reserve breach, and below-70% recovery; full disk during enqueue/checkpoint. | 80% reports degraded storage health; 95% or reserve breach stops new production/scheduling but delivery/recovery continue; automatic resume occurs only below 70% with reserve restored; no silent or age-based unacknowledged deletion. |
| OUT-10 | SQLite WAL recovery, integrity-check failure, and partial corruption. | Complete evidence set is quarantined; read-only replay preserves IDs; no fabricated acknowledgement. |
| OUT-11 | PostgreSQL transaction failure and VictoriaMetrics projection failure. | No acknowledgement before PostgreSQL ledger commit; projection retry occurs without re-ingesting or Agent replay requirement. |
| OUT-12 | Unsupported schema, configuration lineage mismatch, and version skew. | Stable permanent reason or compatible processing; no field reinterpretation and no silent version downgrade. |
| OUT-13 | Log/metric capture under all failures. | No token, raw payload, target address, exception text, or unbounded label; required fixed counters/gauges/histograms present. |

## Determinism and fault injection

The implementation must inject clock, random source, process-boundary checkpoint, SQLite transaction/fsync error, free-space reading, HTTP response, PostgreSQL transaction, and VictoriaMetrics writer behavior. Tests assert durable state directly after each injected boundary and rerun the same seeded schedule to prove reproducibility.

## Exit criteria

All matrix cases pass with documented seeds. The implementation demonstrates at-least-once delivery, durable deduplication, acknowledgement-only deletion, bounded batching/retry, recovery without invented acknowledgements, and the approved 5 GB/reserve/threshold behavior. Load and 30-minute outage performance evidence remain a later approved execution gate; this design plan does not claim it.

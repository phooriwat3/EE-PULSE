# WP-05 outbox recovery runbook

Status: Proposed - design only
Date: 2026-08-20 (Asia/Bangkok)

## Purpose and boundary

Use this runbook for a local Agent outbox that cannot deliver, reports quota/disk pressure, or detects SQLite integrity failure. It covers evidence preservation and recovery planning only. It does not authorize service installation, production network changes, credential handling, data deletion, status/incident repair, or deployment.

## Safe triage

1. Record the Agent ID, installation-scoped target hash if present, result-ID range/count, queue depth/bytes/oldest age, retry band, and fixed failure reason. Do not collect request bodies, tokens, raw target addresses, or exception text.
2. Distinguish a retryable central/network failure from local disk pressure, authentication/revocation, quota exhaustion, or SQLite corruption using only bounded diagnostic codes and health metrics.
3. Preserve the database file, WAL, and SHM as one consistent evidence set before any repair. Do not delete, recreate, or copy only one member of the set while the Agent can write it.
4. For retryable failures, leave the Agent to apply its bounded backoff. Do not manually force repeated uploads or delete rows to clear the queue.

## Disk pressure and quota

The approved MVP quota is 5 GB per Agent, with a reserve equal to the greater of 2 GB or 10% of the hosting volume. At 80% of quota, raise the fixed `outbox_disk_pressure` degraded-health state. At 95% of quota, or when the reserve would be breached, stop new Probe-result production and scheduling; delivery and recovery continue. Resume production and scheduling automatically only below 70% of quota and after the reserve is restored.

Preserve every unacknowledged row: it must never be deleted based only on age, and silent result loss is prohibited. Acknowledged rows may be removed through the documented cleanup process within 24 hours. Escalate with only queue bytes, free-space band, configured limit, backlog age, and the fixed pressure state.

If storage is expanded or space is safely recovered, restart normal sender operation and verify that result IDs drain through acknowledged responses without gaps caused by local intervention.

## Corruption or failed integrity check

1. Stop writes through the Agent's controlled lifecycle; do not repeatedly restart it.
2. Quarantine the complete SQLite evidence set with access restricted to authorized operators. Capture checksums and the non-secret health state.
3. Start a new empty outbox only under an approved recovery change. Treat every result not proven committed in the old database as potentially undelivered; do not fabricate acknowledgements or mark it sent.
4. Attempt read-only extraction/replay of intact immutable envelopes into the replacement outbox. Reuse their original `resultId`; Backend idempotency makes replays safe.
5. If extraction cannot recover particular records, record the exact result-ID range/count and follow the approved operations data-loss/incident process. Do not infer central status from the loss; status behavior is WP-06 scope.

## Authentication and Backend recovery

For 401/403, use the existing approved WP-03 credential recovery path and retain pending records. For 410, keep the outbox intact and escalate revocation/re-enrollment handling; do not bypass revocation. If the Backend acknowledges records but VictoriaMetrics is unavailable, no Agent action is needed: the durable PostgreSQL projection backlog owns recovery.

## Exit evidence

Record only: cause category, pre/post queue count and bytes, oldest age, acknowledged/replayed/quarantined result counts, integrity-check result, and approval references. Confirm no secret, target address, raw payload, or unbounded error text entered tickets or logs.

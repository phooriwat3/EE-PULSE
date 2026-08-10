# ADR-008: Server-time Agent liveness and acknowledged configuration

Status: Accepted at the WP-03 contract-design gate  
Date: 2026-08-10

## Context

The Agent must heartbeat every 15–30 seconds, pull versioned configuration, survive invalid configuration, and make new configuration effective only after acknowledgement. The specification does not define heartbeat expiry, clock-skew treatment, acknowledgement ordering, or rollback version semantics.

## Decision

Assign a 20-second heartbeat interval by default. An Agent is ONLINE until its Server-received heartbeat age reaches `max(60 seconds, 3 × assigned interval)`; otherwise it is OFFLINE. Agent-reported time never determines liveness. Absolute clock skew over five minutes is recorded and returned as a warning without rejecting the heartbeat.

Configuration is a complete immutable snapshot with a monotonic Agent Group version and strong ETag. A matching `If-None-Match` returns 304. The Agent validates and commits the complete snapshot atomically, retains the prior last-known-good snapshot, swaps schedules, and then sends an idempotent acknowledgement. Central effective-version state advances only after an `Applied` acknowledgement.

Rollback publishes earlier content as a new higher version. Invalid configuration leaves the last-known-good version active and produces only a stable, sanitized rejection code. Revocation halts the Agent's scheduling and upload seams.

## Consequences

- ONLINE/OFFLINE is deterministic under clock skew and can later drive UNKNOWN state in WP-06.
- Central and Agent views may briefly differ while an applied acknowledgement is retried; both versions remain observable.
- Immutable snapshots consume PostgreSQL space but make rollback and forensic review deterministic.
- Atomic local persistence, acknowledgement idempotency, offline-boundary tests, and rollback tests are mandatory.

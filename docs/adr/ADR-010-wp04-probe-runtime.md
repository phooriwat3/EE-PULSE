# ADR-010: Bounded local ICMP probe runtime

Status: Accepted at the WP-04 design-document checkpoint
Date: 2026-08-14 (Asia/Bangkok)

## Context

WP-03 delivers acknowledged, immutable Agent configuration and a non-expandable local AllowedNetworks ceiling. WP-04 activates only the local ICMP scheduling and result-production runtime. It must not become an unrestricted scanner or create an unbounded catch-up workload after outages, suspension, or clock changes.

## Decision

- WP-04 accepts IPv4 literals only. Hostnames and DNS resolution are rejected. Each target must be valid in Probe configuration scope and contained by the Agent local AllowedNetworks ceiling immediately before execution; configuration can never expand that ceiling.
- The scheduler uses stable-hash deterministic jitter and monotonic elapsed time. A Probe never overlaps itself. Missed slots are coalesced/skipped, never replayed; resume schedules only the next future slot; wall-clock changes do not reschedule monotonic work.
- Admission is bounded and acquires global capacity first, then normalized-target capacity, then the per-Probe execution guard. Defaults are global 64 (range 1–256) and per normalized target 1 (range 1–8). Attempts are sequential, with a default 250 ms inter-attempt delay.
- Each completed run produces an immutable local result with configuration version, Probe ID, UTC start/end timestamps, attempt and success totals, packet-loss ratio, applicable min/average/max RTT, and a stable error category. Cancellation is not a target failure.
- Logs use Probe ID and an installation-scoped non-reversible target hash; metrics never label by Probe ID or target address. Target outcomes do not make the host unhealthy. Scheduler failure, invalid applied configuration, or unusable ICMP transport may degrade or make self-health unhealthy.

## Consequences

- The runtime is predictable under load and cannot create a catch-up burst, but skipped intervals are intentionally not represented as late results.
- IPv4-only scope avoids DNS rebinding and resolution ambiguity; hostname support requires a later ADR and contract change.
- WP-04 has no HTTP/OpenAPI, PostgreSQL, SQLite queue, batching, upload, ingestion, status, incident, notification, frontend, packaging, or deployment change. These remain WP-05+.
- Rollback is configuration rollback through the existing WP-03 last-known-good and acknowledgement model: a rejected/invalid replacement does not replace the active schedule. Runtime rollback is safe because no result is persisted or delivered in WP-04.

## Operational boundary

Automated tests use fake clocks and fake ICMP transports only. Real ICMP requires UA-03 approval of CIDRs, firewall/routing policy, and controlled targets. Windows Service operational evidence requires UA-04; no installation, service-account change, or network change is authorized by this ADR.

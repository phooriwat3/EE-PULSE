# EE Pulse requirements traceability

Last updated: 2026-08-14
Status legend: Not started, In progress, Implemented, Verified, Blocked.

WP-02 backend inventory and its inventory frontend slice are implemented and integration-verified. The contract-neutral Probe Agent foundation is also verified. Status remains partial where later runtime behavior or broader release evidence is required.

## Functional requirements

| Requirement | Delivery WP | Status | Current evidence / remaining acceptance |
| --- | --- | --- | --- |
| FR-01 Device inventory | WP-02, WP-07 | Verified | Backend CRUD, server filtering/pagination, IPv4/hostname validation, enabled-only Site/address uniqueness, disabled/cross-Site IP reuse, hostname reuse, re-enable/concurrent conflicts, CSV row errors, history-preserving disable, Administrator audited delete, PostgreSQL migration, and responsive inventory UI pass component/browser/integration tests. |
| FR-02 Probe configuration | WP-02, WP-04 | In progress | Verified Probe metadata/defaults/threshold validation/config version and UI editing of stable fields, plus deterministic jitter, bounded concurrency, and non-overlap foundations. Configuration delivery and scheduling runtime remain. |
| FR-03 Agent | WP-03-05, WP-10 | In progress | WP-03 verified enrollment, per-Agent credential auth/rotation/revocation, heartbeat/offline state, conditional immutable configuration pull/LKG, acknowledgement/rollback, DPAPI/ACL-backed local state, and dual non-expandable AllowedNetworks enforcement. SQLite queue, result batching/ingestion, installer, and real Windows operational evidence remain. |
| FR-04 Status engine | WP-06 | Not started | Require the full state matrix, thresholds, Agent-expiry UNKNOWN, maintenance, transition history, watermark, and flapping. |
| FR-05 Incident management | WP-06, WP-07 | Not started | Require atomic uniqueness, lifecycle, comments, attribution, resolution, downtime, and occurrence evidence. |
| FR-06 Dashboard | WP-07 | Not started | Health shell only. Require summary/filter/live/NOC/recent-down/offline-Agent/open-incident behavior. |
| FR-07 Device details | WP-07, WP-09 | Not started | Require configuration, metrics ranges, timeline, incidents, Agent, and result-freshness UI/API. |
| FR-08 Notifications | WP-08 | Not started | Require fake SMTP/webhook open/reminder/recovery, dedupe, suppression, retry, and redacted logs. |
| FR-09 Authentication/authorization | WP-02, WP-03, WP-07, WP-11 | In progress | Verified inventory Bearer policies; separate AgentCredential OpenAPI/authentication path; anonymous enrollment bootstrap; token expiry/single-use/replay/revocation; credential rotation; sanitized errors; and production fail-closed Agent HTTPS/proxy configuration. Production OIDC and full role-matrix evidence remain. |
| FR-10 Reporting | WP-09 | Not started | Require Device/Site availability, downtime/counts, safe CSV, maintenance separation, and explicit UNKNOWN coverage. |

## Non-functional requirements

| Requirement | Delivery WP | Status | Current evidence / remaining acceptance |
| --- | --- | --- | --- |
| NFR-01 Performance | WP-05, WP-07, WP-11 | Not started | No ingest/load/dashboard workload. Require 500 targets/30 s for 60 min, 50 average and 250 burst results/s, overview p95 <=1 s, dashboard <=3 s. |
| NFR-02 Reliability | WP-02, WP-05, WP-06, WP-11 | In progress | Verified PostgreSQL migration/model/rollback behavior, concurrent enrollment/heartbeat/ack/rotation handling, immutable snapshots, retries/LKG restoration, and schema-aware readiness. Agent queue, result ingest/status idempotency, and load evidence remain. |
| NFR-03 Security | WP-01, WP-03, WP-08, WP-10, WP-11 | In progress | Verified digest-only credentials/tokens, separated auth schemes, secret canaries, UTC-Z/body/rate limits, non-expandable CIDRs, no command/URL configuration, DPAPI/ACL seams, source/history checks, and Compose exposure. Real Windows proof, OIDC/TLS deployment, and `gitleaks`/`trivy` scans remain. |
| NFR-04 Observability | WP-01, WP-04-08 | In progress | Verified live/readiness, PostgreSQL/schema-aware readiness, structured JSON/request logs, correlation IDs, and Agent host lifecycle logging. Required runtime metrics/alerts remain. |
| NFR-05 Maintainability | WP-01 onward | In progress | Verified nullable/warnings, dependency graph, checked-in OpenAPI v1, 26 unit + 10 Agent + 7 PostgreSQL integration tests, 7 Vitest component tests, 2 Playwright inventory tests, formatting/lint/build, and QA gate. Coverage expands with later behavior. |

## Business rules and acceptance scenarios

| Rule/scenario | WP | Status | Required evidence |
| --- | --- | --- | --- |
| Disabled is unscheduled and creates no incident | WP-02/04/06 | Not started | Cross-component test. |
| Maintenance probes but suppresses notifications | WP-04/06/08 | Not started | Integration/E2E Scenario G. |
| Freshness uses `max(2 x interval, heartbeat grace)` | WP-06 | Not started | Fake-clock boundaries. |
| Failure/recovery thresholds and Scenarios B-D | WP-06 | Not started | Table-driven state matrix. |
| Config effective only after acknowledgement | WP-03 | Verified | Atomic Agent apply/LKG and durable same-ID acknowledgement are verified; central effective version advances only on valid `Applied`. |
| Late data cannot move current state backward | WP-05/06 | Not started | Duplicate/out-of-order/watermark tests. |
| Availability exposes monitoring coverage | WP-09 | Not started | Report fixture cross-check. |
| Scenario A normal operation/latest RTT | WP-05-07 | Not started | E2E flow. |
| Scenario E Agent outage becomes UNKNOWN | WP-03/06 | In progress | WP-03 server-side per-Agent heartbeat expiry/offline processing is verified; WP-06 UNKNOWN/no-DOWN-storm state behavior remains. |
| Scenario F 30-minute outage drains without duplicates | WP-05/11 | Not started | Resilience E2E/load evidence. |

## Work-package traceability

| WP | Status | Exit evidence / gap |
| --- | --- | --- |
| WP-00 Discovery/audit | Verified | Specifications/instructions/source/config/Git reviewed; inventory, gaps, sequence, actions, risks, and toolchain refreshed. |
| WP-01 Foundation/contracts | Verified | 12-project graph; v1 health/result contracts; Problem Details; correlation/UTC; health/OpenAPI; Compose; CI; ADRs; format/lint/build/tests/audits all pass. |
| WP-02 Database/inventory | Verified | User-approved PostgreSQL schema/migration, CRUD/filter/pagination, validation, confirmed enabled-only duplicate policy, concurrency, audit, CSV, policies, readiness, frozen OpenAPI, responsive inventory UI, 7 component tests, and 2 critical-flow Playwright tests. |
| WP-03 Enrollment/config | Verified local integration checkpoint | Implemented frozen additive endpoints/DTOs, AgentCredential separation, transactional enrollment, heartbeat expiry, immutable snapshots/ETag/ack/rollback, dual AllowedNetworks, credential rotation/revocation, one additive migration, Agent protected storage, and tests. 103/103 .NET tests, Compose/runtime, quality/security, generated OpenAPI, WP-02 comparison, and proposal comparison pass. Windows operational evidence and release scanners remain WP-10/11. |
| WP-04 Scheduler/ICMP | In progress | Contract-neutral deterministic jitter, monotonic timing, non-overlap, bounded concurrency, and transport seam verified; configuration-dependent scheduler and ICMP implementation wait for WP-03. |
| WP-05 Queue/ingestion | Not started | Depends on identity, configuration, and Probe result production. |
| WP-06 Status/incidents | Not started | Depends on durable idempotent ingestion. |
| WP-07 Dashboard | Not started | Depends on stable application APIs/events. |
| WP-08 Notifications | Not started | Depends on incident outbox. |
| WP-09 Reports/retention | Not started | Depends on transitions and time-series data. |
| WP-10 Packaging/operations | Not started | Hardening follows working components. |
| WP-11 QA/release | Not started | Full acceptance, load, resilience, auth, scan, restore, and clean-checkout gates remain. |

## WP-01 architecture evidence

| Decision/component | Status | Evidence |
| --- | --- | --- |
| ADR-001 modular monolith | Verified | Accepted ADR; project graph gate; clean 12-project build. |
| ADR-002 PostgreSQL + VictoriaMetrics | In progress | Accepted ADR and healthy pinned Compose services; adapters and failure semantics remain WP-02/05. |
| ADR-003 Agent-pull configuration | In progress | Accepted ADR; implementation/contracts remain WP-03. |
| ADR-004 transactional outbox | In progress | Accepted ADR; implementation remains WP-06/08. |
| ADR-005 event watermark | In progress | Accepted ADR; policy/tests remain WP-06. |
| ADR-006 Windows Service + SQLite | In progress | Accepted ADR and Windows-Service-capable host; queue/installer evidence remains WP-05/10. |
| Versioned HTTP/OpenAPI baseline | Verified | Package 1.0.0/schema v1; checked-in OpenAPI 3.1.1 has 14 paths, inventory schemas, 19 protected operations, Bearer/401/403 metadata, and unauthenticated health. |

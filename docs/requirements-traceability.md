# EE Pulse requirements traceability

Last updated: 2026-08-10  
Status legend: Not started, In progress, Implemented, Verified, Blocked.

WP-01 is a health-only foundation. No business requirement is marked implemented merely because its future project or dependency exists.

## Functional requirements

| Requirement | Delivery WP | Status | Current evidence / remaining acceptance |
| --- | --- | --- | --- |
| FR-01 Device inventory | WP-02, WP-07 | Not started | No entities, migrations, CRUD, CSV, audit, or UI. Require duplicate policy, validation, disable/history, and admin-delete evidence. |
| FR-02 Probe configuration | WP-02, WP-04 | Not started | Result DTO exists but no Probe model/configuration/scheduler. Require defaults, multi-probe schema, jitter, concurrency, and non-overlap tests. |
| FR-03 Agent | WP-03-05, WP-10 | Not started | Windows-Service-capable empty host and shared result DTO only. Enrollment, LKG config, heartbeat, SQLite queue, batching/retry, limits, and installer remain. |
| FR-04 Status engine | WP-06 | Not started | Require the full state matrix, thresholds, Agent-expiry UNKNOWN, maintenance, transition history, watermark, and flapping. |
| FR-05 Incident management | WP-06, WP-07 | Not started | Require atomic uniqueness, lifecycle, comments, attribution, resolution, downtime, and occurrence evidence. |
| FR-06 Dashboard | WP-07 | Not started | Health shell only. Require summary/filter/live/NOC/recent-down/offline-Agent/open-incident behavior. |
| FR-07 Device details | WP-07, WP-09 | Not started | Require configuration, metrics ranges, timeline, incidents, Agent, and result-freshness UI/API. |
| FR-08 Notifications | WP-08 | Not started | Require fake SMTP/webhook open/reminder/recovery, dedupe, suppression, retry, and redacted logs. |
| FR-09 Authentication/authorization | WP-02, WP-03, WP-07, WP-11 | Not started | No identity/authorization yet. Require production OIDC/fail-closed behavior, development-only admin, API/UI role matrix, and audit. |
| FR-10 Reporting | WP-09 | Not started | Require Device/Site availability, downtime/counts, safe CSV, maintenance separation, and explicit UNKNOWN coverage. |

## Non-functional requirements

| Requirement | Delivery WP | Status | Current evidence / remaining acceptance |
| --- | --- | --- | --- |
| NFR-01 Performance | WP-05, WP-07, WP-11 | Not started | No ingest/load/dashboard workload. Require 500 targets/30 s for 60 min, 50 average and 250 burst results/s, overview p95 <=1 s, dashboard <=3 s. |
| NFR-02 Reliability | WP-02, WP-05, WP-06, WP-11 | Not started | No migrations, queue, ingestion, or status engine. Require 72 h buffering, restart safety, idempotency, and repeatable/safe migrations. |
| NFR-03 Security | WP-01, WP-03, WP-08, WP-10, WP-11 | In progress | Verified placeholder-only sample, source/one-commit secret checks, non-root API image, internal data network, clean NuGet/npm audits. TLS/OIDC, token lifecycle, allowed networks, rate/body limits, CSV safety, full secret/image scans remain. |
| NFR-04 Observability | WP-01, WP-04-08 | In progress | Verified live/readiness, structured compact JSON logging, request logs, and correlation IDs. Dependency-aware readiness and required runtime metrics/alerts remain. |
| NFR-05 Maintainability | WP-01 onward | In progress | Verified nullable, warnings-as-errors, frozen dependency graph, v1 OpenAPI, xUnit/Vitest, formatting/lint/CI. Business unit tests and container-backed persistence integration tests remain. |

## Business rules and acceptance scenarios

| Rule/scenario | WP | Status | Required evidence |
| --- | --- | --- | --- |
| Disabled is unscheduled and creates no incident | WP-02/04/06 | Not started | Cross-component test. |
| Maintenance probes but suppresses notifications | WP-04/06/08 | Not started | Integration/E2E Scenario G. |
| Freshness uses `max(2 x interval, heartbeat grace)` | WP-06 | Not started | Fake-clock boundaries. |
| Failure/recovery thresholds and Scenarios B-D | WP-06 | Not started | Table-driven state matrix. |
| Config effective only after acknowledgement | WP-03 | Not started | Version/ack integration test. |
| Late data cannot move current state backward | WP-05/06 | Not started | Duplicate/out-of-order/watermark tests. |
| Availability exposes monitoring coverage | WP-09 | Not started | Report fixture cross-check. |
| Scenario A normal operation/latest RTT | WP-05-07 | Not started | E2E flow. |
| Scenario E Agent outage becomes UNKNOWN | WP-03/06 | Not started | E2E flow with no DOWN storm. |
| Scenario F 30-minute outage drains without duplicates | WP-05/11 | Not started | Resilience E2E/load evidence. |

## Work-package traceability

| WP | Status | Exit evidence / gap |
| --- | --- | --- |
| WP-00 Discovery/audit | Verified | Specifications/instructions/source/config/Git reviewed; inventory, gaps, sequence, actions, risks, and toolchain refreshed. |
| WP-01 Foundation/contracts | Verified | 12-project graph; v1 health/result contracts; Problem Details; correlation/UTC; health/OpenAPI; Compose; CI; ADRs; format/lint/build/tests/audits all pass. |
| WP-02 Database/inventory | Ready | Next: schema/migrations, inventory CRUD/validation/audit/CSV/auth policies and Testcontainers evidence. |
| WP-03 Enrollment/config | Not started | Depends on WP-02 metadata and Lead-approved contracts. |
| WP-04 Scheduler/ICMP | Not started | Depends on stable WP-03 configuration contracts. |
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
| Versioned HTTP/OpenAPI baseline | Verified | Package 1.0.0/schema v1; OpenAPI 3.1.1 generated; health contract and direct/API regression tests pass. |

# EE Pulse requirements traceability

Last updated: 2026-08-10  
Status legend: **Not started**, **In progress**, **Implemented**, **Verified**, **Blocked**.

No functional requirements were implemented at audit time. The table is the delivery baseline and must be updated with concrete code and test evidence as each Work Package closes.

## Functional requirements

| Requirement | Delivery WP | Current status | Planned evidence |
| --- | --- | --- | --- |
| FR-01 Device inventory | WP-02, WP-07 | Not started | Domain/API/UI tests for CRUD, site duplicate policy, IPv4 validation, CSV preview/commit row errors, disable/history preservation, admin-only audited deletion |
| FR-02 Probe configuration | WP-02, WP-04 | Not started | Validation tests for defaults/thresholds; multiple-probe schema; deterministic jitter, concurrency, and non-overlap tests |
| FR-03 Agent | WP-03–WP-05, WP-10 | Not started | Enrollment/revocation tests; Windows Service publish/install evidence; config LKG tests; heartbeat, SQLite restart, retry/batch/idempotency, limits, and no-command surface review |
| FR-04 Status engine | WP-06 | Not started | Table-driven transition matrix including thresholds, UNKNOWN on Agent expiry, maintenance, transition reasons, watermark, and flapping |
| FR-05 Incident management | WP-06, WP-07 | Not started | Atomic one-open-incident tests; lifecycle/comment/user-time/note/auto-resolution/downtime/occurrence UI and API evidence |
| FR-06 Dashboard | WP-07 | Not started | Summary/filter/pagination tests; SignalR/reconnect; recent-down/offline/open incidents; NOC refresh; accessibility states |
| FR-07 Device details | WP-07, WP-09 | Not started | API/UI tests for probe/config, RTT/loss/success ranges, timeline/incidents, Agent and freshness timestamp |
| FR-08 Notifications | WP-08 | Not started | Fake SMTP/webhook tests for open/escalation/recovery, dedupe, bounded reminders, quiet/maintenance suppression, redacted delivery logs |
| FR-09 Authentication/authorization | WP-02, WP-03, WP-07, WP-11 | Not started | Production fail-closed config; development-only seeded admin; API/UI role matrix; audit evidence |
| FR-10 Reporting | WP-09 | Not started | Fixture-checked Device/Site availability, downtime/count, safe CSV, maintenance separation, explicit UNKNOWN coverage |

## Non-functional requirements

| Requirement | Delivery WP | Current status | Planned evidence |
| --- | --- | --- | --- |
| NFR-01 Performance | WP-05, WP-07, WP-11 | Not started | 500 targets/30 s for 60 min, average 50 and burst 250 results/s, overview p95 <=1 s, initial dashboard <=3 s |
| NFR-02 Reliability | WP-02, WP-05, WP-06, WP-11 | Not started | 72 h queue sizing/configuration; duplicate batch/run/transition/incident tests; restart tests; repeatable/safe migration evidence |
| NFR-03 Security | WP-01, WP-03, WP-08, WP-10, WP-11 | In progress | WP-01 verified placeholder-only config, warning-as-error vulnerable restore, patched OpenAPI dependency, and clean NuGet/npm audits; remaining TLS/OIDC/token/network/CORS/rate/body-size/CSV/container/secret controls follow |
| NFR-04 Observability | WP-01, WP-04–WP-08 | In progress | WP-01 verified live/readiness, correlation IDs, Serilog compact JSON foundation; later ingestion/error/queue/heartbeat/duration/notification metrics and platform alerts |
| NFR-05 Maintainability | WP-01 onward | In progress | WP-01 verified nullable, warnings-as-errors, project boundaries, OpenAPI, xUnit/Vitest; Testcontainers and business-logic coverage follow |

## Business rules and acceptance scenarios

| Rule/scenario | Delivery WP | Current status | Required evidence |
| --- | --- | --- | --- |
| Disabled devices are not scheduled or incidented | WP-02, WP-04, WP-06 | Not started | Cross-component test |
| Maintenance continues probes and suppresses notifications | WP-04, WP-06, WP-08 | Not started | Integration/E2E Scenario G |
| Freshness is `max(2 x interval, heartbeat grace)` | WP-06 | Not started | Fake-clock boundary tests |
| DOWN/recovery thresholds | WP-06 | Not started | State-matrix Scenarios B–D |
| Config effective only after Agent acknowledgement | WP-03 | Not started | Version/acknowledgement integration test |
| Late data cannot move current state backward | WP-05, WP-06 | Not started | Out-of-order/watermark integration tests |
| Availability exposes monitoring coverage | WP-09 | Not started | Report fixture cross-check |
| Normal operation / latest RTT | WP-05–WP-07 | Not started | E2E Scenario A |
| Agent disconnect => UNKNOWN, no DOWN storm | WP-03, WP-06 | Not started | E2E Scenario E |
| 30-minute central outage drains without duplicates | WP-05, WP-11 | Not started | E2E Scenario F |

## Architecture traceability

| Decision/component | WP | Current status | Evidence target |
| --- | --- | --- | --- |
| ADR-001 modular monolith | WP-01 | Verified | ADR accepted; 12-project solution and one-way references build cleanly |
| ADR-002 PostgreSQL + VictoriaMetrics | WP-01, WP-02, WP-05 | In progress | ADR, pinned healthy Compose, repositories/adapters |
| ADR-003 Agent-pull versioned configuration | WP-01, WP-03 | In progress | ADR, v1 contracts, ETag/version/ack tests |
| ADR-004 transactional outbox | WP-01, WP-06, WP-08 | In progress | ADR, transaction and delivery tests |
| ADR-005 current-state event watermark | WP-01, WP-06 | In progress | ADR, late/out-of-order test matrix |
| ADR-006 Windows Service + SQLite queue | WP-01, WP-04, WP-05, WP-10 | In progress | ADR, queue/restart tests, Windows publish/install evidence |
| API/Worker modular boundary | WP-01 | Verified | Projects and one-way references compile with warnings as errors |
| Versioned HTTP/OpenAPI contracts | WP-01 onward | Verified | Initial v1 Agent result and health DTOs tested; generated OpenAPI endpoint returns HTTP 200; future additions remain versioned |
| React/MUI/Query/ECharts/SignalR web | WP-01, WP-07 | In progress | Frontend manifest/build and page/E2E tests |

## Work-package traceability

| WP | Scope status | Exit evidence status |
| --- | --- | --- |
| WP-00 Discovery/audit | Implemented | Complete: inventory, gaps, plan, risks, assumptions, action groups, tool versions, and control documents |
| WP-01 Foundation/contracts | Verified | 12-project build clean; 6 .NET and 2 frontend tests pass; lint/build/format pass; all Compose services healthy; health/OpenAPI HTTP 200; dependency audits clean |
| WP-02 Database/inventory | Ready | Foundation dependency satisfied; no user action blocks starting |
| WP-03 Enrollment/config | Not started | Dependency-blocked by WP-02/contracts |
| WP-04 Scheduler/ICMP | Not started | Dependency-blocked by WP-03 configuration contract |
| WP-05 Queue/ingestion | Not started | Dependency-blocked by WP-03/WP-04 |
| WP-06 Status/incidents | Not started | Dependency-blocked by durable ingestion |
| WP-07 Dashboard | Not started | Dependency-blocked by stable application APIs |
| WP-08 Notifications | Not started | Dependency-blocked by outbox/incidents |
| WP-09 Reports/retention | Not started | Dependency-blocked by transition/time-series model |
| WP-10 Packaging/operations | Not started | Hardening follows working components |
| WP-11 QA/release | Not started | Dependency-blocked by WP-01–WP-10 |

# EE Pulse requirements traceability

Last updated: 2026-09-09
Status legend: Not started, In progress, Implemented, Verified, Blocked.

WP-02 backend inventory and its inventory frontend slice are implemented and integration-verified. WP-04 is locally integration-verified as a deterministic probe-runtime foundation using fake time and transport. WP-05 durable outbox/delivery/ingestion was merged in `2c22766` (PR #5). WP-06 status projection, transition, incident, lifecycle-event, suppression, maintenance-precedence, freshness, and heartbeat-expiry behavior is merged in `751b6bd5`. WP-07 Phase 2A implements the timezone-preference persistence/API vertical slice; Phase 2B closes only the frozen future summary/device-status contract. Phase 2B1 runtime reads, incident actions, audit listing, SignalR runtime, and frontend UI remain pending.

## Functional requirements

| Requirement | Delivery WP | Status | Current evidence / remaining acceptance |
| --- | --- | --- | --- |
| FR-01 Device inventory | WP-02, WP-07 | Verified | Backend CRUD, server filtering/pagination, IPv4/hostname validation, enabled-only Site/address uniqueness, disabled/cross-Site IP reuse, hostname reuse, re-enable/concurrent conflicts, CSV row errors, history-preserving disable, Administrator audited delete, PostgreSQL migration, and responsive inventory UI pass component/browser/integration tests. |
| FR-02 Probe configuration | WP-02, WP-04 | In progress | WP-04 locally verifies the deterministic runtime foundation with fake time/transport: IPv4-literal dual-scope validation, stable jitter, bounded scheduling/non-overlap, sequential attempts, immutable local result/error semantics, cancellation, and observability. Real ICMP and all later persistence/delivery/ingestion evidence remain. |
| FR-03 Agent | WP-03-05, WP-10 | In progress | WP-03 verified enrollment/identity/liveness/configuration; WP-05 merged durable SQLite queue, result batching, and idempotent backend ingestion. Installer and real Windows operational evidence remain. |
| FR-04 Status engine | WP-06 | Implemented and merged | WP-06 merged result-driven projection/transitions, freshness/heartbeat UNKNOWN behavior, maintenance/disabled precedence, watermark/skew policy, and deterministic verification. Dashboard reads remain WP-07. |
| FR-05 Incident management | WP-06, WP-07 | In progress | WP-06 merged atomic availability incident/lifecycle/occurrence/automatic confirmed-recovery resolution and suppression context. WP-07 must add authorized read, acknowledgement, comments, and constrained manual-resolution API/UI. |
| FR-06 Dashboard | WP-07 | In progress | Phase 2A implements the persisted timezone-preference API and generated OpenAPI coverage. Phase 2B freezes summary/filter/recent-down/offline-Agent/open-incident canonical representation and conditional-read semantics only; no 2B1 runtime route or OpenAPI change exists. |
| FR-07 Device details | WP-07, WP-09 | Phase 1 approved | WP-07 contract defines status, metrics range, timeline, incident, Agent, result-freshness, UTC/timezone, and chart-gap semantics; implementation remains outside Phase 2A. |
| FR-08 Notifications | WP-08 | Not started | Require fake SMTP/webhook open/reminder/recovery, dedupe, suppression, retry, and redacted logs. |
| FR-09 Authentication/authorization | WP-02, WP-03, WP-07, WP-11 | In progress | Verified inventory Bearer policies, separate AgentCredential path, and Phase 2A dashboard.read plus explicit issuer/subject fail-closed timezone identity. Production OIDC and broader dashboard role/action coverage remain. |
| FR-10 Reporting | WP-09 | Not started | Require Device/Site availability, downtime/counts, safe CSV, maintenance separation, and explicit UNKNOWN coverage. |

## Non-functional requirements

| Requirement | Delivery WP | Status | Current evidence / remaining acceptance |
| --- | --- | --- | --- |
| NFR-01 Performance | WP-05, WP-07, WP-11 | Not started | No ingest/load/dashboard workload. Require 500 targets/30 s for 60 min, 50 average and 250 burst results/s, overview p95 <=1 s, dashboard <=3 s. |
| NFR-02 Reliability | WP-02, WP-05, WP-06, WP-11 | In progress | WP-05 merged durable queue/delivery and immutable result-ingestion idempotency; WP-06 merged state/incident idempotency and deterministic persistence coverage. Load evidence remains. |
| NFR-03 Security | WP-01, WP-03, WP-08, WP-10, WP-11 | In progress | Verified digest-only credentials/tokens, separated auth schemes, secret canaries, UTC-Z/body/rate limits, non-expandable CIDRs, no command/URL configuration, DPAPI/ACL seams, source/history checks, and Compose exposure. Real Windows proof, OIDC/TLS deployment, and `gitleaks`/`trivy` scans remain. |
| NFR-04 Observability | WP-01, WP-04-08 | In progress | Verified live/readiness, PostgreSQL/schema-aware readiness, structured JSON/request logs, correlation IDs, and WP-04 fake-only runtime observability behavior. Production metrics/alerts and operational evidence remain. |
| NFR-05 Maintainability | WP-01 onward | In progress | WP-04 final integration review passed: Agent tests 112/112, formatting, Agent host and Agent Tests Release builds with 0 warnings/errors, quality/security, and `git diff --check`. Coverage expands with later behavior. |

## Business rules and acceptance scenarios

| Rule/scenario | WP | Status | Required evidence |
| --- | --- | --- | --- |
| Disabled is unscheduled and creates no incident | WP-02/04/06 | Not started | Cross-component test. |
| Maintenance probes but suppresses notifications | WP-04/06/08 | Not started | Integration/E2E Scenario G. |
| Freshness uses `max(2 x interval, heartbeat grace)` | WP-06 | Implemented and verified | Deterministic freshness/heartbeat expiry coverage is merged. |
| Failure/recovery thresholds and Scenarios B-D | WP-06 | Implemented and verified | WP-06 table-driven state and persistence coverage is merged. |
| Config effective only after acknowledgement | WP-03 | Verified | Atomic Agent apply/LKG and durable same-ID acknowledgement are verified; central effective version advances only on valid `Applied`. |
| Late data cannot move current state backward | WP-05/06 | Implemented and verified | WP-06 applies the approved cursor/future/receipt-time historical-only policy with deterministic boundary coverage. |
| Availability exposes monitoring coverage | WP-09 | Not started | Report fixture cross-check. |
| Scenario A normal operation/latest RTT | WP-05-07 | In progress | WP-05 ingestion and WP-06 status projection are merged; dashboard/API/UI E2E remains. |
| Scenario E Agent outage becomes UNKNOWN | WP-03/06 | Implemented and verified | WP-03 heartbeat expiry plus WP-06 UNKNOWN/no-DOWN-storm state processing are merged; UI presentation remains WP-07. |
| Scenario F 30-minute outage drains without duplicates | WP-05/11 | In progress | WP-05 delivery/recovery coverage is merged; 30-minute resilience/load evidence remains. |

## Work-package traceability

| WP | Status | Exit evidence / gap |
| --- | --- | --- |
| WP-00 Discovery/audit | Verified | Specifications/instructions/source/config/Git reviewed; inventory, gaps, sequence, actions, risks, and toolchain refreshed. |
| WP-01 Foundation/contracts | Verified | 12-project graph; v1 health/result contracts; Problem Details; correlation/UTC; health/OpenAPI; Compose; CI; ADRs; format/lint/build/tests/audits all pass. |
| WP-02 Database/inventory | Verified | User-approved PostgreSQL schema/migration, CRUD/filter/pagination, validation, confirmed enabled-only duplicate policy, concurrency, audit, CSV, policies, readiness, frozen OpenAPI, responsive inventory UI, 7 component tests, and 2 critical-flow Playwright tests. |
| WP-03 Enrollment/config | Verified local integration checkpoint | Implemented frozen additive endpoints/DTOs, AgentCredential separation, transactional enrollment, heartbeat expiry, immutable snapshots/ETag/ack/rollback, dual AllowedNetworks, credential rotation/revocation, one additive migration, Agent protected storage, and tests. 103/103 .NET tests, Compose/runtime, quality/security, generated OpenAPI, WP-02 comparison, and proposal comparison pass. Windows operational evidence and release scanners remain WP-10/11. |
| WP-04 Scheduler/ICMP | Implemented and integration-verified locally | Deterministic probe-runtime foundation verified through fake time/transport tests. Final integration review PASS; Agent tests 112/112; formatting; Agent host and Agent Tests Release builds (0 warnings/errors); quality/security; and `git diff --check` passed. This is not real ICMP, host/DI wiring, Windows Service, persistence, delivery, ingestion, UI, deployment, or IP-discovery evidence. |
| WP-05 Queue/ingestion | Implemented and merged | PR #5 / `2c22766` implements the durable SQLite outbox, at-least-once delivery, idempotent PostgreSQL ledger, and recovery coverage under the binding UA-11 pressure policy. |
| WP-06 Status/incidents | Implemented and merged | `751b6bd5` contains projection/transitions, freshness/heartbeat expiry, atomic availability incidents/lifecycle events, maintenance precedence, and deterministic verification. Read/action APIs and UI remain WP-07. |
| WP-07 Dashboard | Phase 2A implemented; Phase 2B contract closure | Timezone-preference persistence/API, explicit principal identity, dashboard.read authorization, safe correlation IDs, atomic redacted audit writes, ETag/concurrency/no-op/clear semantics, deterministic race/rollback evidence, and generated OpenAPI are implemented and verified. Phase 2B freezes the future summary/device-status status-overlay, filter, ordering, canonical bytes/ETag, and conditional-GET contract only. Dashboard/device/status runtime reads, incident actions, audit listing, SignalR runtime, and frontend UI remain pending; WP-08/WP-09 boundaries remain unchanged. |
| WP-08 Notifications | Not started | Depends on incident outbox. |
| WP-09 Reports/retention | Not started | Depends on transitions and time-series data. |
| WP-10 Packaging/operations | Not started | Hardening follows working components. |
| WP-11 QA/release | Not started | Full acceptance, load, resilience, auth, scan, restore, and clean-checkout gates remain. |

### WP-07 Phase 2A evidence (2026-09-08)

The timezone-preference vertical slice is implemented and generated OpenAPI is runtime-produced from the pinned Linux API. The complete pinned Release build passed with 0 warnings/errors; `EePulse.UnitTests` passed 128/128; and the PostgreSQL-backed `EePulse.IntegrationTests` suite passed 175/175 with no failures or skips in 15m26s. Focused evidence covers canonical development/principal identity, server-controlled correlation IDs, dashboard.read authorization, ETag/concurrency/no-op/clear behavior, deterministic first/stale races, deferred ownership draining, dual-SQL rollback/retry, aggregate snapshots, and audit redaction. The generated artifact documents the Phase 2A timezone GET/PUT addition and the source-backed WP-05 result-batches route; remaining dashboard/device/status, incident-action, audit-list, SignalR runtime, and frontend slices remain pending.

## WP-01 architecture evidence

| Decision/component | Status | Evidence |
| --- | --- | --- |
| ADR-001 modular monolith | Verified | Accepted ADR; project graph gate; clean 12-project build. |
| ADR-002 PostgreSQL + VictoriaMetrics | In progress | Accepted ADR and healthy pinned Compose services; adapters and failure semantics remain WP-02/05. |
| ADR-003 Agent-pull configuration | In progress | Accepted ADR; implementation/contracts remain WP-03. |
| ADR-004 transactional outbox | In progress | Accepted ADR; lifecycle-outbox implementation remains WP-06/08. |
| ADR-005 event watermark | In progress | Accepted ADR; ADR-012 approves WP-06 lateness/skew policy; implementation/tests remain. |
| ADR-012 WP-06 UA-01 policy | Implemented and verified | Binding MVP policy was implemented in merged WP-06; WP-09 availability reporting remains. |
| ADR-006 Windows Service + SQLite | In progress | Accepted ADR and Windows-Service-capable host; queue/installer evidence remains WP-05/10. |
| Versioned HTTP/OpenAPI baseline | Verified | Package 1.0.0/schema v1; checked-in generated OpenAPI 3.1.1 has 29 paths, inventory/Agent/timezone schemas, 34 protected operations, Bearer/401/403 metadata, and unauthenticated health. |

# EE Pulse implementation status

Last updated: 2026-09-08 (Asia/Bangkok)
Owner: Lead/Integration Agent
Current checkpoint: WP-07 Phase 2A timezone-preference implementation, generated OpenAPI, and documentation pass

## Outcome

WP-00 and WP-01 remain verified. The user approved WP-02 after the backend baseline and authorized frontend slice passed Lead integration:

- Agent A delivered WP-02 Backend/PostgreSQL inventory.
- Agent B delivered contract-neutral Probe Agent foundations.
- Agent D delivered QA/security gates, fixtures, review, and integrated verification.
- Lead reviewed the changes, returned in-scope defects to their owners, rebuilt the final stack, and generated/reviewed `docs/api/openapi-v1.json`.
- Agent C delivered the Site, Device, Probe-configuration, and CSV inventory UI plus Vitest and Playwright coverage. Lead returned production-authentication, OpenAPI-filter, and conflict-recovery defects; the corrected implementation passes the full integration gate.

The committed WP-02 checkpoint is preserved at frozen contract commit `34718aa13727d8e84e5f56b61e854cbbabc5adab`. WP-03 implements enrollment, identity, heartbeat, configuration, revocation, credential rotation, and AllowedNetworks. WP-04 provides the deterministic, fake-transport-tested probe-runtime foundation only. WP-05 durable result outbox and idempotent ingestion were merged in `2c22766` (PR #5). WP-06 is merged in `751b6bd5a45fd42e00a9ccb22a5765d3c0c16594` with status projection, transitions, freshness/heartbeat expiry, availability incidents, lifecycle events, maintenance precedence, and deterministic verification; it does not expose dashboard-facing APIs, manual incident actions, SignalR, or UI.

## Current repository

| Area | State |
| --- | --- |
| Specifications | Six authoritative files under `docs/spec`; no `AGENTS.md` or additional repository instruction file is present. |
| Git | WP-06 was merged in `751b6bd5a45fd42e00a9ccb22a5765d3c0c16594`; WP-07 Phase 1 was contract/design-only, and this checkpoint completes the Phase 2A timezone-preference persistence and API implementation. |
| Backend | PostgreSQL-backed Site, Device, AgentGroup, Probe, MaintenanceWindow, AuditEvent, CSV import, authorization policies, migration, seed gate, and dependency-aware readiness. |
| Contracts/OpenAPI | Existing v1 inventory/Agent contracts remain compatible; generated `openapi-v1.json` now includes the verified Phase 2A timezone-preference GET/PUT operations and their headers, security, response, and schema metadata. |
| Probe Agent | WP-04 deterministic runtime foundation plus WP-05 durable SQLite outbox and at-least-once result delivery. Real ICMP and Windows-Service operational evidence remain unclaimed. |
| Web | Responsive inventory console for Sites, server-filtered/paged Devices, create/edit/soft-disable, Probe fields, CSV preview/commit, row errors, stale/partial/retry states, and actionable concurrency conflicts. Development synthetic identity is absent from the production bundle, which fails closed pending OIDC. |
| QA | WP-04 final integration review passed: Agent tests 112/112, formatting, Agent host and Agent Tests Release builds (0 warnings/errors), quality/security gate, and `git diff --check`. Earlier WP-02/03 evidence remains recorded below. |
| Compose | PostgreSQL 18.4, VictoriaMetrics 1.148.0, and API healthy; only API publishes a host port. |

## Work-package status

| WP | Status | Evidence / next boundary |
| --- | --- | --- |
| WP-00 | Verified | Audit/control documents and repository governance established. |
| WP-01 | Verified | Foundation/project graph/contracts/health/CI/Compose/ADRs pass. |
| WP-02 | Approved | Backend/PostgreSQL plus inventory frontend pass together: metadata, migration, APIs, validation, authorization, audit, CSV, OpenAPI, enabled-only Device uniqueness, accessible UI, component tests, and critical-flow browser tests. |
| WP-03 | Implemented and integration-verified | Additive v1 Agent endpoints/DTOs, separate Agent credentials, one additive PostgreSQL migration, enrollment/revocation/rotation, heartbeat/offline processing, immutable configuration snapshots/acknowledgements/rollback, dual AllowedNetworks enforcement, DPAPI/ACL-backed Agent storage, and generated OpenAPI are verified. |
| WP-04 | Implemented and integration-verified locally | Deterministic probe-runtime foundation verified with fake time/transport: IPv4-literal scope validation, stable jitter/monotonic cadence, bounded admission/non-overlap, coalesced missed slots, sequential attempts, immutable local results, fixed outcome categories, cancellation, and cardinality-safe observability. No real ICMP, persistence, delivery, ingestion, UI, deployment, or Windows Service evidence is included. |
| WP-05 | Implemented and merged | Durable SQLite outbox, at-least-once delivery, idempotent PostgreSQL ledger/ingestion, and delivery-recovery coverage were merged in `2c22766` (PR #5). UA-11 policy remains binding. No WP-06 status/incident behavior is included. |
| WP-06 | Implemented and merged | Result-driven projection/transitions, freshness and heartbeat UNKNOWN expiry, atomic availability incidents/lifecycle/suppression context, maintenance precedence, and deterministic verification are merged. Dashboard read APIs, manual actions/comments, SignalR, UI, notifications, and reports remain outside WP-06. |
| WP-07 | Phase 2A implemented and integration-verified | Timezone-preference persistence, API GET/PUT, explicit issuer+subject identity, dashboard.read authorization, server-controlled correlation IDs, atomic redacted AuditEvent writes, ETag/concurrency/no-op/clear semantics, deterministic race/rollback coverage, and generated OpenAPI are implemented and verified. Dashboard/device/status reads, incident actions, audit listing, SignalR runtime, and frontend UI remain later WP-07 slices. |
| WP-08 through WP-11 | Not started | Continue in dependency order after WP-07 implementation. |

## Stable contract decision

Lead confirms that Agent C consumed the frozen WP-02 inventory/OpenAPI v1 surface without changing it. The checked-in artifact is `docs/api/openapi-v1.json`.

Stable elements include:

- Site, AgentGroup, Device, Probe, MaintenanceWindow, pagination, CSV preview/commit, and Problem Details shapes.
- UUID strings, bigint row versions, UTC timestamps, page default 1/page size 50/max 200.
- Runtime inventory authorization plus OpenAPI Bearer requirements and 401/403 responses.
- Confirmed Device policy: normalized address is unique among enabled Devices within a Site; disabled reuse and cross-Site reuse are allowed, hostname is non-unique, and re-enable/concurrent conflicts are database-enforced.

Breaking changes require a new API/schema version. Compatible additions remain Lead-owned and require consumer/test review. Backend exclusively owns migrations.

## Integrated verification

### WP-07 Phase 2A verification evidence (2026-09-08)

| Gate | Result |
| --- | --- |
| Pinned Linux .NET 10.0.302 Release solution build | Passed; 0 warnings and 0 errors. |
| Unit tests | 128/128 passed; 0 failed/skipped. |
| Integration tests | 175/175 passed; 0 failed/skipped; PostgreSQL/Testcontainers run completed in 15m26s. |
| Focused WP-07 tests | Contract/API, deterministic first/stale races, deferred ownership drain, dual-SQL rollback, correlation/principal, authorization, and aggregate snapshot tests passed. |
| Generated OpenAPI | Runtime-generated twice from the pinned API; second generation is byte-identical (SHA-256 `44F2C9D1EB902E1EC44C6395305F328F262A3D592E9EDF7BA40724C030DE435C`); timezone GET/PUT and AgentCredential result-batches metadata reviewed. |
| Format and hygiene | Style/analyzers passed; scoped whitespace/final-newline checks passed; `git diff --check` passed. |

This checkpoint claims only the Phase 2A timezone-preference vertical slice. Dashboard/device/status reads, incident actions, audit-log listing/UI, SignalR runtime, and frontend UI remain pending WP-07 work. WP-08 notification fan-out and WP-09 reporting/retention ownership are unchanged.

### WP-06 final verification evidence (baseline at `751b6bd5a45fd42e00a9ccb22a5765d3c0c16594`)

| Gate | Result |
| --- | --- |
| Integration tests | 150/150 passed. |
| Unit tests | 100/100 passed. |
| Build, format, analyzers, and CI | Passed. |
| Scope statement | WP-06 verification covers its projection, transition, incident, lifecycle-event, maintenance, Agent, and audit data behavior; it does not claim dashboard APIs/UI, SignalR, manual incident actions/comments, or reporting. |

| Gate | Result |
| --- | --- |
| .NET restore/format/Release build | Passed; 0 warnings and 0 errors in the isolated final run. |
| .NET tests | 43 passed: Unit 26, Agent 10, Integration 7; 0 failed/skipped. |
| NuGet vulnerability audit | No vulnerable packages reported across all 12 projects. |
| Frontend | ESLint passed; Vitest 7/7; production build transformed 954 modules (320.41 kB, 102.40 kB gzip); Playwright inventory 2/2; npm audit 0 vulnerabilities. |
| Production frontend auth | Bundle inspection found no synthetic auth headers, role chooser, or Development-access markers; production renders a fail-closed OIDC-not-configured state. |
| Agent B independent review | Agent build 0 warnings/errors; Agent tests 10/10; code reviewed by Lead and QA. |
| Quality/security script | Passed foundation, working-tree/history secret patterns, exact versions, lockfile, Compose exposure/network/privilege, and image-tag checks. |
| Compose/runtime | Config valid; PostgreSQL, VictoriaMetrics, API healthy; live/ready schema v1; anonymous inventory HTTP 401. |
| OpenAPI | 3.1.1; 29 paths; 34 protected operations; Bearer scheme; protected operations declare 401/403; health remains unauthenticated. |
| Repository hygiene | `git diff --check` passed; no commit, push, deployment, real credentials, real probes, or notifications. |

### WP-03 final integration evidence (2026-08-14)

| Gate | Result |
| --- | --- |
| Release builds | Backend solution and Windows Agent passed with 0 warnings and 0 errors using `mcr.microsoft.com/dotnet/sdk:10.0.302`. |
| .NET regression | 103/103 passed: Unit 34/34, Agent 54/54, PostgreSQL-backed Integration 14/14, Security 1/1. |
| Migration/model | Integration coverage passed clean migration, WP-02-to-WP-03 inventory preservation, constraints/indexes, no pending model changes, and rollback-script generation. The WP-02 migration files are unchanged; one additive WP-03 migration is present. |
| Runtime/Compose | PostgreSQL, VictoriaMetrics, and API healthy. `/health/live`, `/health/ready`, and `/openapi/v1.json` returned 200; user/Admin/Agent authentication separation and anonymous malformed enrollment validation passed. |
| OpenAPI/contract | Generated OpenAPI 3.1.1 SHA-256 `25A4B67C5B0CBD62A42FAF0A2F99A47F789525059AF9B13E664C3056277B9A6E` matches the live API; WP-03 static gate, frozen WP-02 semantic comparison, and frozen WP-03 proposal comparison passed. |
| Security/quality | Quality/security gate passed, including source/history secret checks, dependency/lockfile/Compose checks, and WP-01 graph governance. `gitleaks` and `trivy` remain unavailable external scanner gaps. |

The first sandboxed Vitest attempt could not spawn Vite's helper (`EPERM`); the authorized rerun is the passing 7/7 evidence above. Invalid earlier concurrent/nested-Docker runs remain excluded; the current isolated .NET run is 43/43.

### WP-04 final integration evidence (2026-08-20)

| Gate | Result |
| --- | --- |
| Final integration review | PASS. |
| Agent tests | 112/112 passed. |
| Formatting | Passed. |
| Release builds | Agent host and Agent Tests Release builds passed with 0 warnings and 0 errors. |
| Quality/security | Passed. `gitleaks` and `trivy` remain WP-11 gaps. |
| Repository hygiene | `git diff --check` passed. |

This checkpoint verifies a deterministic local runtime using fake time and fake transport. It is not evidence of real ICMP, host/DI wiring, Windows Service operation, persistence, delivery, backend ingestion, UI, deployment, or IP discovery.

## Accepted follow-on risks

- UA-01 is approved and binding for WP-06 through ADR-012. The duplicate-device rule remains confirmed and enforced; separate Site/VLAN/role discovery does not reopen the WP-06 status/incident policy.
- Production OIDC remains pending under UA-06; Agent enrollment/configuration and complete role-matrix breadth remain later work.
- The checked-in OpenAPI models the Device-list `criticality` query as an integer enum without published names; the frontend uses contract values 0-3 while presenting Low/Normal/High/Critical labels. Clarifying enum metadata is a future compatible-contract task.
- CSV preview state is intentionally node-local, bounded, and retained for 15 minutes; restart discards it.
- Full per-aggregate authorization/concurrency breadth, concurrent CSV/duplicate races, and soft-disable/history E2E coverage remain explicit QA gaps.
- `gitleaks` and `trivy` remain accepted WP-11 verification gaps; container images use version tags rather than immutable digests.
- Frontend contract types are manually maintained against the frozen OpenAPI; compatibility tests and Lead review mitigate drift until generated-client work is selected.
- The host lacks .NET SDK 10; the pinned `mcr.microsoft.com/dotnet/sdk:10.0.302` container remains the verified build environment.
- VictoriaMetrics dependency behavior is a WP-05 concern; current readiness verifies PostgreSQL schema/connectivity.
- Real Windows DPAPI LocalMachine and service-account ACL/recovery evidence requires a disposable Windows host (WP-10/11).
- Immediate revocation of a disconnected Agent cannot be guaranteed until it reconnects; credential expiry and short polling limit exposure.
- `gitleaks` and `trivy` are still unavailable; dedicated secret/image scanning remains a WP-11 release requirement.
- WP-04's fake-only runtime evidence does not substitute for UA-03 controlled real-ICMP approval or UA-04 Windows Service operational evidence.

## Next checkpoint

WP-06 is merged and verified. WP-07 Phase 1 defined the approved dashboard/device/incident contract boundary, and this checkpoint completes the Phase 2A timezone-preference persistence and API implementation. Dashboard/device/status reads, incident actions, audit read/UI, SignalR runtime, and frontend work remain pending; WP-08 notification fan-out and WP-09 reporting/retention ownership are unchanged. UA-03 remains mandatory before real ICMP validation; UA-04 remains mandatory before Windows Service operational evidence. Do not treat documentation CIDRs or local Compose credentials as production authorization.

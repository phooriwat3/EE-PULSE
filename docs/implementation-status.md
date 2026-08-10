# EE Pulse implementation status

Last updated: 2026-08-10 (Asia/Bangkok)  
Owner: Lead/Integration Agent  
Current checkpoint: first parallel wave complete; WP-02 integration gate ready

## Outcome

WP-00 and WP-01 remain verified. The first parallel wave is complete:

- Agent A delivered WP-02 Backend/PostgreSQL inventory.
- Agent B delivered contract-neutral Probe Agent foundations.
- Agent D delivered QA/security gates, fixtures, review, and integrated verification.
- Lead reviewed the changes, returned in-scope defects to their owners, rebuilt the final stack, and generated/reviewed `docs/api/openapi-v1.json`.

Agent C has not been created. WP-03 integration has not started.

## Current repository

| Area | State |
| --- | --- |
| Specifications | Six authoritative files under `docs/spec`; no `AGENTS.md` or additional repository instruction file is present. |
| Git | Local `main` at `ce3743a`, one commit ahead of configured `origin/main`; first-wave changes are uncommitted. |
| Backend | PostgreSQL-backed Site, Device, AgentGroup, Probe, MaintenanceWindow, AuditEvent, CSV import, authorization policies, migration, seed gate, and dependency-aware readiness. |
| Contracts/OpenAPI | Compatible v1 inventory DTOs; checked-in OpenAPI 3.1.1 artifact with 14 paths, 19 protected operations, Bearer/OIDC-ready metadata, and explicit Development-header note. |
| Probe Agent | Deterministic jitter, monotonic scheduling, per-Probe non-overlap, bounded concurrency, injectable transport seam, and graceful host behavior; no network probe implementation. |
| QA | 42 .NET tests, 2 frontend tests, PostgreSQL Testcontainers coverage, quality/security script, and WP-02 evidence matrix. |
| Compose | PostgreSQL 18.4, VictoriaMetrics 1.148.0, and API healthy; only API publishes a host port. |

## Work-package status

| WP | Status | Evidence / next boundary |
| --- | --- | --- |
| WP-00 | Verified | Audit/control documents and repository governance established. |
| WP-01 | Verified | Foundation/project graph/contracts/health/CI/Compose/ADRs pass. |
| WP-02 | Integration gate ready | Metadata, migration, inventory APIs, validation, authorization, audit, CSV, OpenAPI, PostgreSQL tests, and the confirmed enabled-only Device uniqueness rule pass. |
| WP-03 | Not started | Enrollment/configuration integration is explicitly deferred. |
| WP-04 | Partial foundation only | Contract-neutral scheduling/concurrency/transport seams exist; ICMP/config-dependent work is not started. |
| WP-05 through WP-11 | Not started | Continue in dependency order after the next approved checkpoint. |

## Stable contract decision

Lead confirms the WP-02 inventory and OpenAPI v1 surface is stable enough for Agent C to consume when the user authorizes Agent C startup. The checked-in artifact is `docs/api/openapi-v1.json`.

Stable elements include:

- Site, AgentGroup, Device, Probe, MaintenanceWindow, pagination, CSV preview/commit, and Problem Details shapes.
- UUID strings, bigint row versions, UTC timestamps, page default 1/page size 50/max 200.
- Runtime inventory authorization plus OpenAPI Bearer requirements and 401/403 responses.
- Confirmed Device policy: normalized address is unique among enabled Devices within a Site; disabled reuse and cross-Site reuse are allowed, hostname is non-unique, and re-enable/concurrent conflicts are database-enforced.

Breaking changes require a new API/schema version. Compatible additions remain Lead-owned and require consumer/test review. Backend exclusively owns migrations.

## Integrated verification

| Gate | Result |
| --- | --- |
| .NET restore/format/Release build | Passed; 0 warnings and 0 errors in the isolated final run. |
| .NET tests | 42 passed: Unit 25, Agent 10, Integration 7; 0 failed/skipped. |
| NuGet vulnerability audit | No vulnerable packages reported across all 12 projects. |
| Frontend | ESLint passed; Vitest 2/2; production build transformed 946 modules; npm audit 0 vulnerabilities. |
| Agent B independent review | Agent build 0 warnings/errors; Agent tests 10/10; code reviewed by Lead and QA. |
| Quality/security script | Passed foundation, working-tree/history secret patterns, exact versions, lockfile, Compose exposure/network/privilege, and image-tag checks. |
| Compose/runtime | Config valid; PostgreSQL, VictoriaMetrics, API healthy; live/ready schema v1; anonymous inventory HTTP 401. |
| OpenAPI | 3.1.1; 14 paths; 19 protected operations; Bearer scheme; protected operations declare 401/403; health remains unauthenticated. |
| Repository hygiene | `git diff --check` passed; no commit, push, deployment, real credentials, real probes, or notifications. |

Invalid intermediate runs caused by concurrent build locks or missing nested-Docker access are excluded from evidence. The corrected isolated run is the 42/42 result above.

## Remaining gaps and risks

- The duplicate-device portion of UA-01 is confirmed and fully enforced. Remaining UA-01 Site/VLAN/role/operational-threshold details do not block this rule or the technical gate.
- Production OIDC, Agent enrollment/configuration, and complete role-matrix breadth remain later work.
- CSV preview state is intentionally node-local, bounded, and retained for 15 minutes; restart discards it.
- Full per-aggregate authorization/concurrency breadth, concurrent CSV/duplicate races, and soft-disable/history E2E coverage remain explicit QA gaps.
- `gitleaks` and `trivy` are unavailable; container images use version tags rather than immutable digests.
- VictoriaMetrics dependency behavior is a WP-05 concern; current readiness verifies PostgreSQL schema/connectivity.

## Next checkpoint

Do not start WP-03 integration. Agent C may be started only on explicit user direction, consuming the frozen WP-02 OpenAPI artifact. Keep shared-contract, migration, and ownership rules in `docs/repository-ownership.md`.

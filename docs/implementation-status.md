# EE Pulse implementation status

Last updated: 2026-08-10 (Asia/Bangkok)  
Owner: Lead/Integration Agent  
Current checkpoint: WP-03 contract design approved and frozen; awaiting documentation checkpoint commit

## Outcome

WP-00 and WP-01 remain verified. The user approved WP-02 after the backend baseline and authorized frontend slice passed Lead integration:

- Agent A delivered WP-02 Backend/PostgreSQL inventory.
- Agent B delivered contract-neutral Probe Agent foundations.
- Agent D delivered QA/security gates, fixtures, review, and integrated verification.
- Lead reviewed the changes, returned in-scope defects to their owners, rebuilt the final stack, and generated/reviewed `docs/api/openapi-v1.json`.
- Agent C delivered the Site, Device, Probe-configuration, and CSV inventory UI plus Vitest and Playwright coverage. Lead returned production-authentication, OpenAPI-filter, and conflict-recovery defects; the corrected implementation passes the full integration gate.

The committed WP-02 checkpoint was clean at `8ca821d`. The user approved the design-only enrollment, identity, heartbeat, configuration, and network-scope contract plus ADR-007 through ADR-009. WP-03 implementation has not started; Agents A, B, C, and D remain idle until the user confirms the documentation checkpoint commit.

## Current repository

| Area | State |
| --- | --- |
| Specifications | Six authoritative files under `docs/spec`; no `AGENTS.md` or additional repository instruction file is present. |
| Git | Local `main` and `origin/main` were synchronized at `8ca821d` before this design gate; only Lead-owned WP-03 proposal/control documents are now modified or untracked. |
| Backend | PostgreSQL-backed Site, Device, AgentGroup, Probe, MaintenanceWindow, AuditEvent, CSV import, authorization policies, migration, seed gate, and dependency-aware readiness. |
| Contracts/OpenAPI | Compatible v1 inventory DTOs; checked-in OpenAPI 3.1.1 artifact with 14 paths, 19 protected operations, Bearer/OIDC-ready metadata, and explicit Development-header note. |
| Probe Agent | Deterministic jitter, monotonic scheduling, per-Probe non-overlap, bounded concurrency, injectable transport seam, and graceful host behavior; no network probe implementation. |
| Web | Responsive inventory console for Sites, server-filtered/paged Devices, create/edit/soft-disable, Probe fields, CSV preview/commit, row errors, stale/partial/retry states, and actionable concurrency conflicts. Development synthetic identity is absent from the production bundle, which fails closed pending OIDC. |
| QA | 43 .NET tests, 7 frontend component tests, 2 Playwright inventory tests, PostgreSQL integration coverage, quality/security script, and WP-02 evidence matrix. |
| Compose | PostgreSQL 18.4, VictoriaMetrics 1.148.0, and API healthy; only API publishes a host port. |

## Work-package status

| WP | Status | Evidence / next boundary |
| --- | --- | --- |
| WP-00 | Verified | Audit/control documents and repository governance established. |
| WP-01 | Verified | Foundation/project graph/contracts/health/CI/Compose/ADRs pass. |
| WP-02 | Approved | Backend/PostgreSQL plus inventory frontend pass together: metadata, migration, APIs, validation, authorization, audit, CSV, OpenAPI, enabled-only Device uniqueness, accessible UI, component tests, and critical-flow browser tests. |
| WP-03 | Contract approved; implementation gated | Additive v1 endpoints, schemas, auth/error semantics, persistence/migration plan, threat analysis, test matrix, ownership, and Agent A/B/D acceptance criteria are frozen in `docs/api/wp03-agent-contract-proposal.md`. No implementation has started. |
| WP-04 | Partial foundation only | Contract-neutral scheduling/concurrency/transport seams exist; ICMP/config-dependent work is not started. |
| WP-05 through WP-11 | Not started | Continue in dependency order after the next approved checkpoint. |

## Stable contract decision

Lead confirms that Agent C consumed the frozen WP-02 inventory/OpenAPI v1 surface without changing it. The checked-in artifact is `docs/api/openapi-v1.json`.

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
| .NET tests | 43 passed: Unit 26, Agent 10, Integration 7; 0 failed/skipped. |
| NuGet vulnerability audit | No vulnerable packages reported across all 12 projects. |
| Frontend | ESLint passed; Vitest 7/7; production build transformed 954 modules (320.41 kB, 102.40 kB gzip); Playwright inventory 2/2; npm audit 0 vulnerabilities. |
| Production frontend auth | Bundle inspection found no synthetic auth headers, role chooser, or Development-access markers; production renders a fail-closed OIDC-not-configured state. |
| Agent B independent review | Agent build 0 warnings/errors; Agent tests 10/10; code reviewed by Lead and QA. |
| Quality/security script | Passed foundation, working-tree/history secret patterns, exact versions, lockfile, Compose exposure/network/privilege, and image-tag checks. |
| Compose/runtime | Config valid; PostgreSQL, VictoriaMetrics, API healthy; live/ready schema v1; anonymous inventory HTTP 401. |
| OpenAPI | 3.1.1; 14 paths; 19 protected operations; Bearer scheme; protected operations declare 401/403; health remains unauthenticated. |
| Repository hygiene | `git diff --check` passed; no commit, push, deployment, real credentials, real probes, or notifications. |

The first sandboxed Vitest attempt could not spawn Vite's helper (`EPERM`); the authorized rerun is the passing 7/7 evidence above. Invalid earlier concurrent/nested-Docker runs remain excluded; the current isolated .NET run is 43/43.

## Accepted follow-on risks

- The duplicate-device portion of UA-01 is confirmed and fully enforced. Remaining UA-01 Site/VLAN/role/operational-threshold details do not block this rule or the technical gate.
- Production OIDC remains pending under UA-06; Agent enrollment/configuration and complete role-matrix breadth remain later work.
- The checked-in OpenAPI models the Device-list `criticality` query as an integer enum without published names; the frontend uses contract values 0-3 while presenting Low/Normal/High/Critical labels. Clarifying enum metadata is a future compatible-contract task.
- CSV preview state is intentionally node-local, bounded, and retained for 15 minutes; restart discards it.
- Full per-aggregate authorization/concurrency breadth, concurrent CSV/duplicate races, and soft-disable/history E2E coverage remain explicit QA gaps.
- `gitleaks` and `trivy` remain accepted WP-11 verification gaps; container images use version tags rather than immutable digests.
- Frontend contract types are manually maintained against the frozen OpenAPI; compatibility tests and Lead review mitigate drift until generated-client work is selected.
- The host lacks .NET SDK 10; the pinned `mcr.microsoft.com/dotnet/sdk:10.0.302` container remains the verified build environment.
- VictoriaMetrics dependency behavior is a WP-05 concern; current readiness verifies PostgreSQL schema/connectivity.

## Next checkpoint

Create the documentation checkpoint commit using the recommended Lead message, then notify Lead when it is complete. Do not spawn Agents A, B, or D until that confirmation; do not start Agent C. The generated WP-02 `openapi-v1.json` remains unchanged at this design checkpoint.

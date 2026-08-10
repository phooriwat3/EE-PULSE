# WP-02 QA and security test matrix

Status: evidence-based checkpoint for the approved WP-02 inventory contract. Last verified 2026-08-10 with 42/42 .NET tests passing (25 unit, 10 Agent, 7 PostgreSQL/API integration), the local quality/security gate passing, and the Compose API/PostgreSQL/VictoriaMetrics services healthy.

Status legend: **Verified** has direct executable evidence for the stated scope; **Partial** has useful evidence but not the full required breadth; **Gap** has no sufficient WP-02 evidence; **Blocked** depends on a later Work Package or user decision.

| Area | Status | Verified evidence | Remaining gap / next evidence |
| --- | --- | --- | --- |
| Migration and readiness | Verified | `PostgreSqlPersistenceTests` migrates an empty PostgreSQL container; readiness is 503 before migration and 200 after migration. Compose startup also applies the migration and becomes healthy. | A dedicated automated reapplication/restart test would make repeatability evidence more explicit. |
| UTC | Verified | Domain tests reject non-UTC timestamps; PostgreSQL round-trip asserts UTC offset and migrations use `timestamp with time zone`. | Broader timestamp coverage continues with later entities. |
| Site | Partial | Administrator creates a Site through the API; normalized code and non-UTC rejection have unit coverage. | Update, pagination/filter, enable/disable, stale-write, and complete role-policy coverage are not exercised. |
| Device validation | Verified | Unit tests cover IPv4 normalization/rejection, ASCII hostname normalization and invalid hostname forms, tags, and criticality; validation is syntax-only and performs no DNS/network operation. | Boundary breadth can grow with contract evolution. |
| Device duplicate policy | Verified | PostgreSQL partial unique index enforces normalized Site + IP among enabled Devices; API and two-context tests cover concurrent conflict, disabled reuse/history, cross-Site reuse, hostname reuse, and re-enable conflict; CSV covers enabled existing/preview duplicates and disabled reuse. | Confirmed business rule; UI coverage remains WP-07. |
| Device lifecycle | Partial | Device update, row-version advance, stale-write 409, Viewer delete denial, Administrator permanent deletion, and audit attribution are exercised. Domain models retain an enabled flag for soft disable. | Soft-disable/history retention is not exercised end to end; deleting a Device with dependent history is not covered. |
| Probe | Partial | Unit boundary tests cover interval, timeout, attempts, thresholds, RTT ordering, enabled state, and configuration-version increment; API creation is exercised. | API update, disable persistence, pagination/filter, role breadth, and stale-write behavior are not exercised. |
| Agent group | Partial | Administrator API creation and PostgreSQL association through Probe creation are exercised. | List/update/disable/concurrency and full role-policy coverage remain. |
| Maintenance window | Partial | Unit tests cover UTC range and exactly-one-scope validation; PostgreSQL persistence is exercised. | API CRUD, missing-scope references, enable/disable, concurrency, and role breadth remain. |
| Optimistic concurrency | Partial | A two-context PostgreSQL Device test proves stale writes throw; API stale Device update returns 409 without overwriting the newer value. | Equivalent tests are still needed for Site, AgentGroup, Probe, and MaintenanceWindow. |
| Pagination and ordering | Partial | API test proves deterministic Device pagination with duplicate names and no overlap across two pages; implementation uses unique ID tie-breakers for inventory lists. | Invalid page-size boundaries and stable multi-page tests for every aggregate remain. |
| Filtering and search | Partial | PostgreSQL-backed API test proves address search over the `inet` column; Device filter operations are present in the approved OpenAPI surface. | Composed Site/area/type/criticality/tag/enabled filters and authorization-leakage cases remain. |
| CSV preview | Partial | API tests cover valid/error row counts, existing duplicate reporting, strict malformed UTF-8 rejection, unknown-length payload limit (413), bounded preview cache (429), and retention of an unexpired token at capacity. | Missing/extra headers, empty input, quoted-field boundaries, preview no-write assertion, and per-row validation breadth remain. |
| CSV commit | Partial | Actor-bound token rejects cross-actor commit (403); commit creates/audits the valid row; retry returns the same IDs with `AlreadyCommitted`; replay survives cache-capacity pressure. | Concurrent commit and process-restart/durable-token semantics are not exercised. The current bounded cache is intentionally process-local. |
| CSV/export safety | Blocked | Import size, encoding, and field validation reduce unsafe input. | Spreadsheet-formula export neutralization is WP-09 work; control-character/log-safety assertions are not yet present. |
| Audit content | Partial | PostgreSQL tests verify audit persistence, actor attribution for API mutations/import/delete, Development seed audit, and application-context rejection of modified AuditEvents. | Tests do not assert every action's before/after JSON, correlation ID, source IP, UTC, or secret redaction. |
| Audit immutability | Partial | `EePulseDbContext` rejects AuditEvent modification/deletion and has an automated PostgreSQL-backed assertion. | Database-role permissions or a database-level control preventing direct UPDATE/DELETE are not implemented or tested. |
| Authorization | Partial | Tests cover anonymous inventory 401, malformed privileged Development actor 401, Viewer delete 403, Engineer inventory workflow, Administrator create/delete, and cross-actor CSV commit 403. | Full Viewer/Operator/Engineer/Administrator/Auditor coverage for every operation is still required; production OIDC belongs to later WPs. |
| Problem Details and errors | Partial | Tests assert Problem Details for duplicate conflict and exercise stale-write 409, forbidden 403, malformed input 400, oversized input 413, and capacity 429. Correlation preservation is tested on health. | Missing-resource, validation-field, correlation-on-all-errors, and unexpected database failure cases need a table-driven API suite. |
| OpenAPI | Verified | Runtime/integration assertions cover OpenAPI 3.x, health, Site, AgentGroup, Device CRUD/import, Probe, and Maintenance paths plus representative success schemas; live document contains 14 paths. | A checked-in generated artifact/client policy remains Lead-owned; full response/security metadata review should accompany client generation. |
| Development identity and secrets | Partial | Privileged Development requests require a non-empty synthetic actor; production cannot authenticate through the Development handler. Working-tree/reachable-history secret scans and NuGet/npm audits pass. | Production OIDC/group mapping is later work. `gitleaks` and `trivy` are not installed, so dedicated secret/container scans are not passed. |
| Exposure and dependency health | Verified | Quality gate proves PostgreSQL/VictoriaMetrics publish no host ports, data network is internal, no service is privileged/host-networked, and images are explicitly tagged. Readiness covers missing, unreachable, and pending-migration PostgreSQL states; Compose services are healthy. | Immutable image digests and full container vulnerability scanning remain WP-11 hardening. |

## Verification commands

```powershell
docker run --rm --env TESTCONTAINERS_RYUK_DISABLED=true --env TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal --mount type=volume,source=ee-pulse-nuget-cache,target=/root/.nuget/packages --mount type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock --mount type=bind,source=${PWD},target=/source --workdir /source mcr.microsoft.com/dotnet/sdk:10.0.302 dotnet test src/backend/EePulse.sln --configuration Release --no-build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-quality-security.ps1
docker compose config --quiet
docker compose ps
```

The Docker-socket and host-override settings are needed only when Testcontainers itself runs inside the SDK container. CI or a host with .NET 10 can run the normal documented `dotnet test` command.

## Mandatory review invariants

- Tests use isolated local PostgreSQL containers and synthetic identities; no real credentials or networks.
- Preview does not mutate inventory or audit state; commit is actor-bound and bounded in memory.
- Validation does not resolve or probe user-provided hostnames.
- Database uniqueness and concurrency constraints backstop application validation.
- Error bodies and logs must not echo connection strings, tokens, raw exception details, or unbounded imported cell content.
- Lead/Integration owns future OpenAPI and inventory compatibility decisions; this matrix records evidence and does not redefine the contract.

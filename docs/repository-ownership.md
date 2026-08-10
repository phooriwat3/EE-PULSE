# Repository structure and ownership freeze

Status: Frozen at the WP-01 integration gate on 2026-08-10 (Asia/Bangkok).

The initial project set, project-reference graph, shared contract version, and ADR set are enforced by `scripts/verify-wp01-foundation.ps1`. Changes to those frozen items require Lead/Integration approval and a coordinated update to this document, the verification script, and affected consumers.

## Ownership boundaries

| Path | Primary workstream | Required coordination |
| --- | --- | --- |
| `src/backend/EePulse.Contracts/**` | Lead/Integration | Lead owns schema/API versioning. Backend, Probe Agent, Frontend, and QA review changes affecting their consumers. |
| `src/backend/EePulse.Domain/**` | Backend | Lead review for new cross-module boundaries. |
| `src/backend/EePulse.Application/**` | Backend | Lead review for shared abstractions or contract changes. |
| `src/backend/EePulse.Infrastructure/**` | Backend | Backend exclusively owns PostgreSQL migrations; Lead coordinates migration ordering. |
| `src/backend/EePulse.Api/**` | Backend | Lead review when OpenAPI or public behavior changes. |
| `src/backend/EePulse.Worker/**` | Backend | Lead review when process topology or shared messages change. |
| `src/agent/**` | Probe Agent | Lead review for shared contracts, security boundaries, or host topology. |
| `src/web/**` | Frontend | Lead review for generated-client contract changes; QA reviews critical-path testability. |
| `tests/EePulse.UnitTests/**` | Backend | QA may add cross-cutting fixtures through Backend review. |
| `tests/EePulse.IntegrationTests/**` | Backend | QA reviews integration coverage; Lead reviews shared-contract assertions. |
| `tests/EePulse.Agent.Tests/**` | Probe Agent | QA reviews queue, restart, and network-safety coverage. |
| `tests/e2e/**` | QA | Frontend reviews selectors, fixtures, and UI behavior. |
| `scripts/verify-*.ps1`, future `tests/quality/**` | QA | Lead reviews changes to mandatory gates. |
| `docker-compose.yml`, `deploy/compose/**`, `.github/**`, root build/version files | Lead/Integration | Backend, Frontend, Probe Agent, and QA review changes affecting their runtime or gate. |
| `deploy/agent/**` | Probe Agent | QA reviews install/upgrade verification; Lead reviews release integration. |
| `deploy/reverse-proxy/**` | Lead/Integration | Backend and QA/Security review exposure, TLS, and health routing. |
| `docs/adr/**`, `docs/api/**`, `docs/implementation-status.md` | Lead/Integration | Relevant workstream authors contribute; Lead accepts and freezes decisions. |
| `docs/runbooks/**`, `docs/risk-register.md`, `docs/requirements-traceability.md` | QA | Lead accepts checkpoint and release-state changes. |

## Frozen dependency direction

- `Domain` and `Contracts` have no project dependencies.
- `Application` depends only on `Domain` and `Contracts`.
- `Infrastructure` depends inward on `Application` and `Domain`.
- API and Worker are composition hosts; they depend on application/infrastructure layers, and only API directly consumes HTTP contracts.
- Agent Core consumes shared contracts; Agent Infrastructure depends on Agent Core; the Agent host composes Agent Core and Agent Infrastructure.
- Production projects never depend on test projects, the web application, or deploy assets.

## Shared contract change protocol

1. Lead/Integration owns `EePulse.Contracts`, `ApiVersions`, the generated OpenAPI surface, and compatibility decisions.
2. Contract v1 is the frozen baseline: NuGet/package version `1.0.0`, schema version `1`, and OpenAPI document name `v1`.
3. A compatible v1 addition requires Backend, Probe Agent, Frontend, and QA impact review plus contract/OpenAPI tests.
4. A breaking change requires a new explicit schema/API version; do not redefine v1 in place.
5. Backend alone owns migrations. No other workstream may couple a contract change to an uncoordinated migration.

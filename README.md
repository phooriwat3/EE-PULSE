# EE Pulse

EE Pulse is an internal IP-device monitoring platform. This repository is being built in dependency-ordered Work Packages from the specifications in `docs/spec`.

## Prerequisites

- .NET 10 SDK (the pinned SDK is in `global.json`)
- Node.js 22 and npm 10 or newer
- Docker with Compose

Do not use real credentials for local development. Copy `.env.example` to `.env` and replace its placeholder database password with a local-only value.

## Foundation quick start

```powershell
dotnet restore .\src\backend\EePulse.sln
dotnet build .\src\backend\EePulse.sln --configuration Release --no-restore
dotnet test .\src\backend\EePulse.sln --configuration Release --no-build

npm --prefix .\src\web ci
npm --prefix .\src\web run lint
npm --prefix .\src\web test
npm --prefix .\src\web run build

docker compose config
docker compose up -d --build
```

When running the API directly, its OpenAPI document is available at `/openapi/v1.json`; liveness and readiness are `/health/live` and `/health/ready`.

## Repository map

- `src/backend`: central modular-monolith solution (API, worker, domain, application, infrastructure, contracts)
- `src/agent`: Windows Service Agent, core logic, and infrastructure adapters
- `src/web`: React/TypeScript web application
- `tests`: unit, integration, Agent, and E2E tests
- `deploy`: Compose, Agent packaging, and reverse-proxy assets
- `docs`: ADRs, runbooks, traceability, status, risks, and governing specification pack

## Safety boundaries

- Never commit secrets or use real SMTP/webhook endpoints in automated tests.
- Never scan networks or probe an address outside an explicitly approved allowlist.
- The Agent never accepts arbitrary command execution.
- Production must use TLS and configured OIDC; development identity must fail closed outside Development.

See `docs/implementation-status.md` for the current checkpoint and `docs/user-actions.md` for external prerequisites.

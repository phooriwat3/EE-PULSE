# EE Pulse — AI Agent Prompts and Work Packages

## 1. Master prompt

Give the following prompt to the Lead/Integration AI coding agent:

```text
You are the lead engineer responsible for delivering the EE Pulse MVP.

Before changing code, read these documents completely:
- 00-START-HERE.en.md
- 01-PRD.en.md
- 02-TECHNICAL-SPEC.en.md
- 04-DELIVERY-CHECKLIST.en.md

Your objective is to build a production-oriented MVP, not a UI mockup. The system consists of:
1. React/TypeScript web dashboard.
2. ASP.NET Core 10 API and application worker.
3. .NET 10 Windows Service probe agent.
4. PostgreSQL metadata/workflow storage.
5. VictoriaMetrics time-series storage.
6. SQLite offline queue in the Agent.
7. Docker Compose deployment for central services.

Rules:
- Do not silently change product requirements or API contracts.
- Record material architecture decisions as ADRs.
- Keep the MVP as a modular monolith; do not introduce microservices or Kubernetes.
- Never store secrets in source control, logs, frontend bundles, or sample configuration.
- Never implement remote shell execution or unrestricted network scanning.
- All ingestion must be idempotent by batchId and runId.
- An offline Agent must result in UNKNOWN targets, not a storm of DOWN incidents.
- Use UTC for storage and event processing; convert only at presentation boundaries.
- Add tests with every behavior change.
- Preserve unrelated existing user changes.
- Before marking a Work Package complete, run formatting, linting, unit tests, integration tests where available, and production builds.
- Report exact commands run, results, known limitations, migrations, and changed files.
- If requirements conflict, stop implementation of the conflicting part and present the conflict with a recommended resolution.

Execute Work Packages in dependency order. Maintain docs/implementation-status.md containing:
- completed items
- in-progress item
- decisions
- test evidence
- risks/blockers
- next Work Package

Definition of complete means code, automated tests, documentation, deployment configuration, and verification evidence are all present.
```

## 2. Agent responsibility split

| Agent            | Ownership                                                                      |
| ---------------- | ------------------------------------------------------------------------------ |
| Lead/Integration | Contracts, architecture, repository structure, integration, final verification |
| Backend          | Domain, API, PostgreSQL, status engine, incidents, notifications               |
| Probe Agent      | Windows Service, scheduler, ICMP, SQLite queue, upload                         |
| Frontend         | React UI, API client, SignalR, charts, UI E2E tests                            |
| QA/Security      | Test matrix, load/failure tests, threat review, deployment validation          |

Coordination rules:

- The Lead creates contracts and the repository skeleton before parallel work begins.
- Shared DTO/OpenAPI changes are owned or reviewed by the Lead.
- Backend owns migrations.
- Each Agent should primarily work in a separate directory.
- Integration occurs one Work Package at a time, followed by the full applicable test suite.
- Do not merge work that does not build, even when described as temporary.

## 3. WP-00 — Discovery and repository audit

```text
Inspect the repository and all governing instruction files. Do not implement features yet.

Produce:
1. Current repository inventory and detected toolchain.
2. Gaps against the EE Pulse specification.
3. Proposed repository structure.
4. Dependency and version choices, preferring supported LTS/stable versions.
5. Risk register covering ICMP permissions, Windows Service installation, time-series cardinality, clock skew, offline buffering, authentication, and deployment.
6. A sequenced implementation plan mapped to WP-01 through WP-11.
7. docs/implementation-status.md.

Validate that secrets or unrelated user changes will not be overwritten. Do not make speculative refactors.
```

Exit criteria:

- Repository audit completed
- Conflicts and dirty files identified
- Tool versions selected
- Implementation-status document created

## 4. WP-01 — Foundation and contracts

```text
Create the EE Pulse repository foundation according to 02-TECHNICAL-SPEC.en.md.

Implement:
- .NET solution and projects with dependency direction enforced.
- React TypeScript Vite application.
- Shared versioned HTTP contracts and OpenAPI generation.
- Common error responses using Problem Details.
- Correlation ID middleware.
- UTC clock abstraction.
- Docker Compose skeleton for PostgreSQL and VictoriaMetrics.
- Formatting, linting, editor configuration, and test projects.
- ADR-001 through ADR-006 drafts.
- CI workflow that restores, lints, tests, and builds without deployment.

Do not implement business features beyond a vertical health-check slice.

Verify:
- clean backend build
- frontend lint, test, and build
- containers become healthy
- API live and readiness endpoints respond
```

## 5. WP-02 — Database and inventory

```text
Implement Site, Device, AgentGroup, Probe, MaintenanceWindow, and AuditEvent metadata.

Requirements:
- PostgreSQL migrations owned by the backend.
- UUID identifiers and timestamptz UTC fields.
- Optimistic concurrency for mutable configuration.
- CRUD APIs with pagination, filtering, validation, and role policies.
- Soft-disable rather than destructive deletion for normal workflows.
- CSV import preview and commit with row-level errors.
- Audit before/after values for configuration changes.
- Seed data only in Development.
- Unit and Testcontainers integration tests.

Generate or update OpenAPI and make the Web API client consume the defined contract.
```

## 6. WP-03 — Agent enrollment and configuration

```text
Implement secure Agent enrollment and versioned configuration delivery.

Requirements:
- One-time enrollment token stored hashed with expiry.
- Agent identity persisted under ProgramData-compatible storage.
- Heartbeat endpoint and Agent online/offline view.
- Agent-pull configuration with ETag or version number.
- Atomic application of configuration and last-known-good rollback.
- Allowed-network validation.
- Agent revocation capability.
- Never log tokens or credentials.
- Tests for expired, reused, revoked, malformed, and unauthorized enrollment.

For MVP development, permit a documented local credential option, but fail closed in Production if secure identity is not configured.
```

## 7. WP-04 — Probe scheduler and ICMP engine

```text
Implement the .NET Worker Service Probe runtime.

Requirements:
- Windows Service hosting support.
- Stable-hash deterministic jitter over each interval.
- Per-Probe non-overlap.
- Configurable global and per-target concurrency limits.
- ICMP attempt count, timeout, min/avg/max RTT, and packet loss.
- Error categories, not only free-text errors.
- Cancellation and graceful shutdown.
- Monotonic duration measurement.
- Self-health metrics and structured logs.
- Documented Windows permission and firewall behavior.

Write deterministic scheduler tests using a fake clock and Probe tests using an injectable Probe transport. Do not make unit tests depend on external public IP addresses.
```

## 8. WP-05 — Offline queue and ingestion

```text
Implement durable result delivery from Agent to Server.

Agent:
- SQLite WAL queue.
- Transactional enqueue.
- Batch by configurable count, size, and time.
- Exponential backoff with jitter.
- Delete only after acknowledgement.
- Queue quota, dead-letter behavior, and metrics.

Server:
- Request-body and batch limits.
- Authenticate the Agent.
- Validate configuration ownership.
- Idempotency by batchId and runId.
- Write metrics to VictoriaMetrics.
- Persist processing/outbox data required for status and Incident behavior.
- Return accepted, duplicate, and rejected counts.

Test API outage, retry, duplicate submission, partial rejection, process restart, and queue draining.
```

## 9. WP-06 — Status and incident engine

```text
Implement the status algorithm exactly as specified.

Requirements:
- UNKNOWN, UP, DEGRADED, DOWN, RECOVERING, MAINTENANCE, DISABLED.
- Consecutive failure and recovery thresholds.
- Result freshness and Agent heartbeat expiry.
- Late-event watermark so historical upload cannot corrupt current state.
- Atomic status-transition and Incident mutation.
- One open Incident per Probe/rule.
- OPEN, ACKNOWLEDGED, RESOLVED lifecycle.
- Comments and resolution notes.
- Transactional outbox for notification events.
- Basic flapping detection and suppression.

Create a table-driven state-machine test matrix covering every state transition, duplicate event, out-of-order event, maintenance, disable, and Agent outage.
```

## 10. WP-07 — Dashboard and device experience

```text
Implement the React UI using accessible, responsive components.

Pages:
- Login and session handling.
- Overview summary and filters.
- Device list with server-side pagination.
- Device create, edit, and import.
- Device details with RTT/loss charts and status timeline.
- Incident center with acknowledge, resolve, and comments.
- Agent list and details.
- Maintenance windows.
- Audit log for authorized roles.

Requirements:
- TanStack Query for server state.
- SignalR for incremental live updates, with reconnect and fallback refresh.
- Explicit loading, empty, stale-data, partial-error, and permission-denied states.
- Do not use color as the only status indicator.
- Display timestamps in the selected or Site timezone while the API remains UTC.
- Do not fabricate live data outside Development fixtures.
- Vitest component tests and Playwright critical-path tests.
```

## 11. WP-08 — Notifications

```text
Implement SMTP email and generic webhook delivery from the transactional outbox.

Requirements:
- Open, reminder/escalation, and recovery messages.
- Deduplication by Incident, event type, channel, and policy.
- Retry with bounded exponential backoff.
- Delivery log with redacted request and response data.
- Quiet hours and maintenance suppression.
- Test-notification operation.
- SSRF protection for webhook URLs using an explicit allowlist policy.
- Template encoding to prevent injection.

Use fake SMTP and webhook servers in automated tests. Never send real external messages from tests.
```

## 12. WP-09 — Reports and retention

```text
Implement availability and downtime reports.

Requirements:
- Per-Device and per-Site time-range reports.
- Separate planned maintenance, unplanned downtime, and unknown coverage.
- CSV export protected against formula injection.
- Documented raw-metric retention configuration.
- Downsampling design and initial scheduled aggregation if required by the selected retention.
- Query limits, pagination, and maximum time range.
- Cross-check report calculations against status-transition fixtures.
```

## 13. WP-10 — Packaging, deployment, and operations

```text
Create production-oriented deployment artifacts.

Central:
- Docker Compose with pinned images, volumes, health checks, restart policies, internal networks, and an example environment file.
- Reverse-proxy TLS configuration template.
- Database migration procedure.
- Backup and restore scripts and runbooks.
- Upgrade and rollback runbook.

Agent:
- Windows Service publish profile.
- Idempotent PowerShell install, upgrade, and uninstall scripts or an MSI project.
- Program Files for binaries and ProgramData for mutable data.
- Service recovery configuration.
- Preserve the local queue during normal upgrade/uninstall unless purge is explicit.

Document firewall ports, service accounts, certificate handling, minimum requirements, and troubleshooting.
Test installation in a disposable Windows environment where available; otherwise document the unverified step explicitly.
```

## 14. WP-11 — QA, security, and release candidate

```text
Act as the release owner. Do not add new scope unless required to fix a release blocker.

Perform:
- Full requirements traceability against 01-PRD.en.md.
- Backend, Agent, frontend, integration, and E2E tests.
- 500-target/30-second load test for at least 60 minutes.
- Central outage and recovery test.
- Agent restart and Server restart test.
- Duplicate and out-of-order event test.
- Authorization matrix test.
- Dependency and container vulnerability scan.
- Secret scan.
- Backup and restore rehearsal.
- Production build from a clean checkout.

Produce:
- release-notes.md
- test-report.md with commands and evidence
- known-limitations.md
- threat-model.md
- operations handoff
- signed or tagged release instructions

Do not declare the MVP complete if any mandatory checklist item lacks evidence.
```

## 15. Code review prompt

```text
Review this EE Pulse change as a production monitoring-system reviewer.

Prioritize concrete defects over style. Check:
- incorrect DOWN/UNKNOWN behavior
- lost or duplicated Probe results
- race conditions in state transitions
- out-of-order event handling
- transaction/outbox consistency
- unbounded queues or retries
- metric-label cardinality
- authentication and authorization bypass
- enrollment-token or secret exposure
- SSRF and unrestricted Probe targets
- timezone and UTC errors
- destructive migrations or upgrades
- missing failure-path tests

For every finding, provide severity, exact file and line, failure scenario, impact, and the smallest safe correction. If no actionable finding exists, say so and list residual test gaps.
```

## 16. Bug-fix prompt

```text
Diagnose the reported EE Pulse defect before changing code.

1. Reproduce it with the smallest deterministic test.
2. Identify the root cause and affected invariants.
3. Check whether persisted state, queued results, incidents, or metrics may already be corrupted.
4. Implement the smallest safe fix.
5. Add a regression test that fails before the fix.
6. Run the relevant package tests plus integration tests for shared contracts and state logic.
7. Report whether data repair or migration is required.

Do not mask the symptom with retries, timeouts, or broad exception handling unless the root cause specifically requires it.
```

## 17. Prompts to avoid

Avoid vague prompts such as:

```text
Build a complete and attractive Ping monitoring system.
```

Such a prompt has no contract, failure behavior, security boundary, test evidence, or Definition of Done. It commonly produces an attractive demo that does not survive Server outages, duplicate events, or Agent outages.

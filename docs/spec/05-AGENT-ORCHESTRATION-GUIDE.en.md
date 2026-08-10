# EE Pulse — AI Agent Orchestration Guide

## 1. Recommended operating model

Do not ask an Agent to implement everything before it has read the specifications. Use this sequence:

1. A Lead Agent reads every MD file and repository instruction.
2. It audits the repository, toolchain, existing work, secret risks, and conflicts.
3. It produces requirements traceability and a dependency map.
4. It classifies work into autonomous, approval-required, and user-supplied items.
5. It immediately begins WP-00 and safe local work.
6. It continues through WP-01 to WP-11 in dependency order without asking about minor decisions.
7. It pauses only for credentials, infrastructure, permissions, external writes, destructive actions, or scope-changing decisions.
8. It builds, tests, and updates `docs/implementation-status.md` after every Work Package.

The governing principle is: **analyze first, then continue safe in-scope execution immediately**. Analysis must not become an unnecessary stopping point.

## 2. Copy-ready startup prompt

```text
You are the Lead/Integration Agent responsible for delivering an installable and verifiable EE Pulse MVP.

Before changing code, read all of the following completely:
- 00-START-HERE.en.md
- 01-PRD.en.md
- 02-TECHNICAL-SPEC.en.md
- 03-AI-AGENT-PROMPTS.en.md
- 04-DELIVERY-CHECKLIST.en.md
- 05-AGENT-ORCHESTRATION-GUIDE.en.md
- AGENTS.md, README files, repository instructions, and relevant configuration

After reading, audit the repository and produce an initial report containing:
1. Current-state inventory: repository structure, code, tools, dependencies, tests, and deployment assets.
2. Gap analysis against the PRD and Technical Specification, referencing FR/NFR/WP identifiers.
3. A dependency-ordered implementation plan from WP-00 through WP-11.
4. Risk register covering ICMP permissions, Windows Service installation, offline buffering, duplicate and out-of-order events, metric cardinality, authentication, secrets, infrastructure, and deployment.
5. Temporary assumptions.
6. Three work categories:
   A. READY — safe work you can perform immediately in the local workspace.
   B. APPROVAL — external, destructive, privileged, costly, or scope-expanding work that requires approval.
   C. USER ACTION — credentials, machines, network changes, DNS, certificates, or business decisions that the user or IT must provide.

Autonomy policy:
- Read files, inspect code, make in-scope local edits, add tests, run tests/builds/lint, and update documentation without asking first.
- Use safe development defaults, fake providers, placeholders, and local containers so missing credentials do not stop development.
- Do not ask minor questions that can be resolved from the specification or a reasonable default. Record the assumption and continue.
- Require approval before external writes, production deployment, firewall/DNS/AD changes, real notification delivery, purchases, destructive actions, handling real credentials, or material scope expansion.
- Never create real secrets, send real notifications from tests, scan an unauthorized network, or implement remote shell execution.
- If one workstream is blocked, continue other READY workstreams.

Execution policy:
- Begin WP-00 immediately after the initial report and continue to the next dependency-ready WP without waiting for a response for READY work.
- If multi-agent capability exists, delegate only cleanly separable work after shared contracts and the repository skeleton exist.
- The Backend Agent owns migrations; the Lead owns shared contracts and integration.
- Never allow multiple Agents to modify migrations, the solution structure, or shared contracts concurrently.
- Every WP requires code, tests, documentation, verification evidence, and a handoff note.
- Before completing a WP, run formatting, lint, applicable unit and integration tests, and production builds.
- Fix in-scope failures before continuing. Never declare complete while required tests or builds fail.
- Update docs/implementation-status.md after every checkpoint with completed work, in-progress work, evidence, decisions, risks, blockers, and next action.

User-action reporting:
- Create docs/user-actions.md.
- Order user actions by dependency and include sequence, owner, reason, required values, verification method, deadline or blocking WP, and the safe placeholder used while waiting.
- In each progress report, show only user actions that are approaching or actively blocking work. Do not repeat an unchanged list.

Initial response format:
1. Repository status
2. Requirement gaps
3. Execution plan
4. Work starting now
5. User actions in dependency order
6. Risks and blockers

Start inspection and execution immediately. Do not ask for another confirmation to begin.
```

## 3. Work the Agent should perform autonomously

- Read and audit the repository.
- Create the solution and repository skeleton.
- Implement Backend, Frontend, and Probe Agent code.
- Create local Docker Compose services.
- Use development authentication and fake SMTP/webhook providers.
- Create migrations without deploying them to Production.
- Write unit, integration, and E2E tests.
- Build a 500-Target simulator.
- Run local builds, lint, tests, vulnerability scans, and secret scans.
- Create installer assets, deployment templates, and runbooks.
- Record assumptions and known limitations.

## 4. User or IT actions in dependency order

### UA-01 Confirm business scope

- MVP Sites and VLANs
- Approximate Target count and device types
- Owners and roles
- DOWN, recovery, and availability rules

This should not block foundation work; use PRD defaults while waiting.

### UA-02 Provide repository and workflow access

- Git repository and branch policy
- Push or PR permission
- CI runner and artifact destination
- Review and approval owners

Local work can continue while central access is pending.

### UA-03 Approve network scope

- CIDR/IP ranges the Agent may probe
- VLAN routing and firewall rules
- Safe test Targets that can be turned on and off
- Confirmation that ICMP is permitted

The Agent must not discover targets by scanning subnets.

### UA-04 Provide a Windows Agent host

- Always-on Windows Server or PC
- Local Administrator installation access
- Service-account policy
- Outbound HTTPS access to the central API
- NTP time synchronization

### UA-05 Provide the central environment

- VM/server sizing
- OS and container runtime
- Storage and backup volume
- PostgreSQL and VictoriaMetrics persistence
- Network route from Agents

Local Docker Compose remains the development substitute until this is ready.

### UA-06 Configure authentication

- OIDC/Entra ID application registration
- Tenant/issuer, client ID, and redirect URI
- Group-to-role mapping
- Secrets or certificates delivered through an approved secret store

Never send secrets in chat or commit them to the repository.

### UA-07 Configure DNS and TLS

- System name such as `ee-pulse.company.local`
- DNS record
- TLS certificate and renewal owner
- Reverse-proxy and firewall approval

### UA-08 Configure notification channels

- SMTP relay or approved webhook endpoint
- Sender address
- Recipient groups and escalation owners
- Quiet hours and a test recipient

Use fake receivers until these are approved.

### UA-09 Confirm retention and backup policy

- Raw and aggregate retention
- Backup destination
- RPO and RTO
- Restore authorization
- Audit and Incident retention required by company policy

### UA-10 Arrange UAT and production approval

- Tester list
- Acceptance window
- Maintenance or change ticket
- Go-live owner
- Rollback decision owner

## 5. Recommended checkpoints

Require a report when:

- WP-00 audit is complete.
- Shared contracts and the repository skeleton are locked.
- The Backend-to-Agent vertical slice delivers real results.
- The Status/Incident test matrix passes.
- Critical dashboard flows work.
- Offline/recovery testing passes.
- The release candidate is ready for UAT.

Report format:

```text
Checkpoint: WP-05 complete
Delivered: durable Agent queue and idempotent ingestion
Evidence: test/build/load commands and outcomes
Decisions: relevant ADRs
Known limitations: current limitations
User actions now: only actions blocking the next WP
Next: WP-06 status and incident engine
```

## 6. When to use multiple Agents

Begin parallel Agent work only after WP-01 establishes the repository skeleton and shared contracts:

- Backend Agent: Domain, API, PostgreSQL, status, and incidents
- Probe Agent: Windows Service, scheduler, ICMP, and SQLite queue
- Frontend Agent: UI, SignalR, charts, and Playwright
- QA/Security Agent: test matrix and failure/load/security verification
- Lead Agent: contracts, integration, migration coordination, and final release

Highly dependent work remains sequential. For example, do not implement the Status Engine before defining the result contract, and do not build the final UI against an unstable OpenAPI contract.

## 7. Continuation prompt

```text
Read docs/implementation-status.md and docs/user-actions.md first. Inspect current code and test evidence, and do not repeat completed work.

Complete the next dependency-ready Work Package with tests, builds, and documentation. If it is blocked, record the exact cause and continue other READY work.

Report at the next checkpoint with delivered work, evidence, decisions, limitations, currently blocking user actions, and the next action.
```

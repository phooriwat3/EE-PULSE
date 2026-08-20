# EE Pulse risk register

Last updated: 2026-08-20
Scale: likelihood/impact are Low (L), Medium (M), or High (H). Residual ratings assume the treatment is implemented.

| ID | Risk | L | I | Treatment / control | Owner / WP | Residual |
| --- | --- | --- | --- | --- | --- | --- |
| R-01 | ICMP needs privileges or is blocked and is not proof of application health. | H | H | WP-04 deterministic fake-transport foundation, fixed categorized errors, IPv4-only dual AllowedNetworks enforcement, controlled targets, documented Windows limitation; no discovery/scanning. Real ICMP remains separately gated. | Agent + IT, WP-03/04/10 | M/H |
| R-02 | Windows Service account/install/recovery cannot be proven here. | M | H | Idempotent installer, Program Files/ProgramData split, queue preservation, disposable Windows reboot/recovery test. | Agent + IT, WP-10/11 | L/H |
| R-03 | A 72-hour queue exhausts disk or silently loses data. | M | H | UA-11 approves a 5 GB quota, greater-of-2-GB-or-10%-volume reserve, 80% degraded health, 95%/reserve-breach production stop, below-70% resume, acknowledged cleanup within 24 hours, durable quarantine, and no age-based unacknowledged deletion; disk-full and recovery tests are required. | Agent, WP-05/11 | M/H |
| R-04 | Duplicate batches/runs create duplicate metrics, transitions, incidents, or notifications. | H | H | Unique batch/run IDs, durable dedupe, one-open-incident constraint, transactional outbox/delivery keys. | Backend, WP-05/06/08 | L/H |
| R-05 | Late/clock-skewed events corrupt current state. | H | H | UTC, skew flag, per-Probe watermark, historical-only late writes, NTP, deterministic tests. | Backend/Agent, WP-05/06 | L/H |
| R-06 | Agent outage causes a DOWN/notification storm. | M | H | Heartbeat/freshness expiry forces UNKNOWN; state-matrix and E2E tests. | Backend, WP-03/06/11 | L/H |
| R-07 | Unbounded labels cause VictoriaMetrics cardinality growth. | M | H | Fixed label allowlist; reject error/hostname/tag labels; cardinality/load and retention checks. | Backend/QA, WP-05/09/11 | L/M |
| R-08 | OIDC mapping or development login grants excess production access. | M | H | API/UI policies, explicit groups, production fail-closed startup, full role matrix. | Backend/Web/IT, WP-02/03/07/11 | L/H |
| R-09 | Enrollment/Agent credentials leak. | M | H | Hashed one-time expiring tokens, redaction tests, approved secret store, rotation/revocation, secret scans. | Backend/Agent, WP-03/11 | L/H |
| R-10 | Webhooks enable SSRF or secret disclosure. | M | H | HTTPS/host/network allowlist, DNS/IP revalidation, bounded redirects/body, redaction, fake receivers. | Backend/Security, WP-08/11 | L/H |
| R-11 | PostgreSQL/VictoriaMetrics partial failure loses or diverges data. | M | H | Durable ingest record, retry semantics, dependency health/metrics, outage/restart tests. | Backend, WP-05/11 | M/H |
| R-12 | Concurrent state processing opens multiple incidents. | M | H | Per-Probe serialization/optimistic concurrency; atomic state/transition/incident/outbox; uniqueness. | Backend, WP-06 | L/H |
| R-13 | Deployment exposes stores or uses weak defaults. | M | H | Internal data network, proxy-only production path, placeholders, fail-closed validation, pinned images. | Integration/IT, WP-01/10 | L/H |
| R-14 | Backups cannot meet RPO/RTO or restore both stores consistently. | M | H | Approved policy, scripts, versioned runbook, isolated restore rehearsal. | Operations, WP-09/10/11 | M/H |
| R-15 | Unbounded queues/backpressure miss performance targets. | M | H | WP-04 locally verifies bounded deterministic admission, global/per-target limits, coalesced missed runs, and lag/skipped runtime behavior with fakes; WP-05 adds durable queues/batches; simulator and 60-minute evidence remain. | All/QA, WP-04/05/07/11 | M/H |
| R-16 | Timezone/DST handling corrupts maintenance or reports. | M | M | UTC core; explicit timezone mapping; DST fixtures. | Backend/Web, WP-06/07/09 | L/M |
| R-17 | Host toolchain cannot reproduce net10.0 builds. | H | M | SDK 10.0.302 pin and verified official container; CI setup-dotnet 10.0.x. | Integration, WP-01 | M/L |
| R-18 | Docker access depends on engine state and local permission boundary. | M | M | Document prerequisite; use approved local Docker execution; Compose config/build/wait health gate. | User/Integration, WP-01/10 | L/M |
| R-19 | Remote workflow evidence is incomplete despite a configured `origin`. | M | M | Confirm branch/PR/CI/artifact/reviewer policy through UA-02 and obtain clean-clone CI evidence. | Repository owner, WP-11 | M/M |
| R-20 | Documentation or tooling retains the former `docs/spac` path. | L | L | `docs/spec` is authoritative; Lead corrected current README/control references and verification should reject future drift. | Integration, continuous | L/L |
| R-21 | Readiness omits a required dependency. | M | H | WP-02 now checks PostgreSQL connectivity and pending migrations; add VictoriaMetrics semantics in WP-05. | Backend, WP-05 | L/M |
| R-22 | A clean package audit is mistaken for a complete supply-chain/container scan. | M | H | Keep NuGet/npm audits in CI; add SBOM, image scan, and dedicated secret scan in WP-11; resolve critical/high findings. | QA/Security, WP-11 | L/H |
| R-23 | Parallel agents drift from frozen shared contracts. | M | H | WP-02 OpenAPI is checked in and Lead-frozen; generate clients from it, require compatibility tests, and keep migrations Backend-owned. | Lead/All, continuous | L/M |
| R-24 | Node-local CSV preview tokens disappear on restart or cannot be shared across API replicas. | M | M | Bounded 15-minute cache is explicit for MVP single-node; return clear invalid-token behavior and revisit durable/distributed storage before scale-out. | Backend, WP-10/11 | M/L |
| R-25 | Synthetic Development authentication or its role headers leak into a production Web bundle. | M | H | Compile-time Development gating, API-client fail-closed checks, production authentication-required state, bundle-content verification, and later production OIDC integration. | Web/Security, WP-02/07/11 | L/H |
| R-26 | Hand-maintained frontend types drift from the frozen OpenAPI artifact. | M | M | Contract-shaped types, request-level component tests, runtime/checked-in OpenAPI compatibility gate, Lead review, and generated-client evaluation before broader API growth. | Lead/Web, continuous/WP-07 | L/M |
| R-27 | Enrollment or rotation concurrency creates multiple identities, leaks a response secret, or locks an Agent out. | M | H | Row-locked transactional one-time token use, digest-only persistence, one pending credential, promote-on-first-use rotation, strict redaction, and concurrency/lost-response tests. | Backend/Agent/Security, WP-03/11 | L/H |
| R-28 | Heartbeat clock skew or inconsistent configuration acknowledgement misstates Agent status/effective version. | M | H | Server receive time, deterministic 60-second default expiry, skew flag, immutable monotonic snapshots, idempotent acknowledgements, and fake-clock/restart tests. | Backend/Agent, WP-03/06 | L/M |
| R-29 | A compromised Server widens Agent target scope or supplies executable configuration. | M | H | Local non-expandable network ceiling, IPv4-only Server and Agent CIDR checks, closed ICMP-only schema, full-snapshot rejection, execution-time containment, and no command/DNS fields. | Lead/Backend/Agent/Security, WP-03/04/11 | L/H |
| R-30 | A disconnected Agent continues probing after central revocation because it cannot receive the 410 response. | M | M | Server rejects immediately; Agent halts on reconnect; short heartbeat/config polling; credential expiry; document that immediate offline revocation requires an external host/network control. | IT/Agent, WP-03/10/11 | M/M |

## Temporary assumptions

| ID | Assumption | Revisit by |
| --- | --- | --- |
| A-01 | The six files under `docs/spec` are authoritative. | Revisit only through a governed specification change. |
| A-02 | PRD defaults apply until UA-01: 30 s interval, 2 s timeout, 3 attempts, failure threshold 3, recovery threshold 2. | WP-02 acceptance |
| A-03 | Scale means 500 enabled ICMP probes, not 500 devices each with multiple active probes. | WP-04/load design |
| A-04 | Store/process UTC; Site timezone affects presentation, maintenance interpretation, and reporting only. | WP-06/07 |
| A-05 | Development uses placeholder identity and fake notifications; Production fails closed without OIDC/TLS/secrets. | WP-03/08/10 |
| A-06 | Local Compose with named volumes is the development substitute; only application entrypoints become host-facing in production. | WP-10 |
| A-07 | No target is probed before explicit allowlist approval; automated tests use fakes. | WP-03/04 |
| A-08 | Normal Device workflow is soft-disable; permanent deletion is exceptional, Administrator-only, and audited. | WP-02 |
| A-09 | Agent pull configuration is effective only after acknowledgement and retains a last-known-good version. | WP-03 |
| A-10 | Direct dependencies/images are version-pinned; automated updates require review. | Continuous/WP-11 |
| A-11 | UA-11-approved MVP policy: 5 GB Agent outbox quota; reserve is greater of 2 GB or 10% of hosting volume; degrade at 80%; stop new production/scheduling at 95% or reserve breach; resume below 70%; remove acknowledged rows within 24 hours; never age-delete unacknowledged rows. | Revisit through governed policy change |

## Checkpoint notes

- WP-01 is verified and WP-02 is user-approved. PostgreSQL, VictoriaMetrics, and API containers are healthy; readiness verifies PostgreSQL schema/connectivity.
- NuGet and npm audits report no known vulnerable packages. `gitleaks` and `trivy` are not installed, so full dedicated secret and image scanning remains outstanding.
- The approved WP-02 checkpoint is committed at `8ca821d`; local `main` and `origin/main` were synchronized and the worktree was clean before Lead created the WP-03 design documents.
- Agent C consumed the Lead-frozen WP-02 inventory/OpenAPI v1 contract. Frontend lint, 7 component tests, production build, 2 Playwright flows, full 43-test .NET gate, Compose/runtime, OpenAPI, and quality/security checks pass.
- Production Web authentication intentionally fails closed until UA-06/OIDC work; synthetic headers and role-selection markers are absent from the production bundle.
- The user accepted production OIDC, unavailable `gitleaks`/`trivy`, integer OpenAPI criticality values 0-3, manually maintained frontend contract types, and containerized .NET 10 builds as tracked follow-on risks rather than WP-02 blockers.
- WP-03 local integration checkpoint passed: 103/103 .NET tests, Release builds, migration/model coverage, Compose/runtime auth checks, generated OpenAPI SHA parity, frozen WP-02 comparison, frozen WP-03 proposal comparison, and quality/security gates. Agent C remained deferred.
- Residual WP-03 risks are accepted only for this local checkpoint: real Windows DPAPI/ACL/service recovery, approved production CIDRs/ICMP routing, disconnected-Agent revocation latency, OIDC/TLS deployment, and unavailable `gitleaks`/`trivy` evidence remain WP-10/11/operator work.
- WP-04 final integration review passed: Agent tests 112/112, formatting, Agent host and Agent Tests Release builds with 0 warnings/errors, quality/security, and `git diff --check`. It is fake-only deterministic probe-runtime evidence, not real ICMP, host/DI wiring, Windows Service, persistence, delivery, ingestion, UI, deployment, or IP discovery. `gitleaks` and `trivy` remain WP-11 gaps.

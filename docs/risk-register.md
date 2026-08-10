# EE Pulse risk register

Last updated: 2026-08-10  
Scale: likelihood/impact are Low (L), Medium (M), or High (H). Residual ratings assume the treatment is implemented.

| ID | Risk | L | I | Treatment / control | Owner / WP | Residual |
| --- | --- | --- | --- | --- | --- | --- |
| R-01 | ICMP needs privileges or is blocked and is not proof of application health. | H | H | Injectable transport, categorized errors, explicit AllowedNetworks, controlled targets, documented limitation; no discovery/scanning. | Agent + IT, WP-03/04/10 | M/H |
| R-02 | Windows Service account/install/recovery cannot be proven here. | M | H | Idempotent installer, Program Files/ProgramData split, queue preservation, disposable Windows reboot/recovery test. | Agent + IT, WP-10/11 | L/H |
| R-03 | A 72-hour queue exhausts disk or silently loses data. | M | H | Capacity formula, quota, WAL/transactions, explicit drop policy, dead letters, critical metric/log, disk-full tests. | Agent, WP-05/11 | L/H |
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
| R-15 | Unbounded queues/backpressure miss performance targets. | M | H | Bounded concurrency/queues/batches/rates; simulator and 60-minute load evidence. | All/QA, WP-04/05/07/11 | M/H |
| R-16 | Timezone/DST handling corrupts maintenance or reports. | M | M | UTC core; explicit timezone mapping; DST fixtures. | Backend/Web, WP-06/07/09 | L/M |
| R-17 | Host toolchain cannot reproduce net10.0 builds. | H | M | SDK 10.0.302 pin and verified official container; CI setup-dotnet 10.0.x. | Integration, WP-01 | M/L |
| R-18 | Docker access depends on engine state and local permission boundary. | M | M | Document prerequisite; use approved local Docker execution; Compose config/build/wait health gate. | User/Integration, WP-01/10 | L/M |
| R-19 | Remote workflow evidence is incomplete despite a configured `origin`. | M | M | Confirm branch/PR/CI/artifact/reviewer policy through UA-02 and obtain clean-clone CI evidence. | Repository owner, WP-11 | M/M |
| R-20 | Documentation or tooling retains the former `docs/spac` path. | L | L | `docs/spec` is authoritative; Lead corrected current README/control references and verification should reject future drift. | Integration, continuous | L/L |
| R-21 | Readiness omits a required dependency. | M | H | WP-02 now checks PostgreSQL connectivity and pending migrations; add VictoriaMetrics semantics in WP-05. | Backend, WP-05 | L/M |
| R-22 | A clean package audit is mistaken for a complete supply-chain/container scan. | M | H | Keep NuGet/npm audits in CI; add SBOM, image scan, and dedicated secret scan in WP-11; resolve critical/high findings. | QA/Security, WP-11 | L/H |
| R-23 | Parallel agents drift from frozen shared contracts. | M | H | WP-02 OpenAPI is checked in and Lead-frozen; generate clients from it, require compatibility tests, and keep migrations Backend-owned. | Lead/All, continuous | L/M |
| R-24 | Node-local CSV preview tokens disappear on restart or cannot be shared across API replicas. | M | M | Bounded 15-minute cache is explicit for MVP single-node; return clear invalid-token behavior and revisit durable/distributed storage before scale-out. | Backend, WP-10/11 | M/L |

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

## Checkpoint notes

- WP-01 and the WP-02 integration gates pass. PostgreSQL, VictoriaMetrics, and API containers are healthy; readiness verifies PostgreSQL schema/connectivity.
- NuGet and npm audits report no known vulnerable packages. `gitleaks` and `trivy` are not installed, so full dedicated secret and image scanning remains outstanding.
- The current Git worktree is on local `main`, one commit ahead of configured `origin/main`; first-wave changes remain uncommitted.
- The Lead-reviewed WP-02 inventory/OpenAPI v1 contract is stable for an explicitly authorized Agent C; later compatible changes remain governed by `docs/repository-ownership.md`.

# EE Pulse risk register

Last updated: 2026-08-10  
Scale: likelihood and impact are Low (L), Medium (M), or High (H). Residual ratings assume the listed treatment is implemented.

| ID | Risk | Likelihood | Impact | Treatment / control | Owner / target WP | Residual |
| --- | --- | --- | --- | --- | --- | --- |
| R-01 | ICMP may require OS privileges, be blocked by endpoint policy/firewalls, or be a poor proxy for application health | H | H | Injectable transport and categorized errors; document limitation; explicit AllowedNetworks; controlled test targets; no discovery/scanning | Agent + IT, WP-03/04/10 | M/H |
| R-02 | Windows Service install/recovery/account behavior cannot be proven on the development workstation | M | H | Idempotent installer, Program Files/ProgramData split, preserved queue, disposable Windows acceptance host, reboot/recovery test | Agent + IT, WP-10/11 | L/H |
| R-03 | 72-hour buffering can exhaust disk or silently discard results | M | H | Capacity formula/defaults, configurable quota, WAL/transactions, explicit oldest-drop policy only, dead letters, critical metric/log, disk-full tests | Agent, WP-05/11 | L/H |
| R-04 | Duplicate batches/runs produce duplicate metrics, transitions, incidents, or notifications | H | H | Unique batchId/runId, transactional dedupe ledger, idempotent acknowledgements, one-open-incident constraint, outbox/delivery dedupe | Backend, WP-05/06/08 | L/H |
| R-05 | Late buffered or clock-skewed events move current state backward | H | H | UTC, Agent clock-skew marking, per-Probe event watermark, historical-only late writes, deterministic out-of-order tests, NTP requirement | Backend/Agent, WP-05/06 | L/H |
| R-06 | Agent outage is mistaken for a mass device outage | M | H | Heartbeat expiry and freshness logic must force UNKNOWN without opening DOWN incidents; table-driven and E2E tests | Backend, WP-03/06/11 | L/H |
| R-07 | Uncontrolled metric labels cause VictoriaMetrics cardinality/cost growth | M | H | Fixed allowlisted IDs/type labels only; reject arbitrary error/hostname/tag labels; cardinality load checks and retention sizing | Backend/QA, WP-05/09/11 | L/M |
| R-08 | OIDC/AD mapping errors grant excessive privileges or production enables local login | M | H | Policy authorization in API and UI, explicit group mapping, production fail-closed startup, role-matrix tests, development-only seed | Backend/Web/IT, WP-02/03/07/11 | L/H |
| R-09 | Enrollment tokens or Agent credentials leak through source, logs, or diagnostics | M | H | Hashed one-time short-lived tokens, redaction tests, environment/secret store, rotation/revocation, secret scan | Backend/Agent, WP-03/11 | L/H |
| R-10 | Webhook delivery enables SSRF or leaks secrets in logs | M | H | HTTPS/host/network allowlist, DNS/IP revalidation, bounded redirects/body, redacted logs, fake receivers, no real test delivery | Backend/Security, WP-08/11 | L/H |
| R-11 | PostgreSQL and VictoriaMetrics partial failure loses or diverges data | M | H | Durable ingest staging/processing record, retryable adapter, readiness/metrics, explicit failure semantics, outage/restart tests | Backend, WP-05/11 | M/H |
| R-12 | Concurrent status processing races and opens multiple incidents | M | H | Per-Probe serialization or optimistic concurrency, single transaction for state/transition/incident/outbox, uniqueness constraints | Backend, WP-06 | L/H |
| R-13 | Deployment exposes database ports or uses weak/default secrets | M | H | Internal network, reverse-proxy-only public path, placeholder-only `.env.example`, fail-closed production validation, pinned images | Integration/IT, WP-01/10 | L/H |
| R-14 | Backup exists but cannot meet RPO/RTO or restore consistently across stores | M | H | User-approved policy, scripted backup, versioned procedure, isolated restore rehearsal and evidence | Operations, WP-09/10/11 | M/H |
| R-15 | Load targets are missed because queues/backpressure are unbounded | M | H | Bounded channels/queues/concurrency, batch limits, rate limiting, 500-target simulator and 60-minute test, observable backlog | All/QA, WP-04/05/07/11 | M/H |
| R-16 | Timezone/DST errors corrupt reports or maintenance evaluation | M | M | Store/process UTC; IANA/Windows timezone mapping at presentation/policy boundary; DST boundary fixtures | Backend/Web, WP-01/06/07/09 | L/M |
| R-17 | Toolchain mismatch prevents reproducible builds | H | M | Pin .NET SDK policy and npm lockfile; CI/container build; record host lacks .NET 10; provide bootstrap prerequisites | Integration, WP-01 | M/M |
| R-18 | Docker engine unavailable prevents Compose health validation | H | M | Keep Compose declarative and pinned; validate syntax where possible; rerun health gate when engine is started | User/Integration, WP-01 | L/M |
| R-19 | Absence of Git metadata prevents change isolation and repository-history secret checks | H | M | Do not assume a clean branch; inventory current files before edits; request repository workflow; initialize/connect Git only with approval | Repository owner, WP-00/11 | M/M |
| R-20 | Specification directory/file naming mismatch causes automation to miss governing documents | M | L | Treat `docs/spac` as authoritative; preserve it; document mismatch; optionally rename only with owner approval because references may exist externally | Integration, WP-00 | L/L |
| R-21 | Foundation readiness can report ready before database/time-series dependencies are wired | H | M | WP-01 readiness deliberately proves host health only; add PostgreSQL migration/connectivity readiness in WP-02 and VictoriaMetrics adapter readiness with explicit degraded/failure semantics in WP-05 | Backend, WP-02/05 | L/M |

## Assumptions

| ID | Temporary assumption | Revisit by |
| --- | --- | --- |
| A-01 | `docs/spac` is the intended `docs/spec` directory, and its six files are authoritative despite filename references to absent `.en.md` variants. | Before external repository integration |
| A-02 | PRD defaults govern until UA-01 is answered: 30 s interval, 2 s timeout, 3 attempts, failure threshold 3, recovery threshold 2. | WP-02 validation |
| A-03 | Target scale is 500 enabled ICMP probes, not 500 devices each with multiple simultaneously active probes. | WP-04/load model |
| A-04 | All persistence timestamps and contract timestamps use UTC; site timezone affects display, maintenance interpretation, and reports only. | WP-06/07 |
| A-05 | Development uses local placeholder identity and fake notification receivers; Production must fail closed without OIDC/TLS/secrets. | WP-03/08/10 |
| A-06 | PostgreSQL and VictoriaMetrics run as local Compose services with named volumes; only API/web entrypoints will eventually be host-facing. | WP-01/10 |
| A-07 | No network targets may be probed until an explicit allowlist exists; unit tests use fakes and loopback-safe fixtures. | WP-03/04 |
| A-08 | Permanent Device deletion remains an exceptional audited Administrator operation; normal UI workflows expose disable, not delete. | WP-02 |
| A-09 | Agent configuration is pull-based and becomes effective centrally only after acknowledgement; a last-known-good version is retained locally. | WP-03 |
| A-10 | Direct dependencies and images are exactly pinned; automated update tooling may propose patches later through review. | WP-01/11 |

## Checkpoint updates

- R-17 reduced: SDK 10.0.302 is pinned and the full solution builds/tests in its official container; the host still has only SDK 9.0.300.
- R-18 reduced: Docker engine started successfully and PostgreSQL, VictoriaMetrics, and API health checks passed with named volumes retained.
- R-09/R-13 evidence: current-tree placeholder review found no real credential; Git-history scanning remains unavailable because this workspace has no `.git` metadata.
- A high-severity transitive `Microsoft.OpenApi` finding surfaced during restore and was remediated by pinning patched 2.7.5. Final NuGet and npm vulnerability reports are clean.

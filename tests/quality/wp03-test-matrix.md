# WP-03 QA and security test matrix

Baseline: frozen contract commit `34718aa13727d8e84e5f56b61e854cbbabc5adab`.

This matrix is an evidence index, not a substitute for executable tests. `Verified` requires a passing named test or gate and an independent source review. `Blocked` is reserved for a genuine external dependency. `Pending` means implementation or evidence has not yet completed. Test data must use documentation-only IPv4 ranges and runtime-generated secret canaries; plaintext bootstrap or credential values must never be committed.

| ID | Owner | Required executable evidence | Status |
| --- | --- | --- | --- |
| ENR-01 | Backend | PostgreSQL/API test: issuance and valid enrollment return 201; token use, Agent, initial credential, and audit commit atomically; persisted values contain only digests. | Pending |
| ENR-02 | Backend + QA | API tests: malformed token is 400, unknown well-formed token is 401; no state change and sanitized Problem Details/log capture. | Pending |
| ENR-03 | Backend | PostgreSQL/API tests: expired, revoked, and reused known token each return terminal 410 and never create a second Agent. | Pending |
| ENR-04 | Backend | PostgreSQL concurrency test: ten simultaneous uses yield exactly one 201 and nine 410 responses. | Pending |
| ENR-05 | Backend | API tests: machine or normalized network mismatch returns 403 and leaves token unused. | Pending |
| ID-01 | Backend + QA | API tests: missing, malformed, and expired Agent credentials return sanitized 401 using `AgentCredential`. | Pending |
| ID-02 | Backend + QA | API test: valid credential with different route Agent ID returns 403 and performs no mutation. | Pending |
| ID-03 | Backend + Agent | Rotation/lost-response tests: one pending credential; first successful new use atomically promotes it and revokes old; secret is returned once only. | Agent Verified (54/54 Agent suite); Backend Pending |
| ID-04 | Backend + Agent | Revocation tests: every Agent endpoint returns 410 and Agent halts schedule/upload seams and clears active-memory credential. | Agent Verified (54/54 Agent suite); Backend Pending |
| ID-05 | Backend + Agent + QA | Production startup/readiness tests: missing trusted HTTPS/identity configuration fails closed; Development bootstrap cannot activate in Production. | Agent fail-closed tests Verified; Windows DPAPI/ACL runtime evidence Blocked to WP-10/11; Backend Pending |
| HB-01 | Backend | API test: first heartbeat changes Pending to Online using Server receive time. | Pending |
| HB-02 | Backend | API/PostgreSQL test: duplicate heartbeat ID returns the original response and creates no duplicate transition/audit. | Pending |
| HB-03 | Backend | Fake-clock worker/domain test: Online before and Offline at `max(60s, 3 * interval)` with Revoked precedence. | Verified: Backend unit suite 34/34, including 15/20/30-second boundaries and Revoked precedence |
| HB-04 | Backend | API test: more than five minutes of sent-time skew is accepted, flagged, and cannot control liveness. | Pending |
| HB-05 | Backend + Agent | Boundary tests for queue, health, SemVer, UTC-Z, and request size; unsupported version returns 426. | Agent request/retry behavior Verified; Backend Pending |
| CFG-01 | Backend + Agent | Conditional pull test: exact strong ETag sends/handles 304 with empty body and no local or central mutation. | Agent Verified; Backend Pending |
| CFG-02 | Backend | API/PostgreSQL test: published full snapshot has monotonic version and stable strong ETag and contains only enabled authorized ICMP probes. | Pending |
| CFG-03 | Backend + QA | Atomicity test: one out-of-policy target prevents publication/response; no partial configuration is persisted or returned. | Pending |
| CFG-04 | Agent | Restart/storage test: complete valid snapshot is durably applied, scheduler swaps atomically, prior LKG remains, and Applied acknowledgement is stable. | Verified: Agent 54/54, including prior/no-prior cancellation compensation |
| CFG-05 | Agent | Invalid schema, threshold, duplicate Probe, lower version, corrupt storage, and outside-ceiling tests leave LKG active and emit only an allowlisted rejection code. | Verified: Agent 54/54, including closed-schema remote-command rejection |
| CFG-06 | Backend | API/PostgreSQL test: duplicate acknowledgement ID returns the original response and stores one event. | Pending |
| CFG-07 | Backend | API test: future or stale Applied acknowledgement returns 409; central effective version neither jumps nor regresses. | Pending |
| CFG-08 | Backend + Agent | Rollback test: earlier content is copied into version N+1; snapshot is immutable and Agent rejects a lower version. | Agent monotonic behavior Verified; Backend Pending |
| NET-01 | Backend + Agent + QA | Table/property tests use `fixtures/wp03-network-boundaries.json` or equivalent documentation-range cases: canonicalization, containment, `/8` and `/32`, prohibited scope, duplicates, redundant overlaps, and 64/65 boundaries. | Backend 34/34 and Agent 54/54 equivalent parameterized coverage Verified |
| NET-02 | Agent + QA | Execution-seam test: outside-ceiling target is rejected both at apply and immediately before execution; fake transport invocation count remains zero. | Verified: Agent 54/54 |
| SEC-01 | Lead + Backend + QA | Generated OpenAPI gate: Web `Bearer` vs `AgentCredential` separation, anonymous enrollment bootstrap, correct statuses, Problem Details, closed schemas, strong ETag headers. | Pending |
| SEC-02 | Backend + Agent + QA | Runtime-generated canary test plus audit/database/log/Problem Details/OpenAPI scans prove no enrollment token or credential leakage. | Agent protected-storage/canary evidence Verified; Backend/OpenAPI Pending |
| COMP-01 | Lead + Backend + Agent | Shared-contract fixture serialization test proves exact schema-version/property compatibility across Backend and Agent. | Agent exact `Z`, non-`Z` rejection, closed-schema rejection, and Backend-format ETag fixture Verified (54/54); Backend wire fixture Pending |
| MIG-01 | Backend + QA | PostgreSQL tests: clean migration and upgrade from committed WP-02 migration both succeed; constraints/indexes hold; exactly one additive WP-03 migration; no pending model changes; rollback SQL generates. | Pending |

## Mandatory gates

Run from the repository root after Agent A and Agent B handoff:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\wp03-verify-contract.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-quality-security.ps1
docker compose config --quiet
git diff --check
```

The Lead runs the complete Release build/test, PostgreSQL, generated OpenAPI, Compose-health, dependency-audit, and regression gates. Absence of `gitleaks` or `trivy` remains the accepted WP-11 gap and must not be recorded as passing scanner evidence.

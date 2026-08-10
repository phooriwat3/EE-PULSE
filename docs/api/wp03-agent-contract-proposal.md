# WP-03 Agent contract-design proposal

Status: Approved and frozen — implementation agents remain gated on the documentation checkpoint commit  
Date: 2026-08-10 (Asia/Bangkok)  
Owner: Lead/Integration Agent  
Base artifact: `docs/api/openapi-v1.json` SHA-256 `69FFD677416A1952E3AF085EC5D0A2931EBD787AF9E339D38B02CEBDDEBE116D`

This is the approved additive OpenAPI v1 design contract for WP-03. It does not modify the generated, frozen WP-02 artifact. After the user confirms the documentation checkpoint commit, the Lead may start implementation Agents A, B, and D. The Lead will add shared DTOs, Backend will implement the endpoints, the API will regenerate `openapi-v1.json`, and Lead will accept the generated diff only after Backend, Agent, and QA contract tests pass.

## 1. Requirements and current foundation

The governing requirements are FR-03, FR-09, NFR-02 through NFR-05, the Agent and security sections of the Technical Specification, WP-03, the Agent/security delivery checklist, ADR-003, and ADR-006.

Existing reusable foundations are:

- a modular-monolith project graph with Lead-owned shared contracts;
- PostgreSQL EF Core persistence, append-only application audit events, UTC clock, Problem Details, correlation IDs, OpenAPI 3.1.1, and production fail-closed user authentication;
- `AgentGroup` and `Probe` metadata, including per-Probe configuration versions;
- a Windows-Service-capable Agent host with graceful shutdown;
- deterministic scheduling, per-Probe non-overlap, bounded concurrency, and an injectable transport seam;
- versioned Probe-result DTOs, although result ingestion remains WP-05.

WP-03 must add Agent identity and configuration boundaries without activating ICMP, remote commands, arbitrary payload execution, or unrestricted network access.

## 2. Resolved specification choices

| Topic | Specification tension or omission | Proposed resolution |
| --- | --- | --- |
| Credential type | FR-03 permits an Agent credential or certificate; the Technical Specification names a certificate thumbprint and calls mTLS a hardening target; WP-03 permits a documented local credential. | v1 uses an opaque, 256-bit random bearer credential over TLS. PostgreSQL stores only its SHA-256 digest with domain separation and identifiers. mTLS remains a compatible future hardening option and does not replace the v1 Agent identity model. |
| Production identity | WP-03 permits local credentials but requires Production to fail closed if secure identity is not configured. | Production requires HTTPS at the trusted proxy boundary and explicit `AgentIdentity` configuration. Missing/invalid configuration prevents Agent endpoints from being mapped or makes readiness fail. Development may use synthetic credentials created only through the enrollment flow; no static shared key is permitted. |
| Configuration effective time | The PRD says new configuration is effective only after acknowledgement, while an acknowledgement can only truthfully follow an atomic apply. | The Agent validates, durably applies, and swaps schedules atomically, then acknowledges. Central `LastAppliedConfigurationVersion` advances only on an `Applied` acknowledgement. Until then, central state reports the prior version as effective. |
| Rollback and monotonic versions | ADR-003 requires monotonically versioned configuration, while rollback implies older content. | Rollback publishes a new version containing a copy of a retained earlier snapshot and records `rollbackOfVersion`; versions never decrease. |
| Allowed networks | The Technical Specification places networks on Agent identity but does not prevent a compromised Server from later widening them. | A non-empty local network ceiling is persisted during enrollment. Server policy and delivered configuration may only be equal to or narrower than that ceiling. Remote expansion is rejected by the Agent and requires local administrative reprovisioning. |
| Heartbeat expiry | FR-03 gives a 15–30 second heartbeat interval but no expiry. | Default heartbeat interval is 20 seconds. ONLINE expires after `max(60 seconds, 3 × assigned interval)`, using Server receive time only. The detector runs at least every 15 seconds. |
| Clock skew | Probe-result rules mention skew, but heartbeat behavior is unspecified. | Absolute skew over 5 minutes is flagged but does not reject liveness; Server timestamps remain authoritative. The response instructs the Agent to check NTP. Result-event consequences remain WP-05/06. |
| Unsupported Agent version | Required behavior has no status mapping. | Return HTTP 426 with stable code `agent-version-unsupported`; it is permanent until the binary is upgraded. HTTP 409 is reserved for mutable state/configuration conflicts. |

## 3. Secure defaults

- Enrollment tokens: 32 random bytes, base64url encoded with a public UUID token identifier; default lifetime 15 minutes, configurable 1–1,440 minutes; one use only.
- Agent credentials: 32 random bytes plus public credential UUID; 90-day lifetime, rotate-after at 75 days, one pending rotation, no plaintext persistence on the Server.
- Hashing: SHA-256 over a domain label, public identifier, and random secret. High-entropy secrets make offline guessing infeasible; comparisons are constant-time.
- Heartbeat: 20-second assigned interval; ONLINE expiry at 60 seconds by default.
- Rate limits: enrollment 5 attempts/minute/source address and 20/hour/token identifier; authenticated Agent mutation endpoints 60/minute/Agent unless a lower endpoint limit is stated. `Retry-After` is mandatory on 429.
- Request limits: 32 KiB enrollment/heartbeat/acknowledgement bodies and 2 MiB full configuration responses.
- All timestamps are RFC 3339 UTC values with offset `Z`; inputs with non-zero offsets are rejected rather than silently converted.
- Machine names are 1–255 characters after trimming. Agent versions are SemVer 2 strings of at most 64 characters.
- Secrets are write-only contract fields, never examples, never query parameters, never audit before/after JSON, and never logged.

## 4. Proposed additive API surface

| Method and path | Caller | Success | Purpose |
| --- | --- | --- | --- |
| `POST /api/v1/agent-enrollment-tokens` | Administrator OIDC/development admin | 201 | Issue a one-time token bound to an Agent Group, optional machine name, expiry, and network ceiling. Secret returned once. |
| `DELETE /api/v1/agent-enrollment-tokens/{tokenId}` | Administrator | 204 | Revoke an unused enrollment token; idempotent. |
| `POST /api/v1/agents/enroll` | Anonymous bootstrap token | 201 | Atomically consume the token, create Agent identity and first credential, and return bootstrap settings. |
| `GET /api/v1/agents` | `agents.read` | 200 | Paged Agent ONLINE/OFFLINE/PENDING/REVOKED view without credential material. |
| `GET /api/v1/agents/{agentId}` | `agents.read` | 200 | Agent state, network-policy metadata, heartbeat/configuration state, and row version. |
| `PUT /api/v1/agent-groups/{agentGroupId}/allowed-networks` | Administrator | 200 | Set the group's non-empty Server-side network policy, publish a new snapshot, and audit the change. |
| `PUT /api/v1/agents/{agentId}/allowed-networks` | Administrator | 200 | Narrow or change Server policy with optimistic concurrency and audit. It cannot remotely expand the Agent's local ceiling. |
| `POST /api/v1/agent-groups/{agentGroupId}/configuration/rollback` | Administrator | 201 | Publish a new monotonic snapshot whose content copies a retained earlier version. |
| `POST /api/v1/agents/{agentId}/revoke` | Administrator | 200 | Revoke Agent and every credential transactionally; republish group configuration as needed. |
| `POST /api/v1/agents/{agentId}/heartbeat` | Matching Agent credential | 200 | Record Server-received liveness and Agent self-health. |
| `GET /api/v1/agents/{agentId}/configuration` | Matching Agent credential | 200/304 | Pull a complete immutable configuration snapshot with ETag support. |
| `POST /api/v1/agents/{agentId}/configuration/acknowledgements` | Matching Agent credential | 200 | Idempotently report applied or rejected configuration. |
| `POST /api/v1/agents/{agentId}/credentials/rotate` | Matching active Agent credential | 201 | Issue one pending replacement credential; old credential remains active until first successful use of the replacement. |

No endpoint accepts shell commands, executable content, arbitrary URLs, scripts, or a list of targets not derived from stored enabled Device/Probe metadata.

## 5. Contract schemas

All objects set `additionalProperties: false`. `schemaVersion` is required and must equal integer `1`. Identifiers are non-empty UUID strings. `ApiInteger` behavior remains compatible with the existing .NET OpenAPI representation for `int64` values.

### Enrollment token administration

```text
CreateAgentEnrollmentTokenRequest
  schemaVersion: integer const 1
  agentGroupId: uuid
  label: string, 1..200
  expectedMachineName: string|null, 1..255
  allowedNetworks: unique string[1..64]
  expiresInSeconds: integer, 60..86400, default 900

CreateAgentEnrollmentTokenResponse
  schemaVersion: integer const 1
  tokenId: uuid
  enrollmentToken: string, writeOnly, minLength 48, maxLength 256
  agentGroupId: uuid
  allowedNetworks: string[1..64]
  expiresAt: UTC date-time
  createdAt: UTC date-time
```

The enrollment token wire form is opaque. OpenAPI supplies no value or example.

### Enrollment

```text
AgentEnrollmentRequest
  schemaVersion: integer const 1
  enrollmentToken: string, writeOnly, minLength 48, maxLength 256
  clientInstanceId: uuid
  machineName: string, 1..255
  agentVersion: string, SemVer, maxLength 64
  localAllowedNetworks: unique string[1..64]
  sentAt: UTC date-time

AgentEnrollmentResponse
  schemaVersion: integer const 1
  agentId: uuid
  agentGroupId: uuid
  credentialId: uuid
  agentCredential: string, writeOnly, minLength 48, maxLength 256
  credentialExpiresAt: UTC date-time
  rotateAfter: UTC date-time
  serverTime: UTC date-time
  heartbeatIntervalSeconds: integer, 15..30
  heartbeatExpiresAfterSeconds: integer, minimum 45
  desiredConfigurationVersion: int64
  configurationUrl: relative URI
```

`localAllowedNetworks` must normalize to the exact or narrower set granted by the token. An enrollment mismatch is 403; the token is not consumed.

### Agent view

```text
AgentResponse
  schemaVersion: integer const 1
  id: uuid
  agentGroupId: uuid
  name: string
  machineName: string
  agentVersion: string
  status: Pending|Online|Offline|Revoked
  selfHealth: Healthy|Degraded|Unhealthy|Unknown
  queueDepth: int64
  allowedNetworks: string[]
  lastHeartbeatAt: UTC date-time|null
  lastReportedAt: UTC date-time|null
  desiredConfigurationVersion: int64
  lastAppliedConfigurationVersion: int64
  lastConfigurationAcknowledgedAt: UTC date-time|null
  clockSkewSuspected: boolean
  credentialExpiresAt: UTC date-time|null
  createdAt: UTC date-time
  revokedAt: UTC date-time|null
  rowVersion: int64

PagedAgentResponse
  items: AgentResponse[]
  page, pageSize, totalCount: existing pagination contract
```

The list supports `agentGroupId`, `status`, `selfHealth`, `search`, `page`, and `pageSize` filters with the existing default/max pagination rules.

### Heartbeat

```text
AgentHeartbeatRequest
  schemaVersion: integer const 1
  heartbeatId: uuid
  agentVersion: string, SemVer, maxLength 64
  machineName: string, 1..255
  currentConfigurationVersion: int64, minimum 0
  queueDepth: int64, minimum 0
  healthState: Healthy|Degraded|Unhealthy
  sentAt: UTC date-time

AgentHeartbeatResponse
  schemaVersion: integer const 1
  heartbeatId: uuid
  agentId: uuid
  receivedAt: UTC date-time
  serverTime: UTC date-time
  nextHeartbeatSeconds: integer, 15..30
  desiredConfigurationVersion: int64
  configurationChanged: boolean
  credentialRotationRequired: boolean
  clockSkewSuspected: boolean
  warningCode: string|null
```

`heartbeatId` is idempotent per Agent. Replays return the original accepted response without duplicating transitions or audit data. Liveness always uses `receivedAt`, never `sentAt`.

### Configuration pull

```text
AgentConfigurationResponse
  schemaVersion: integer const 1
  agentId: uuid
  agentGroupId: uuid
  configurationVersion: int64, minimum 1
  generatedAt: UTC date-time
  rollbackOfVersion: int64|null
  allowedNetworks: unique string[1..64]
  probes: AgentProbeConfiguration[]

AgentProbeConfiguration
  probeId: uuid
  deviceId: uuid
  probeConfigVersion: int64
  type: "icmp"
  targetAddress: IPv4 string
  intervalSeconds: integer 5..3600
  timeoutMilliseconds: integer 100..60000
  attemptCount: integer 1..10
  warningRttMilliseconds: integer|null, minimum 1
  criticalRttMilliseconds: integer|null, minimum 1
  failureThreshold: integer 1..100
  recoveryThreshold: integer 1..100
```

Only enabled Probes on enabled Devices in the Agent's enabled group appear. The Server must prove every `targetAddress` belongs to both the persisted Agent policy and its Agent Group policy before returning any configuration. One invalid target fails the entire response with 409; partial configurations are forbidden.

The response has a strong ETag derived from schema version, configuration version, and canonical payload SHA-256. Exact `If-None-Match` returns 304 with no body. ETags contain no secret.

### Configuration acknowledgement

```text
AgentConfigurationAcknowledgementRequest
  schemaVersion: integer const 1
  acknowledgementId: uuid
  configurationVersion: int64, minimum 1
  status: Applied|Rejected
  appliedAt: UTC date-time|null
  errorCode: string|null, stable allowlisted code, maxLength 100
  sentAt: UTC date-time

AgentConfigurationAcknowledgementResponse
  schemaVersion: integer const 1
  acknowledgementId: uuid
  agentId: uuid
  configurationVersion: int64
  acceptedAt: UTC date-time
  centralEffectiveConfigurationVersion: int64
  desiredConfigurationVersion: int64
```

Free-text exception details are not accepted. `Applied` requires `appliedAt`; `Rejected` requires an allowlisted `errorCode`. Acknowledgement IDs are idempotent. A version newer than desired or an `Applied` acknowledgement older than the current effective version returns 409.

### Credential rotation and revocation

```text
RotateAgentCredentialRequest
  schemaVersion: integer const 1

RotateAgentCredentialResponse
  schemaVersion: integer const 1
  credentialId: uuid
  agentCredential: string, writeOnly
  expiresAt: UTC date-time
  rotateAfter: UTC date-time

RevokeAgentRequest
  schemaVersion: integer const 1
  reasonCode: Compromised|Decommissioned|Replaced|Administrative
  rowVersion: int64

UpdateAgentAllowedNetworksRequest
  schemaVersion: integer const 1
  allowedNetworks: unique string[1..64]
  rowVersion: int64

UpdateAgentGroupAllowedNetworksRequest
  schemaVersion: integer const 1
  allowedNetworks: unique string[1..64]
  rowVersion: int64

AgentNetworkPolicyResponse
  schemaVersion: integer const 1
  ownerId: uuid
  allowedNetworks: string[1..64]
  configurationVersion: int64
  rowVersion: int64

RollbackAgentConfigurationRequest
  schemaVersion: integer const 1
  sourceConfigurationVersion: int64, minimum 1
  rowVersion: int64

AgentConfigurationPublicationResponse
  schemaVersion: integer const 1
  agentGroupId: uuid
  configurationVersion: int64
  rollbackOfVersion: int64|null
  generatedAt: UTC date-time
```

Only one pending replacement credential exists. Creating another revokes the prior pending credential. The active credential remains valid until the pending credential first authenticates, at which point promotion and old-credential revocation occur transactionally. A pending credential expires after 24 hours. Revocation invalidates all credentials immediately.

## 6. Authentication and authorization

- User-management operations use the existing Web Bearer/OIDC scheme and policies: `agents.read` for Viewer, Operator, Engineer, Administrator, and Auditor; `agents.admin` for Administrator only.
- Enrollment accepts no user identity. The one-time token is the sole bootstrap factor and is subject to strict rate/body limits. Token material is parsed only from the JSON body and the body is excluded from request logging.
- Agent operations use a distinct OpenAPI HTTP bearer security scheme named `AgentCredential`, bearer format `EE-Pulse-Agent-v1`. The credential's Agent ID must match `{agentId}`; mismatch is 403.
- User Bearer tokens cannot authenticate Agent endpoints, and Agent credentials cannot authenticate user/inventory endpoints.
- Development may exercise the real enrollment flow against local PostgreSQL. Static shared Agent keys and header-only Agent impersonation are forbidden.
- Production Agent endpoints fail closed unless trusted HTTPS-forwarding policy and Agent identity settings are valid. Direct cleartext production binding is not accepted.

## 7. Domain invariants

1. Enrollment token ID and secret are non-empty; only a digest is persisted.
2. A token is usable only while unrevoked, unused, unexpired, and bound to an enabled Agent Group.
3. Token consumption, Agent creation, first credential creation, audit event, and `UsedAt`/`UsedByAgentId` update commit in one PostgreSQL transaction.
4. Concurrent token users serialize on the token row; exactly one can commit. Every loser receives the same terminal 410 response.
5. `clientInstanceId` is unique for non-revoked Agents, preventing accidental duplicate enrollment of one installation.
6. Credential secrets are returned once, stored only as digests centrally, compared in constant time, and never audited or logged.
7. Revoked Agents cannot heartbeat, pull configuration, acknowledge, rotate credentials, or later ingest results.
8. Agent status precedence is `Revoked`, then `Pending` before first heartbeat, then `Online` or `Offline` from Server receive time.
9. Heartbeats cannot move `LastHeartbeatAt` backward. Duplicate `heartbeatId` values are idempotent.
10. Configuration versions are positive and monotonic per Agent Group. Published snapshots are immutable.
11. Central effective version advances only on a valid `Applied` acknowledgement from the matching Agent.
12. An Agent never applies a configuration with an unsupported schema, duplicate Probe ID, invalid threshold, target outside both allowlists, or a version lower than its active version.
13. Atomic apply either replaces the complete schedule and local active-version pointer or leaves the prior last-known-good configuration untouched.
14. Rollback is a new monotonic version; an older version is never reactivated by decrementing a version counter.
15. Empty AllowedNetworks means no probing. `0.0.0.0/0`, unspecified, multicast, and broadcast destinations are prohibited. Automated tests use documentation ranges/fakes only.

## 8. Persistence and migration proposal

Backend owns one additive WP-03 migration created after the committed `InitialInventory` migration. It must not edit the WP-02 migration.

### New/extended data

- Extend `agent_groups` with `configuration_version bigint NOT NULL DEFAULT 0`; version zero means no published snapshot. Existing optimistic row-version behavior remains.
- Add `agent_group_allowed_networks(agent_group_id, network cidr)` with a unique normalized key.
- Add `agents`: technical-specification fields plus `client_instance_id`, self-health, queue depth, Server-received heartbeat fields, desired/effective configuration versions, credential-expiry metadata, revocation fields, clock-skew flag, and `row_version`.
- Add `agent_allowed_networks(agent_id, network cidr)` as the Server-side copy of the enrollment ceiling.
- Add `agent_enrollment_tokens`: token ID/digest, group, label, optional machine binding, expiry, use/revocation fields, creator, timestamps, and row version. Never store plaintext.
- Add `agent_credentials`: credential ID/digest, Agent ID, Active/Pending/Revoked state, expiry/rotation timestamps, first-use and revocation timestamps. Never store plaintext.
- Add immutable `agent_configuration_snapshots`: Agent Group, version, canonical JSON payload, payload digest, generated time, rollback source, and unique `(agent_group_id, version)`.
- Add append-only `agent_configuration_acknowledgements`: acknowledgement ID, Agent, version, status, applied/sent/received timestamps, stable error code; unique `(agent_id, acknowledgement_id)`.
- Add `agent_heartbeat_receipts` only as a bounded idempotency window keyed by `(agent_id, heartbeat_id)`; retain 24 hours. Long-term heartbeat history belongs in metrics/status transitions, not an unbounded PostgreSQL heartbeat table.
- Reuse append-only `audit_events` for token issued/revoked/consumed, Agent enrolled/revoked, AllowedNetworks changed, credential rotation requested/promoted, configuration published/rolled back, and acknowledgement state changes. Audit payloads contain IDs and metadata only.

### Constraints and indexes

- Unique active `client_instance_id` where Agent is not revoked.
- Unique normalized CIDR rows per group and Agent.
- Unique token and credential IDs; fixed-length digest checks.
- One Active and one Pending credential per Agent via partial unique indexes.
- Unique snapshot version per group and acknowledgement ID per Agent.
- Index `(status, last_heartbeat_at)` for offline detection and `(agent_group_id, status)` for views.
- Foreign keys use restrictive deletion; Agents, tokens, credentials, snapshots, acknowledgements, and audits are retained rather than cascaded away.

Migration tests must prove empty-database migration, upgrade from the committed WP-02 schema, model/snapshot synchronization, constraints, and rollback script generation. Production application startup continues not to auto-migrate.

## 9. Configuration publication and Agent behavior

1. A relevant Agent Group, Device, Probe, or allowed-network mutation validates the complete affected group configuration. Setting the first non-empty group AllowedNetworks policy publishes version 1.
2. Validation failure aborts the mutation; no partial snapshot is published.
3. Success increments the group version and stores one immutable canonical snapshot in the same transaction as the metadata/audit change.
4. Agent polls with its current ETag. A 304 retains the active configuration.
5. For 200, Agent validates schema/version, all Probe invariants, canonical payload digest, and both Server and local AllowedNetworks.
6. Agent writes the candidate and active pointer in one local SQLite transaction, swaps the scheduler atomically, and retains the prior snapshot as LKG.
7. Agent posts `Applied`; transport failure retries the same acknowledgement ID. `Rejected` leaves LKG active and reports only a stable error code.
8. Server updates central effective version only for `Applied`.
9. Rollback publishes old content as version N+1. Agent applies it through the same path.
10. HTTP 410 for Agent revocation causes immediate scheduler halt, upload halt, credential removal from active memory, and a local security log without secret data. Persisted LKG remains encrypted but inactive for diagnostics.

## 10. CIDR/address policy

- IPv4 only for MVP. Accept canonical IPv4 addresses and CIDR prefixes `/8` through `/32`; normalize addresses to `/32` and mask host bits in CIDRs.
- Reject duplicates, overlaps that add no scope, `0.0.0.0/0`, `0.0.0.0/8`, multicast `224.0.0.0/4`, limited broadcast, and non-unicast target addresses.
- Loopback and link-local ranges are Development-only unless a future explicit production policy is approved.
- Maximum 64 allowed networks per group/Agent and maximum 2,000 Probes in one configuration payload.
- Server enforcement occurs on policy save, Probe assignment/change, snapshot publication, and every configuration response.
- Agent enforcement occurs before durable apply and again immediately before each Probe execution. DNS is not used for authorization; the delivered target is the normalized IP address.
- An empty or invalid local ceiling is fail-closed: heartbeat may continue, but scheduling cannot start.

UA-03 must provide the real approved CIDRs and controlled targets before any real network test. No design-time or automated test sends ICMP.

## 11. Error contract

All non-304 errors use RFC 9457 Problem Details with `type`, `title`, `status`, `detail`, `instance`, `code`, `retryable`, `correlationId`, and optional `retryAfterSeconds`. Details are sanitized and contain no token, credential, raw Authorization value, or submitted request body.

| HTTP | Stable code | Meaning | Agent action |
| --- | --- | --- | --- |
| 400 | `request-invalid`, `schema-unsupported`, `timestamp-not-utc` | Malformed or unsupported contract | Permanent until local input/software is corrected. |
| 401 | `agent-authentication-required`, `enrollment-token-invalid` | Missing or invalid bootstrap/Agent credential | Do not retry rapidly; require enrollment/credential recovery. |
| 403 | `agent-identity-mismatch`, `network-policy-mismatch` | Valid identity is not permitted for route or scope | Permanent; halt affected operation and alert locally. |
| 404 | `agent-not-found`, `configuration-not-found` | Authenticated caller references absent resource | Permanent unless central administration changes state. |
| 409 | `configuration-conflict`, `acknowledgement-conflict`, `agent-group-disabled` | Valid request conflicts with current mutable state | Refresh desired state; retry only when response says so. |
| 410 | `enrollment-token-unavailable`, `agent-revoked`, `configuration-retired` | Token used/expired/revoked, Agent revoked, or version retired | Permanent. Revoked Agent halts all work. |
| 426 | `agent-version-unsupported` | Agent binary falls outside supported SemVer range | Permanent until upgrade; heartbeat may be recorded only when policy permits. |
| 429 | `rate-limit-exceeded` | Endpoint limit exceeded | Retry after `Retry-After` with jitter. |
| 500 | `server-error` | Unexpected failure | Retry with bounded exponential backoff. |
| 503 | `dependency-unavailable` | PostgreSQL or required identity dependency unavailable | Retry with bounded exponential backoff. |

Enrollment deliberately distinguishes a cryptographically unrecognized token (401) from a known terminal token (410). Token identifiers have 128 bits of entropy, requests are rate-limited, and both responses remain generic.

## 12. Threat analysis

| Threat | Control and required evidence |
| --- | --- |
| Database disclosure exposes bootstrap or Agent secrets | Only domain-separated digests persist; secret-return fields are write-only and returned once; database assertions and log/audit scans. |
| Token replay or concurrent consumption creates multiple Agents | Row lock/conditional update plus one transaction and concurrency integration test; exactly one 201. |
| Stolen enrollment token enrolls another machine | Short expiry, optional machine binding, local/server allowlist equality, rate limits, transactional audit, and revocation. |
| Stolen Agent credential enables impersonation | TLS, per-Agent credentials, route-ID binding, expiry/rotation, immediate revocation, hashed storage, no shared key. |
| Lost rotation response locks Agent out | Old credential remains active until first use of pending replacement; pending credential expires; one pending record only. |
| Compromised Server expands probe scope | Agent's locally persisted network ceiling cannot be remotely widened; execution-time IP containment check. |
| Malicious configuration performs remote execution | Closed schema with ICMP-only Probe data; no command/script/URL fields; unknown properties rejected. |
| Revoked Agent continues probing | 410 causes immediate local halt; Server rejects all Agent operations; central offline/revoked processing; tests. Network-isolated Agents cannot learn revocation immediately, documented as residual risk. |
| Clock spoof keeps Agent online or corrupts state | ONLINE uses Server receive time; skew is flagged; NTP remains UA-04; result watermark is WP-05/06. |
| Logs/OpenAPI leak credentials | Request-body/header redaction, no secret examples/defaults, structured log capture tests, Problem Details sanitization. |
| Heartbeat flood or configuration amplification | Per-source/per-Agent rate limits, small request caps, conditional GET/304, configuration size and Probe-count caps. |
| Partial configuration disables safety controls | Full-snapshot validation and atomic apply; no partial response; LKG retained on rejection. |

## 13. Contract test matrix

| ID | Layer | Scenario | Expected evidence |
| --- | --- | --- | --- |
| ENR-01 | API/PostgreSQL | Valid token enrollment | 201; Agent/credential/token-use/audit commit together; no plaintext persisted. |
| ENR-02 | API | Malformed or unknown token | 400/401 Problem Details; no state change; no secret in logs. |
| ENR-03 | API/PostgreSQL | Expired, revoked, or reused token | 410; no second Agent. |
| ENR-04 | PostgreSQL concurrency | 10 simultaneous uses of one token | Exactly one 201 and nine terminal 410 responses. |
| ENR-05 | API | Machine/network binding mismatch | 403; token remains unused. |
| ID-01 | API | Missing, malformed, expired credential | 401 with sanitized Problem Details. |
| ID-02 | API | Credential Agent ID differs from route | 403; no mutation. |
| ID-03 | API/Agent | Rotation then first use | Pending secret returned once; new credential promotes and old credential fails thereafter. |
| ID-04 | API/Agent | Revocation | All Agent endpoints return 410; Agent halts schedules/uploads. |
| ID-05 | Configuration | Production identity/TLS settings absent | Startup/readiness fails closed; Development-only path cannot activate. |
| HB-01 | API | First heartbeat | Agent becomes Online using Server receive time. |
| HB-02 | API | Duplicate heartbeat ID | Idempotent response; no duplicate transition. |
| HB-03 | Worker/fake clock | Heartbeat age crosses boundary | Online before and Offline at `max(60s, 3×interval)`; revoked precedence. |
| HB-04 | API | Sent time skew over five minutes | Heartbeat accepted, skew flag/warning set, Server time authoritative. |
| HB-05 | API | Queue/health/version validation | Boundaries accepted; invalid values 400; unsupported version 426. |
| CFG-01 | API | Matching ETag | 304, empty body, no configuration mutation. |
| CFG-02 | API | New full configuration | 200; monotonic version, stable ETag, only enabled authorized ICMP Probes. |
| CFG-03 | Backend | One out-of-policy target | Publication/response fails atomically; never returns a partial list. |
| CFG-04 | Agent | Valid snapshot | Durable atomic apply, scheduler swap, Applied acknowledgement, prior LKG retained. |
| CFG-05 | Agent | Invalid schema/threshold/duplicate/out-of-range target | No schedule change; LKG remains active; sanitized Rejected acknowledgement. |
| CFG-06 | API | Duplicate acknowledgement ID | Idempotent response and one stored event. |
| CFG-07 | API | Stale/future acknowledgement | 409; central effective version does not regress or jump. |
| CFG-08 | Backend/Agent | Rollback | New N+1 snapshot copies earlier content; Agent never accepts a lower version. |
| NET-01 | Unit/property | CIDR normalization and containment boundaries | Exact/masked forms accepted; prohibited/overlapping/unrestricted values rejected. |
| NET-02 | Agent execution seam | Target outside local ceiling after apply attempt | Apply rejected and transport never invoked. |
| SEC-01 | OpenAPI | Security schemes and status responses | Admin operations use Bearer; Agent operations use AgentCredential; enrollment bootstrap has no Bearer requirement; all declared errors use Problem Details. |
| SEC-02 | Logs/audit/OpenAPI | Secret canaries | No token/credential appears in logs, audit JSON, Problem Details, snapshots, or OpenAPI examples. |
| COMP-01 | Shared contract | Backend and Agent serialize the same DTO fixtures | Exact schema/version/property compatibility. |
| MIG-01 | PostgreSQL | WP-02 upgrade and clean migration | Both succeed; constraints/indexes enforced; no pending model changes. |

## 14. Ownership split and exact acceptance criteria

### Lead/Integration

- Owns and freezes shared DTOs, schema version, authentication scheme names, error codes, ADRs, and regenerated OpenAPI.
- Reviews the Backend migration and Agent local-storage/security boundary without editing concurrently owned files.
- Runs the full solution, Agent, OpenAPI, Compose, secret, and quality gates before WP-03 integration approval.

### Agent A — Backend/PostgreSQL

Owns `EePulse.Domain`, `EePulse.Application`, `EePulse.Infrastructure`, `EePulse.Api`, Backend tests, and the single WP-03 migration. It must:

1. implement every administrative/bootstrap/Agent endpoint and policy above;
2. enforce transactional token consumption, hashed token/credential persistence, rotation, revocation, rate/body limits, sanitized Problem Details, and audit events;
3. publish immutable, monotonic full configuration snapshots and enforce both Server allowlists;
4. implement Server-time heartbeat state and offline detection without starting WP-06 Device-state behavior;
5. preserve all WP-02 behavior and migration history;
6. pass ENR-01..05, ID-01..05 Server portions, HB-01..05, CFG-01..03/06..08 Server portions, NET-01 Server portions, SEC-01..02, and MIG-01;
7. report exact dependency additions before changing central package pins.

Acceptance: Release build has zero warnings/errors; Backend unit/integration tests pass against PostgreSQL; concurrent enrollment proves exactly one winner; EF reports no pending model changes; generated OpenAPI matches the Lead-frozen proposal with no secret examples.

### Agent B — Probe Agent

Owns `src/agent/**` and `tests/EePulse.Agent.Tests/**`. It must:

1. implement an HTTP client for enrollment, credential auth/rotation, heartbeat, conditional configuration pull, and acknowledgement using Lead-owned DTOs;
2. persist identity and local AllowedNetworks under a ProgramData-compatible abstraction using Windows DPAPI LocalMachine protection plus an ACL restricted to the service identity and Administrators; non-Windows tests use an injected fake protector, never plaintext production fallback;
3. serialize local state changes and atomically replace the credential file; never log request bodies, Authorization, tokens, or credentials;
4. implement durable atomic configuration/LKG storage and a scheduler handoff seam, without ICMP or WP-05 queue implementation;
5. enforce local CIDR policy both during apply and immediately before transport execution;
6. halt configuration scheduling/upload seams on 410 revocation and fail Production startup when identity/network protection is not configured;
7. pass ID-03..05 Agent portions, HB request/retry behavior, CFG-01/04/05/08 Agent portions, NET-01/02 Agent portions, SEC-02, and COMP-01.

Acceptance: Release build has zero warnings/errors; deterministic tests cover restart, corrupt identity/config, lost rotation response, 304, rejection/LKG, atomic apply failure, revocation, cancellation, and prohibited targets; tests perform no real network probes and contain no credential values outside generated in-memory canaries.

### Agent D — QA/Security

Owns quality/security fixtures, matrices, and verification scripts only. It must:

1. create the executable WP-03 matrix mapped to every ID above;
2. independently review authentication separation, constant-time digest comparison, transaction/concurrency behavior, log/audit/OpenAPI redaction, TLS/Production fail-closed settings, CIDR parsing, response/status semantics, and rate/body limits;
3. add safe contract fixtures using documentation-only IP ranges and runtime-generated secret canaries;
4. verify migration from the committed WP-02 baseline, empty migration, Compose health, and no WP-02 regression;
5. treat unavailable `gitleaks`/`trivy` as the already accepted WP-11 gap, not as passed scanner evidence.

Acceptance: all mandatory matrix cases are Verified with commands/evidence or explicitly Blocked by a genuine external requirement; no High/Critical finding remains; quality/security, secret-pattern, dependency, and `git diff --check` gates pass.

## 15. Freeze and implementation boundary

The user approved and froze this additive v1 contract and every secure default recorded above on 2026-08-10. Implementation remains paused until the user confirms that the documentation checkpoint commit is complete. Agent C remains stopped. Any change to credential type, lifetime, heartbeat/expiry, network-ceiling behavior, status/error mapping, endpoint shape, configuration immutability, rollback semantics, or persistence ownership returns to Lead review. No real credential, real network probe, external system change, commit, push, or deployment is authorized by this approval.

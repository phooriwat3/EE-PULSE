# WP-07 dashboard and device experience — contract and policy design

Status: Phase 2A timezone-preference implementation plus Phase 2B contract closure (2026-09-09)
Scope: Phase 2A implements the timezone-preference contract, persistence foundation, API slice, authorization, and generated OpenAPI. Phase 2B closes only the frozen 2B1 contract for future dashboard summary and device-status reads. No Phase 2B1 runtime endpoint exists yet; OpenAPI remains unchanged.

## Phase 2B1 frozen runtime contract (not implemented)

The only future 2B1 routes are `GET /api/v1/dashboard/summary` and `GET /api/v1/devices/{id}/status`. Metrics, a status-enriched device list, history/timeline, incident reads/actions/comments, audit reads, SignalR, frontend behavior, WP-08 notification fan-out, and WP-09 reporting/retention are excluded. In particular, `includeStatus` is not added to the existing device-list response.

Both routes initially require the global authenticated `dashboard.read` scope. There is no site-grant model: optional `siteId` is a data filter only, never an authorization boundary. Reads use the normal validated caller correlation convention and never persist a correlation ID.

`DashboardSummaryResponse` has no `generatedAt` and neither route adds a request-time timestamp. `StatusEnrichedDeviceResponse` also has no request-time timestamp. Authoritative monitoring instants are UTC `Z`; client receipt time is presentation-only and never an authoritative monitoring timestamp.

Filters are optional. `siteId` is parsed as UUID-D and rendered lowercase. Non-null `area`, `deviceType`, `criticality`, and `tag` are trimmed, Unicode Form-C normalized, non-empty after normalization, and limited to 128 UTF-16 code units; exact enum tokens are `Unknown`, `Up`, `Degraded`, `Down`, `Recovering`, `Maintenance`, and `Disabled`. The response `appliedFilter` contains the single validated normalized filter representation (including explicit nulls), never the caller's unnormalized input. No other Unicode normalization is performed.

Visible status is contract-owned: disabled Device or Probe wins (`!Device.Enabled || !Probe.Enabled => Disabled`), then active matching maintenance (`Maintenance`), then persisted projection `VisibleStatus`. A missing projection is `Unknown`, state version `0`, and has nullable freshness, receipt, Agent, and incident fields; it must never become `Up`. UNKNOWN expiry may coexist with an active availability incident. An offline Agent is persisted `Agent.Status == Offline`; an active incident is `Open` or `Acknowledged`. `openIncidentId` is projection-authoritative; an incident row is a consistency check.

Summary always emits all seven status-count values in the exact order above. `recentlyDown` contains final visible `Down` rows with the active incident referenced by the projection; `sinceAt` is `availability_incidents.opened_at`, maximum 20, ordered `sinceAt DESC, probeId DESC`, with duplicate `probeId` rejected. `offlineAgents` contains persisted offline Agents referenced by a filtered projection `watermark_agent_id`, maximum 20, ordered `lastHeartbeatAt ASC NULLS FIRST, agentId ASC`, with duplicate `agentId` rejected. `openIncidents` contains Open/Acknowledged rows, maximum 20, ordered `openedAt DESC, incidentId DESC`, with duplicate `incidentId` rejected. UUID rendering and every tie-break use lowercase UUID-D and ordinal comparison.

The reusable contract canonicalizer validates the complete response before producing any bytes or ETag, then writes exact UTF-8 bytes for 200 responses. It writes object names in ascending ordinal order, writes every declared property including null, uses the collection orders above, exact PascalCase enum tokens, lowercase UUID-D, minimal base-10 integer JSON, System.Text.Json's fixed default escaping, and UTC `yyyy-MM-dd'T'HH:mm:ss.fffffffZ`. PostgreSQL timestamps are normalized to microsecond precision before DTO construction. Probes are sorted by lowercase `probeId` ordinal and duplicate IDs fail; tags are sorted ordinal and exact duplicates fail, while case-distinct tags remain distinct. Null collections/elements and invalid response values fail closed. The semantic summary lists and status counts are never re-sorted: they must already satisfy their frozen membership, cap, and order. The canonicalization marker is `wp07-2b1`; the strong identity-encoded response ETag is `"wp07-2b1-sha256-<43-character-unpadded-base64url-SHA256>"`, calculated from those exact bytes. Content type is `application/json; charset=utf-8`; no alternate representation or compression is permitted.

Future GET conditional behavior accepts `If-None-Match` entity-tag lists across multiple header lines. Wildcard is valid only as the sole member. Supplied weak and strong tags use weak comparison for GET. Malformed input is `400` code `invalid-if-none-match`; a match is bodyless `304` with the current strong ETag, `Cache-Control: private, max-age=0, must-revalidate`, and `X-Correlation-ID`; a non-match is canonical `200`. `If-Match` is unsupported for these reads.

## 1. Boundaries and compatibility

The existing v1 surface remains additive and compatibility-owned. Phase 2A adds the verified `GET` and `PUT /api/v1/users/me/timezone-preference` operations to the generated `docs/api/openapi-v1.json`; no other WP-07 route is claimed as implemented.

All UUID identifiers use canonical lowercase UUID-D syntax: `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`. Required identifiers are non-empty canonical UUID-D values; optional identifiers may be null but are canonical when present. Timestamps are UTC `Z` instants; integral versions are JSON numbers when safe for the target client and otherwise must preserve the existing string-or-number bigint compatibility convention. A response must not invent current status, metrics, or live data when its backing source is unavailable.

## 2. Role matrix

| Capability | Viewer | Operator | Engineer | Administrator | Auditor |
| --- | --- | --- | --- | --- | --- |
| Dashboard, device detail/history/metrics, Agent and maintenance reads | Yes | Yes | Yes | Yes | Yes |
| Device/probe/maintenance/inventory mutation | No | No | Yes | Yes | No |
| Incident read | Yes | Yes | Yes | Yes | Yes |
| Acknowledge and comment on an incident | No | Yes | No | Yes | No |
| Manually resolve an incident subject to section 6 | No | Yes | No | Yes | No |
| Audit-log read | No | No | No | Yes | Yes |
| Agent administration | No | No | No | Yes | No |

The API enforces every row above; UI affordances are a convenience only. The new policies are `dashboard.read`, `incidents.read`, `incidents.operate`, and `audit.read`. `incidents.operate` requires Operator or Administrator; `audit.read` requires Auditor or Administrator.

## 3. Proposed read surface

| Operation | Purpose | Filters/order/paging |
| --- | --- | --- |
| `GET /api/v1/dashboard/summary` | Status counts, recently-down targets, offline Agents, open incidents | Site, area, type, criticality, tag, visible status; fixed deterministic sub-list order is frozen here and enters generated OpenAPI only with runtime implementation. |
| `GET /api/v1/devices` | Existing server-paged inventory | No 2B1 `includeStatus` addition. |
| `GET /api/v1/devices/{id}/status` | Status-enriched device/probe/Agent snapshot | One Device; `404` if absent. |
| `GET /api/v1/devices/{id}/metrics` | RTT, loss and success chart data | Required `from`, `to`; optional `resolution`. |
| `GET /api/v1/devices/{id}/timeline` | Status-transition history | Cursor page; `from`, `to`, Probe, and direction filter; default occurred-at descending. |
| `GET /api/v1/devices/{id}/incidents` | Device incident history | Cursor page; status/time filters; default opened-at descending. |
| `GET /api/v1/incidents` and `GET /api/v1/incidents/{id}` | Incident center and detail | Cursor page/filter surface below. |
| `GET /api/v1/incidents/{id}/lifecycle-events` and `/comments` | Immutable lifecycle and comments | Cursor page, occurred/created-at descending. |
| `GET /api/v1/audit-events` | Authorized audit list | Cursor page/filter surface below. |

`DashboardSummaryResponse`, `StatusEnrichedDeviceResponse`, `DeviceMetricsResponse`, `CursorPage<T>`, `IncidentResponse`, `IncidentLifecycleResponse`, `IncidentCommentResponse`, and `AuditLogEntryResponse` in `EePulse.Contracts.Dashboard` are the Phase 1 wire-shape source of truth.

### Pagination, filtering and sorting

Existing collection endpoints retain `page`/`pageSize` (default 1/50; maximum 200). New history, incidents, comments, and audit lists use opaque cursor paging: `pageSize` default 50/max 200 and `nextCursor`; an absent cursor begins the query. Cursor payloads bind the normalized filters and sort so reuse with changed criteria returns `400` `cursor-filter-mismatch`.

All filter names are explicit and case-insensitive only where documented: UUIDs, exact status enums, `siteId`, `deviceId`, `probeId`, `area`, `deviceType`, `criticality`, `tag`, `openedFrom`, `openedTo`, `from`, `to`, `action`, `entityType`, `entityId`, and `actorId`. Time bounds are inclusive UTC instants. Incident ordering is `openedAt desc,id desc` by default; timeline/audit ordering is `occurredAt desc,id desc`; every alternate direction retains `id` as its tie-breaker.

## 4. Status, dashboard and chart semantics

Visible status vocabulary is exactly `Unknown`, `Up`, `Degraded`, `Down`, `Recovering`, `Maintenance`, `Disabled`. `DeviceStatusResponse` exposes both visible and underlying status, `stateVersion`, `lastFreshEventAt`, receipt time, current Agent identity, and open-incident reference. Summary counts count Probes, not Devices; a Device with multiple Probes retains one status item per Probe.

Metrics accepts only bounded UTC ranges: 1 hour, 6 hours, 24 hours, 7 days, or a custom range no greater than 31 days. `resolution=auto|raw|minute|fiveMinutes|hour`; the server selects and returns each series' effective resolution. The implementation phase must publish its maximum point count before enabling `raw`. Points are ordered ascending, use UTC instants, preserve null RTT for failed attempts, and never turn missing intervals into zero values. Missing intervals render as chart gaps. `isPartial=true` plus a stable `partialErrorCode` means some series/source data could not be returned; an unavailable entire metrics dependency is `503` Problem Details, not an empty successful series.

## 5. Timezone, stale data and caching

Storage, event processing, query bounds, cursor ordering, audit, and SignalR payloads remain UTC. The display timezone precedence is: persisted user override, selected Site IANA timezone, then browser timezone. Ambiguous daylight-saving local times display their UTC offset; custom-range inputs are converted to UTC before transmission.

### Frozen timezone-preference contract (implemented in Phase 2A)

The eventual implementation retains at most one global PostgreSQL row for each authenticated human principal after its first state-changing write. Its identity key is exactly the explicit authenticated `iss` plus `sub` claim pair. Both are required, non-empty, and at most 512 characters. Email, display name, role, `NameIdentifier`, and `Guid.Empty` are never fallbacks; missing either claim fails closed. Development authentication will supply constant issuer `https://ee-pulse.invalid/development` and a stable canonical synthetic subject derived from `X-EE-Pulse-Actor`. Production endpoints remain unavailable to identities without explicit issuer and subject claims.

The sole preference field is nullable `timezone`. `GET /api/v1/users/me/timezone-preference` is read-only and never creates a row. With no row it returns HTTP 200, `{ "timezone": null, "etag": "\"tz-0\"" }`, and `ETag: "tz-0"`. The no-row ETag is therefore exactly `"tz-0"`. A retained row has monotonically increasing positive version `n` and exactly `ETag: "tz-n"`; this cannot collide with `"tz-0"`. Response-body `etag` and response-header `ETag` are byte-identical. GET supports `If-None-Match`; a 304 includes the current ETag. The generated OpenAPI documents the conditional request header, strong ETag response header, server-controlled `X-Correlation-ID`, and 200/304/400/401/403 outcomes.

`PUT /api/v1/users/me/timezone-preference` requires exactly one strong `If-Match` value. Missing is 428. Malformed, weak, wildcard, empty, comma-list, or multi-value `If-Match` is HTTP 400 with code `invalid-if-match`. A syntactically valid but non-current tag is HTTP 412 with code `concurrency-conflict` and the current ETag. The first non-null write requires `If-Match: "tz-0"` and atomically inserts issuer, subject, canonical timezone, and version 1. A concurrent insert conflict re-reads current state and returns that 412 response; it never overwrites the winner.

`null` clears a persisted override by retaining its row, writing null timezone, and incrementing version. Rows are never deleted, preventing ABA; re-setting after a clear increments the retained version again. PUT of the same canonical timezone with the current ETag is an idempotent no-op: no version increment and no audit event. PUT null against absent `"tz-0"` is also a no-op: it creates no row and returns `"tz-0"`. The first non-null write atomically persists the preference and its redacted audit event; all other state-changing writes do the same. Audit metadata contains only operation and resulting version. The API emits one safe server-generated lowercase UUID-N correlation ID per request and reuses it in the response and audit event without persisting caller-controlled correlation, trace, or identity values.

Timezone values are validated and normalized by the reusable contract-level TZDB mechanism, backed by the NodaTime 3.3.3 embedded catalog, never the host operating-system catalog. It accepts canonical IANA IDs and recognized TZDB aliases, case-sensitively, and persists/returns only the canonical ID. The pinned provider canonicalizes `UTC` to `Etc/UTC`. Windows IDs, unknown IDs, empty or whitespace values, case-modified IDs, malformed/path-like inputs, and backslash-separated values are rejected; null remains valid only for clearing.

Timezone preference is personal settings data. Emit an audit event only for a successful state-changing create, update, or clear. Its metadata may contain operation type and resulting version only; it must not contain timezone values, issuer, subject, email, display name, authorization headers, or tokens. Logs must likewise omit raw identity tokens and unnecessary profile data. Phase 2A implements the PostgreSQL table/model/migration, repository transaction boundary, endpoint, authorization, and integration coverage for this contract. No endpoint claims dashboard/status data, incident actions, audit listing, SignalR runtime, or frontend behavior.

Every screen has distinct loading, empty, stale-data, partial-error, and permission-denied states. A successful stale cached response is labelled with `generatedAt`/last-refresh time. Summary and status reads support ETags derived from stable query/result versions. Clients send `If-None-Match`; `304` preserves cached data. Mutating commands require `If-Match` with the incident ETag; absent is `428`, stale is `412` with Problem Details code `concurrency-conflict` and the current ETag. Existing inventory row-version behavior remains unchanged.

## 6. Incident actions and exact invalid-resolution conflict

Proposed commands are:

- `POST /api/v1/incidents/{id}/acknowledge` with `AcknowledgeIncidentRequest`.
- `POST /api/v1/incidents/{id}/comments` with `AddIncidentCommentRequest`.
- `POST /api/v1/incidents/{id}/resolve` with `ResolveIncidentRequest`.

All require `incidents.operate`, non-empty bounded text, `If-Match`, and a UUID `Idempotency-Key` header. The implementation persists command receipts atomically with the action. Retrying the same route, incident, key, actor, and canonical body returns the original success response and ETag; reuse of the key with a different route, incident, actor, or body returns `409` code `idempotency-key-reuse-conflict`. Acknowledge is valid only for an open incident and otherwise returns `409` code `incident-action-state-conflict`; comments are valid for active or resolved incidents and return `201`; successful acknowledgement/manual resolution return `200` `IncidentActionResponse`. Every successful command records the action-specific append-only audit event in the same transaction.

Manual resolution is forbidden while the current underlying Probe status is `Down` or `Recovering`. The API returns HTTP `409 Conflict`, standard Problem Details with type/code `incident-manual-resolution-state-conflict`, title `Manual resolution is not currently allowed`, and extensions `incidentId`, `probeId`, `underlyingStatus`, `visibleStatus`, `stateVersion`, and `currentEtag`. It makes no incident, lifecycle, comment, or audit mutation. The client refetches incident and device-status queries, announces the reason, and keeps the resolve action unavailable until the state permits it. Confirmed recovery remains the normal automatic resolution route.

## 7. Audit and maintenance

`GET /api/v1/audit-events` is restricted to Auditor and Administrator. It returns metadata only—before/after JSON is not exposed by this WP-07 contract—and supports action/entity/actor/time filters and deterministic cursor paging. Incident mutations create append-only audit events with actor, entity, correlation ID, timestamp, and redacted action metadata.

The existing maintenance endpoints are reusable for CRUD. A later additive list contract must support scope (`siteId`, `deviceId`, `probeId`) and active/range filtering before a complete maintenance page is claimed.

## 8. SignalR invalidation and refresh policy

The proposed authorized hub is `GET/WS /hubs/dashboard`; it is not implemented in Phase 1. Connection authorization uses the same authenticated user principal and applies read-policy scope before subscription. Clients do not send arbitrary group names. Server grouping, if required, is derived only from validated Site/device scope.

The sole initial payload shape is `DashboardInvalidationEvent` with schema version 1 and event types `status.changed`, `incident.changed`, `agent.changed`, and `maintenance.changed`. Payloads contain only identifiers, optional version, and UTC occurrence time—not metrics, comments, credentials, or unrestricted inventory fields. On receipt, clients invalidate/refetch the corresponding TanStack Query keys; they do not merge aggregate state locally.

Clients reconnect with bounded exponential backoff and jitter. On successful reconnect they refetch every visible dashboard, device, incident, Agent, and maintenance query. While disconnected they refresh visible queries every 30 seconds. While connected, including NOC/TV mode, they perform a 60-second safety refresh. NOC mode never suppresses the disconnected cadence. Reconnection and refresh failures surface stale/partial-error state without fabricating data.

## 9. Problem Details and acceptance boundaries

All new endpoints declare `401`, `403`, `404`, validation `400`, rate/dependency failures where applicable, and the command-specific `412`, `428`, and `409` responses in OpenAPI when implemented. Problem Details have a stable machine-readable code and correlation ID; sensitive infrastructure details, metrics query text, and audit before/after data are excluded.

Phase 2A acceptance includes the generated OpenAPI artifact, contract/API tests, PostgreSQL persistence tests, deterministic race and rollback tests, complete unit/integration verification, pinned Release build, format/analyzer checks, and scoped whitespace checks. Remaining WP-07 work is the dashboard summary/device/status/metrics/timeline/incident/audit read surface and incident actions; SignalR runtime and frontend UI remain later slices. WP-08 owns notification fan-out; WP-09 owns reporting/retention boundaries.

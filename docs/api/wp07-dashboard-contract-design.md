# WP-07 dashboard and device experience — contract and policy design

Status: Phase 1 approved contract design (2026-09-07)
Scope: additive contracts and policy only. This document introduces no endpoint, hub, persistence, domain behavior, migration, or frontend implementation.

## 1. Boundaries and compatibility

The existing `docs/api/openapi-v1.json` remains the frozen implemented surface. The routes in this document are proposed additive `/api/v1` operations and must not be added to the checked-in OpenAPI artifact until their implementation, authorization, integration coverage, and generated-OpenAPI review arrive in a later phase.

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
| `GET /api/v1/dashboard/summary` | Status counts, recently-down targets, offline Agents, open incidents | Site, area, type, criticality, tag, visible status; fixed deterministic sub-list order documented in OpenAPI. |
| `GET /api/v1/devices` (add `includeStatus=true`) | Existing server-paged inventory enriched with current Probe status | Preserve existing filters and page contract; add visible-status filter and stable `name,id` default order. |
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

### Frozen timezone-preference contract (proposed only)

The eventual implementation persists this preference in PostgreSQL, with exactly one global row per authenticated human principal. Its identity key is the stable authenticated issuer-plus-subject pair; email address, display name, and any mutable profile claim are never keys. The sole preference field is `timezone`, an IANA timezone identifier. The proposal is `GET /api/v1/users/me/timezone-preference` and `PUT /api/v1/users/me/timezone-preference`, both requiring an authenticated human principal; the response is `TimezonePreferenceResponse { timezone, etag }` and the PUT body is `TimezonePreferenceRequest { timezone }`.

`timezone` must be a known, supported IANA zone. `null` in PUT clears the persisted override, returns the resulting current representation and ETag, and immediately falls back to selected Site timezone, then browser timezone. PUT requires a strong `If-Match` ETag: missing is `428`, stale is `412` `concurrency-conflict`, and success returns the new strong ETag. GET returns the current strong ETag and supports `If-None-Match`/`304`. The explicitly supported strong entity-tag subset is `^"[\x21\x23-\x7E]+"$`: surrounding quotes and at least one opaque visible-ASCII character are required; weak `W/` tags, spaces, embedded quotes, DEL, CR/LF, tabs, and controls are invalid, while backslash is permitted.

The row stores the IANA identifier, UTC created/updated/audit instants, and only the issuer/subject identity key needed to associate the preference. Timezone preference is personal settings data: audit records must not expose its historical values or identity claims beyond the existing authorized audit boundary; logs must not include raw identity tokens or unnecessary profile data. This is a frozen persistence contract, not Phase 1 persistence authorization: no table, model, migration, repository, endpoint, or runtime behavior is created now.

Development-role simulation may supply only a development issuer and stable synthetic subject pair through the existing development-auth seam. It must still produce an issuer-plus-subject identity, and must be disabled/fail closed in production; production never derives this preference from a role, email, or display-name simulation.

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

Phase 1 contract acceptance consists of documentation review and implementation-independent DTO tests only. Endpoint, persistence, SignalR, OpenAPI artifact, frontend, and browser coverage are explicitly deferred to later WP-07 phases.

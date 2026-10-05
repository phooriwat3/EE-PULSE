# ADR-014: Production v1 local authentication and anonymous display boundary

Status: User-approved design on 2026-09-28; implementation and deployment not started. Lead/Integration contract review remains required before changing the frozen API or generated OpenAPI.

## Context

Production Microsoft Entra ID application approval requires a Global Admin. Local IT cannot grant that approval, and earlier requests have not yielded a dependable release path. Production v1 therefore cannot depend on Entra ID/OIDC. The current production Web bundle denies access; existing dashboard, device-status, and incident reads require authenticated roles. This ADR does not change that deployed or implemented behavior.

## Decision

- Production v1 will use named individual local accounts for Operator, Engineer, Administrator, and Auditor. Authentication will be provider-independent from EE Pulse roles and permissions so approved OIDC can replace the local provider later. Development synthetic role headers remain Development-only; Agent credentials remain a separate authentication scheme. Local account, session, permission, and audit implementations require their own reviewed checkpoints.
- Production monitors will use a dedicated read-only `/display` experience in a browser on a Mini PC/TV, without interactive login or a privileged user session. Anonymous access is limited to a separate backend-enforced Viewer-safe projection. Hiding controls in the browser is not authorization.
- The approved anonymous projection covers all Sites but only Site and machine names, line/area, actual operational status, last-seen time, and aggregate status counts. It must not expose IP addresses, hostnames, owner, Agent details, incident comments or notes, actor IDs, audit data, inventory-management data, diagnostics, network identifiers, or other security-sensitive metadata. The existing richer authenticated response DTOs must not be made anonymous wholesale.
- Show every Viewer-safe device regardless of status. Preserve distinct `Online`, `Offline`, `Unknown`, `Degraded`, `Recovering`, `Maintenance`, and `Disabled` presentation; never relabel an unknown or other non-online state as offline. The existing API's `Up`/`Down` terminology and any public label mapping require explicit contract review before implementation.
- Every human-facing privileged write or action, including incident actions, configuration changes, inventory-management operations, and audit access, requires an authenticated individual account, backend authorization, and applicable audit recording. This rule does not change the existing separate Agent enrollment, Agent-specific credential, heartbeat, configuration, or result-delivery protocol. Anonymous monitors must never hold Operator, Engineer, Administrator, or Auditor sessions.
- The approved production access baseline is relevant Company Internal LANs for production monitors, Engineering, and IT only. Public Internet, Guest Wi-Fi, and other untrusted networks are excluded. Corporate VPN is not approved by this decision and may be added later through policy. Clients must use HTTPS through a central reverse proxy; the backend/API service must not be directly reachable from client networks.
- The production subnet/VLAN allowlist, reverse-proxy platform and implementation, hostname/DNS, and TLS certificate/renewal owner remain **TBD**. The proxy could be IIS, Nginx, Docker-hosted, or another approved platform; none is selected here. Do not invent a production hostname or certificate.

## Release gate and consequences

No anonymous production endpoint or `/display` route may be enabled until the Viewer-safe wire contract, network allowlist, reverse proxy, DNS, and TLS configuration are confirmed, implemented, and tested. Existing authenticated APIs and generated OpenAPI remain unchanged by this ADR. The previously approved all-Site read policy applies to users with read rights; this ADR separately limits what an anonymous display may receive.

Local authentication avoids an unavailable identity-provider approval but creates local account lifecycle, password, session, lockout, recovery, audit, and deprovisioning responsibilities. These controls and the actual local-account owner are not yet implemented or confirmed. Future OIDC must preserve the same EE Pulse permission model and require a separately approved provider registration and migration plan.

## Required follow-up

1. Product/Security and Lead review the exact Viewer-safe DTO, status/last-seen semantics, endpoint authorization matrix, and OpenAPI change before implementation.
2. IT/Infrastructure provides approved production subnet/VLAN ranges, proxy topology and trusted proxy addresses, internal DNS name, TLS certificate and renewal owner, and proof that clients cannot reach the API directly.
3. Identity/IT confirms the local-account provisioning, disable/reset, bootstrap administrator, and deprovisioning owners; implementation then specifies password/session/CSRF/lockout controls and tests.
4. Backend and Web implement separate public display and authenticated surfaces, with negative security tests for every excluded field and privileged route. Production remains fail-closed until the release gates pass.

This decision supersedes OIDC as a **production-v1 prerequisite** in the earlier technical specification and user-action register; OIDC remains a future integration target. It does not retrospectively change ADR-013's implemented incident-route authorization or authorize a silent v1 contract change.

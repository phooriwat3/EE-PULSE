# EE Pulse user and IT actions

Last updated: 2026-08-10

Do not place passwords, client secrets, private keys, enrollment tokens, or other real credentials in this file, chat, source control, or sample configuration. Deliver production secrets only through the organization's approved secret store.

| Sequence | ID | Owner | Action / required values | Why | Verification | Blocks | Safe placeholder while waiting |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | UA-01 | Product owner / EE | Confirm Sites/VLANs, approximate target count and types, role owners, thresholds, recovery, flapping, and availability rules | Validates the PRD defaults against operating policy | Signed scope/acceptance note | WP-02 final acceptance; does not block WP-01 | PRD defaults: 30 s interval, 2 s timeout, 3 attempts, 3 failures, 2 recoveries |
| 2 | UA-02 | Repository owner | Provide Git remote, branch/PR policy, CI runner, artifact destination, and reviewers | Enables protected collaboration and immutable evidence | Clean clone, CI run, permitted PR | Shared collaboration/release; local work continues | Local workspace and non-deploying GitHub Actions template |
| 3 | UA-02A | Developer workstation owner | Optional convenience: install current .NET 10 SDK (10.0.x); keep Docker available for the verified SDK-container fallback | Host currently has only SDK 9, but the approved container path satisfies reproducible builds | `dotnet --info` lists 10.0.x or pinned SDK-container build passes | Satisfied for WP-01; host-only workflows still affected | Verified `mcr.microsoft.com/dotnet/sdk:10.0.302` build/test path |
| 4 | UA-03 | Network/IT security | Approve explicit Agent `AllowedNetworks`, routing, ICMP/firewall rules, and safe test targets | Prevents unauthorized scanning and enables ICMP acceptance tests | Approved CIDR list and controlled ping test | WP-03/WP-04 environment tests | Loopback/private documentation examples only; injectable fake transport in tests |
| 5 | UA-04 | Windows/IT operations | Provide an always-on Windows host, installation admin, service-account policy, outbound HTTPS, and NTP | Needed for real Windows Service validation | Reboot/service-recovery/enrollment test evidence | WP-10/WP-11 | Worker runs interactively in development; installer remains unverified |
| 6 | UA-05 | Infrastructure owner | Provide central VM/container runtime, sizing, persistent storage, backup target, and Agent route | Needed for shared and production-like deployment | Environment readiness checklist and connectivity test | WP-10/WP-11 | Local Docker Compose |
| 7 | UA-06 | Identity/AD owner | Register OIDC app; provide issuer/tenant, client ID, redirect URI, logout URI, and group-to-role mapping via approved channels | Required for production authentication | Viewer/Operator/Engineer/Admin/Auditor matrix test | WP-07/WP-11 production readiness | Development-only local identity, disabled in Production |
| 8 | UA-07 | DNS/PKI/network owners | Select hostname, create DNS record, issue TLS certificate, assign renewal owner, approve reverse proxy/firewall | Required for production TLS and stable Agent endpoint | TLS scan and Agent-to-API connection | WP-10/WP-11 | `localhost` and development certificates |
| 9 | UA-08 | Messaging/security owners | Approve SMTP relay or webhook allowlist, sender, recipients, escalation owners, quiet hours, and test recipient | Required for real notification acceptance | Approved test delivery and redaction review | WP-08 production validation | Fake SMTP/webhook receivers only |
| 10 | UA-09 | Data/operations owners | Decide raw/aggregate/audit retention, backup location, RPO/RTO, and restore authority | Controls capacity, retention jobs, and acceptance | Restore rehearsal meets approved RPO/RTO | WP-09/WP-11 | Documented conservative local defaults |
| 11 | UA-10 | Product/change owners | Name UAT testers, acceptance window, change ticket, go-live owner, and rollback owner | Required for release authorization | Signed checklist and go/no-go record | Production release | No deployment |

## Actions currently blocking the next checkpoint

- None block starting WP-02. The pinned .NET 10 SDK container and local Docker stack passed the WP-01 gate.
- **Approaching WP-02 acceptance — UA-01:** confirm business scope and duplicate-device policy; PRD defaults are used meanwhile.
- **Before WP-04 environment testing — UA-03:** provide the authorized network ranges and safe ICMP test targets. No real network probing will occur before approval.

## Approval-only operations

These are not user-supplied data, but the Lead must request approval immediately before performing them: external repository writes/PRs, production or shared-environment deployments, firewall/DNS/AD changes, real notification delivery, destructive actions, purchases, privileged machine changes, real-credential handling, and material scope expansion.

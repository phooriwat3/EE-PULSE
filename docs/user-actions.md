# EE Pulse user and IT actions

Last updated: 2026-08-14

Never place real credentials, keys, tokens, SMTP passwords, or certificates in this file, chat, source control, or sample configuration. Use the organization's approved secret-delivery mechanism.

| Sequence | ID | Owner | Required action/values | Verification | Blocks | Safe local placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | UA-01 | Product owner / EE | Duplicate policy confirmed: normalized IP is unique among enabled Devices within a Site; disabled and cross-Site reuse are allowed; hostname is non-unique. Still confirm Sites/VLANs, target count/types, role owners, thresholds, recovery/flapping, and availability rules. | Approved scope and acceptance note; automated duplicate-rule tests. | Remaining operational decisions affect later acceptance, not the WP-02 duplicate rule. | Confirmed duplicate rule is implemented; PRD probe defaults remain 30 s interval, 2 s timeout, 3 attempts, 3 failures, 2 recoveries. |
| 2 | UA-02 | Repository owner | Confirm branch/PR policy, CI runner/artifact destination, and reviewers for the configured `origin`. | Clean clone, protected workflow, successful CI run. | Shared collaboration evidence and release; local work continues. | Local `main` and `origin/main` synchronized at `8ca821d`; non-deploying GitHub Actions workflow. |
| 3 | UA-02A | Workstation owner | Optionally install .NET SDK 10.0.x; keep Docker available if using the verified container fallback. | `dotnet --info` lists 10.0.x or pinned container gate passes. | Host-only workflows; WP-01 is satisfied through Docker. | `mcr.microsoft.com/dotnet/sdk:10.0.302`. |
| 4 | UA-03 | Network / IT security | Approve explicit Agent `AllowedNetworks`, routing, ICMP/firewall policy, and controlled IPv4-literal test targets. | Approved CIDRs and a controlled reachability test. | WP-03/04 environment acceptance. | Fake transport and documentation-only examples; no real probe. |
| 5 | UA-04 | Windows / IT operations | Provide an always-on Windows host, installation administrator, service-account policy, outbound HTTPS, and NTP. | Install, reboot, recovery, enrollment, and queue-preservation evidence. | WP-10/11 Windows acceptance. | Interactive Worker execution only. |
| 6 | UA-05 | Infrastructure owner | Provide central host/runtime, sizing, persistent storage, backup target, and Agent route. | Environment readiness and connectivity checklist. | WP-10/11 deployment acceptance. | Healthy local Docker Compose stack. |
| 7 | UA-06 | Identity / AD owner | Register OIDC app and provide issuer/tenant, client ID, redirect/logout URIs, and group-to-role mapping through approved channels. | Full role-matrix test; production fails closed without configuration. | WP-07/11 production readiness. | Development-only local identity. |
| 8 | UA-07 | DNS / PKI / network | Choose hostname, create DNS, issue TLS certificate, assign renewal owner, and approve proxy/firewall rules. | TLS scan and Agent-to-API connection. | WP-10/11 production readiness. | `localhost` and development certificates. |
| 9 | UA-08 | Messaging / security | Approve SMTP or webhook allowlist, sender, recipients, escalation owners, quiet hours, and test recipient. | Approved test delivery and redaction review. | WP-08 production validation. | Fake local receivers only. |
| 10 | UA-09 | Data / operations | Decide raw/aggregate/audit retention, backup target, RPO/RTO, and restore authority. | Isolated restore rehearsal meets policy. | WP-09/11 acceptance. | Conservative documented local defaults. |
| 11 | UA-10 | Product / change owners | Name UAT testers, window, change ticket, go-live owner, and rollback owner. | Signed go/no-go checklist. | Production release. | No deployment. |

## Actions approaching the next checkpoint

- The duplicate-device portion of UA-01 is confirmed and verified. Remaining Site/VLAN, scale, role-owner, and operational-threshold details approach later acceptance but do not block this WP-02 rule.
- UA-02 is partially satisfied because `origin` is configured. Branch/PR policy, CI runner/artifact destination, and reviewers still need confirmation before external collaboration or release evidence.
- UA-03 is required before any real ICMP environment test. WP-04 accepts IPv4 literals only; no network scanning, DNS resolution, or unapproved probing is authorized.
- WP-03 implementation is locally verified: the Server may narrow but cannot remotely expand the Agent's non-empty AllowedNetworks ceiling. UA-03 remains required before real enrollment/probing; approve only explicit CIDRs and controlled targets.
- UA-06 is not needed for this local WP-02 checkpoint, but production Web access intentionally remains denied until OIDC is configured and integrated.

WP-03 is implementation-verified locally. Before release or real Agent use, complete UA-03/04/05/06/07 as applicable: approved CIDRs and ICMP/firewall policy, disposable Windows service/DPAPI/ACL/recovery evidence, central runtime/backups, OIDC, and TLS/proxy/DNS. Obtain checkpoint commit/PR approval before external repository writes.

Approval remains required immediately before external repository writes, deployment, firewall/DNS/AD changes, real notification delivery, destructive operations, privileged machine changes, or real-credential handling.

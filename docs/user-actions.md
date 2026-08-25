# EE Pulse user and IT actions

Last updated: 2026-08-25

Never place real credentials, keys, tokens, SMTP passwords, or certificates in this file, chat, source control, or sample configuration. Use the organization's approved secret-delivery mechanism.

| Sequence | ID | Owner | Required action/values | Verification | Blocks | Safe local placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | UA-01 | Product owner / EE | Approved 2026-08-25: the complete nine-decision WP-06 MVP status/incident policy, including thresholds/quality, recovery, `RECOVERING`, `UNKNOWN`, flapping, lateness/skew, configuration-effective boundary, availability, and maintenance. Binding record: ADR-012. The duplicate policy remains: normalized IP is unique among enabled Devices within a Site; disabled and cross-Site reuse are allowed; hostname is non-unique. | ADR-012 and approved WP-06 policy record. | Closed for WP-06 policy; implementation and deterministic acceptance coverage remain. | No placeholder; implement only ADR-012 policy snapshots. |
| 2 | UA-02 | Repository owner | Confirm branch/PR policy, CI runner/artifact destination, and reviewers for the configured `origin`. | Clean clone, protected workflow, successful CI run. | Shared collaboration evidence and release; local work continues. | Local `main` and `origin/main` synchronized at `8ca821d`; non-deploying GitHub Actions workflow. |
| 3 | UA-02A | Workstation owner | Optionally install .NET SDK 10.0.x; keep Docker available if using the verified container fallback. | `dotnet --info` lists 10.0.x or pinned container gate passes. | Host-only workflows; WP-01 is satisfied through Docker. | `mcr.microsoft.com/dotnet/sdk:10.0.302`. |
| 4 | UA-03 | Network / IT security | Approve explicit Agent `AllowedNetworks`, routing, ICMP/firewall policy, and controlled IPv4-literal test targets. | Approved CIDRs and a controlled reachability test. | Real ICMP validation after the WP-04 fake-only runtime checkpoint. | Fake transport and documentation-only examples; no real probe. |
| 5 | UA-04 | Windows / IT operations | Provide an always-on Windows host, installation administrator, service-account policy, outbound HTTPS, and NTP. | Install, reboot, recovery, enrollment, and queue-preservation evidence. | WP-10/11 Windows acceptance. | Interactive Worker execution only. |
| 6 | UA-05 | Infrastructure owner | Provide central host/runtime, sizing, persistent storage, backup target, and Agent route. | Environment readiness and connectivity checklist. | WP-10/11 deployment acceptance. | Healthy local Docker Compose stack. |
| 7 | UA-06 | Identity / AD owner | Register OIDC app and provide issuer/tenant, client ID, redirect/logout URIs, and group-to-role mapping through approved channels. | Full role-matrix test; production fails closed without configuration. | WP-07/11 production readiness. | Development-only local identity. |
| 8 | UA-07 | DNS / PKI / network | Choose hostname, create DNS, issue TLS certificate, assign renewal owner, and approve proxy/firewall rules. | TLS scan and Agent-to-API connection. | WP-10/11 production readiness. | `localhost` and development certificates. |
| 9 | UA-08 | Messaging / security | Approve SMTP or webhook allowlist, sender, recipients, escalation owners, quiet hours, and test recipient. | Approved test delivery and redaction review. | WP-08 production validation. | Fake local receivers only. |
| 10 | UA-09 | Data / operations | Decide raw/aggregate/audit retention, backup target, RPO/RTO, and restore authority. | Isolated restore rehearsal meets policy. | WP-09/11 acceptance. | Conservative documented local defaults. |
| 11 | UA-10 | Product / change owners | Name UAT testers, window, change ticket, go-live owner, and rollback owner. | Signed go/no-go checklist. | Production release. | No deployment. |
| 12 | UA-11 | Product / data / operations | Approved 2026-08-20: 5 GB default Agent quota; reserve is greater of 2 GB or 10% of hosting volume; degraded at 80%; stop new Probe-result production/scheduling at 95% or reserve breach; resume below 70%; clean acknowledged records within 24 hours; preserve unacknowledged and corrupt files; prohibit silent loss. | Approval recorded in ADR-011 and WP-05 acceptance plan. | Closed; implementation must conform. | No additional placeholder. |

## Actions approaching the next checkpoint

- UA-01 is closed for the WP-06 status/incident policy. ADR-012 is binding; WP-06 implementation must add the approved deterministic acceptance coverage. Separate Site/VLAN, scale, and role-owner scope confirmation remains a product-discovery concern but does not reopen the approved WP-06 policy.
- UA-02 is partially satisfied because `origin` is configured. Branch/PR policy, CI runner/artifact destination, and reviewers still need confirmation before external collaboration or release evidence.
- WP-04 is locally integration-verified as a deterministic probe-runtime foundation: final review PASS, Agent tests 112/112, formatting, Agent host and Agent Tests Release builds with 0 warnings/errors, quality/security, and `git diff --check` passed. This is fake time/transport evidence only.
- UA-03 is required before any real ICMP environment test. WP-04 accepts IPv4 literals only; no network scanning, DNS resolution, IP discovery, or unapproved probing is authorized.
- WP-03 implementation is locally verified: the Server may narrow but cannot remotely expand the Agent's non-empty AllowedNetworks ceiling. UA-03 remains required before real enrollment/probing; approve only explicit CIDRs and controlled targets.
- UA-06 is not needed for this local WP-02 checkpoint, but production Web access intentionally remains denied until OIDC is configured and integrated.
- UA-11 is closed: its approved MVP policy is recorded in ADR-011, the WP-05 contract, recovery runbook, and acceptance plan. Any policy change requires renewed approval.

WP-04 does not provide real ICMP, host/DI wiring, Windows Service, persistence, delivery, backend ingestion, UI, or deployment evidence. Before release or real Agent use, complete UA-03/04/05/06/07 as applicable: approved CIDRs and ICMP/firewall policy, disposable Windows service/DPAPI/ACL/recovery evidence, central runtime/backups, OIDC, and TLS/proxy/DNS. Obtain checkpoint commit/PR approval before external repository writes.

Approval remains required immediately before external repository writes, deployment, firewall/DNS/AD changes, real notification delivery, destructive operations, privileged machine changes, or real-credential handling.

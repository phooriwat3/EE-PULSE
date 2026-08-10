# EE Pulse user and IT actions

Last updated: 2026-08-10

Never place real credentials, keys, tokens, SMTP passwords, or certificates in this file, chat, source control, or sample configuration. Use the organization's approved secret-delivery mechanism.

| Sequence | ID | Owner | Required action/values | Verification | Blocks | Safe local placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | UA-01 | Product owner / EE | Confirm Sites/VLANs, target count/types, duplicate-device-within-Site policy, role owners, thresholds, recovery/flapping, and availability rules. | Approved scope and acceptance note. | WP-02 acceptance; implementation may start. | PRD defaults: 30 s interval, 2 s timeout, 3 attempts, 3 failures, 2 recoveries. |
| 2 | UA-02 | Repository owner | Provide Git remote, branch/PR policy, CI runner/artifact destination, and reviewers. | Clean clone, protected workflow, successful CI run. | Shared collaboration evidence and release; local work continues. | Clean local `main` at `de076a2`; non-deploying GitHub Actions workflow. |
| 3 | UA-02A | Workstation owner | Optionally install .NET SDK 10.0.x; keep Docker available if using the verified container fallback. | `dotnet --info` lists 10.0.x or pinned container gate passes. | Host-only workflows; WP-01 is satisfied through Docker. | `mcr.microsoft.com/dotnet/sdk:10.0.302`. |
| 4 | UA-03 | Network / IT security | Approve explicit Agent `AllowedNetworks`, routing, ICMP/firewall policy, and controlled test targets. | Approved CIDRs and a controlled reachability test. | WP-03/04 environment acceptance. | Fake transport and loopback/private documentation examples only. |
| 5 | UA-04 | Windows / IT operations | Provide an always-on Windows host, installation administrator, service-account policy, outbound HTTPS, and NTP. | Install, reboot, recovery, enrollment, and queue-preservation evidence. | WP-10/11 Windows acceptance. | Interactive Worker execution only. |
| 6 | UA-05 | Infrastructure owner | Provide central host/runtime, sizing, persistent storage, backup target, and Agent route. | Environment readiness and connectivity checklist. | WP-10/11 deployment acceptance. | Healthy local Docker Compose stack. |
| 7 | UA-06 | Identity / AD owner | Register OIDC app and provide issuer/tenant, client ID, redirect/logout URIs, and group-to-role mapping through approved channels. | Full role-matrix test; production fails closed without configuration. | WP-07/11 production readiness. | Development-only local identity. |
| 8 | UA-07 | DNS / PKI / network | Choose hostname, create DNS, issue TLS certificate, assign renewal owner, and approve proxy/firewall rules. | TLS scan and Agent-to-API connection. | WP-10/11 production readiness. | `localhost` and development certificates. |
| 9 | UA-08 | Messaging / security | Approve SMTP or webhook allowlist, sender, recipients, escalation owners, quiet hours, and test recipient. | Approved test delivery and redaction review. | WP-08 production validation. | Fake local receivers only. |
| 10 | UA-09 | Data / operations | Decide raw/aggregate/audit retention, backup target, RPO/RTO, and restore authority. | Isolated restore rehearsal meets policy. | WP-09/11 acceptance. | Conservative documented local defaults. |
| 11 | UA-10 | Product / change owners | Name UAT testers, window, change ticket, go-live owner, and rollback owner. | Signed go/no-go checklist. | Production release. | No deployment. |

## Actions approaching the next checkpoint

- UA-01 should be resolved before WP-02 acceptance, especially the duplicate-device policy. It does not block starting WP-02.
- UA-02 is needed before parallel-agent branches/PR evidence can be coordinated externally. Agents can work in the shared local workspace only with the frozen ownership boundaries.
- UA-03 is required before any real ICMP environment test. No network scanning or unapproved probing is authorized.

No user action blocks starting safe local WP-02 work.

Approval remains required immediately before external repository writes, deployment, firewall/DNS/AD changes, real notification delivery, destructive operations, privileged machine changes, or real-credential handling.

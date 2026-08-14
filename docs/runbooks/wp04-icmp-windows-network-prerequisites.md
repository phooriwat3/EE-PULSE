# WP-04 Windows ICMP and network prerequisites

Status: Design-time operational boundary
Date: 2026-08-14 (Asia/Bangkok)

## Purpose and boundary

This runbook defines prerequisites for a controlled real ICMP validation after WP-04 implementation. It does not authorize probing, service installation, firewall changes, routing changes, or deployment. Automated tests use fake transport and fake time only.

## Required approvals

1. UA-03: Network/IT Security approves explicit Agent AllowedNetworks CIDRs, controlled IPv4 literal targets, expected routing, ICMP policy, and a reachability test window. Approval must be narrow; no discovery, subnet sweep, hostname, or `0.0.0.0/0` scope is allowed.
2. UA-04: Windows/IT Operations supplies an always-on Windows host, approved service-account policy, outbound HTTPS, and NTP. Service install/reboot/recovery evidence is a later WP-10/11 action.

## Controlled execution checklist

- Confirm the target is an IPv4 literal, an enabled configured Probe, and contained by both configuration scope and the local Agent AllowedNetworks ceiling.
- Confirm the local ceiling was established through controlled enrollment and was not remotely expanded.
- Confirm ICMP is permitted by the approved host/network policy and that the selected target owner consents.
- Record only Probe ID, installation-scoped target hash, approved CIDR reference, UTC window, and sanitized error category. Do not record credentials, raw protected configuration, or unneeded target addresses in shared logs.
- Verify NTP before interpreting timestamps. A target timeout/unreachable condition is a probe outcome, not proof of Agent-host failure.

## Windows behavior and troubleshooting

Windows ICMP capability can be blocked by firewall, routing, endpoint policy, service-account restrictions, or an unavailable adapter. The runtime maps these deterministically to `PermissionDenied`, `NetworkUnavailable`, `Unreachable`, `Timeout`, or `TransportError`; it does not retry without bounds and does not change central status in WP-04. If transport is unusable or applied configuration is invalid, Agent self-health may be degraded/unhealthy. Stop the controlled test if scope validation fails, unexpected targets appear, or raw sensitive data is logged.

## Deferred operations

Service installation, ACL validation, recovery/reboot, packaging, central connectivity, queue preservation, upload, monitoring alerts, and production firewall/DNS/TLS changes are outside WP-04 and require their respective WP-05+ / WP-10+ approvals.

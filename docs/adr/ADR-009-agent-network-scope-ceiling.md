# ADR-009: Dual-enforced Agent network-scope ceiling

Status: Accepted at the WP-03 contract-design gate  
Date: 2026-08-10

## Context

The MVP must never perform unrestricted scanning or probe arbitrary Server-supplied targets. Server-side validation alone does not protect the network if the central service or credential is compromised.

## Decision

Every Agent has a non-empty, locally persisted AllowedNetworks ceiling established during controlled enrollment. Server-side Agent and Agent Group policies may be equal to or narrower than this ceiling but cannot remotely widen it. Expansion requires local administrative reprovisioning.

For MVP, accept normalized IPv4 addresses and CIDRs `/8` through `/32`, with at most 64 entries. Reject `0.0.0.0/0`, unspecified, multicast, broadcast, redundant overlaps, and non-unicast targets. Loopback and link-local ranges are Development-only unless explicitly approved later.

The Server validates scope when policy or Probe assignments change, when a snapshot is published, and before returning configuration. The Agent validates the full configuration before apply and checks containment again immediately before execution. Authorization uses normalized IP addresses, never DNS names. Any invalid target rejects the entire configuration; partial delivery is forbidden.

## Consequences

- A compromised Server cannot turn an enrolled Agent into an unrestricted network scanner.
- Legitimate network expansion requires a local operational action and UA-03 approval.
- Different Agents in one group may have different ceilings; publication must be safe for each targeted Agent.
- CIDR normalization, containment, local persistence, and transport-never-invoked tests are mandatory.

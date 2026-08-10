# ADR-003: Agent-pull versioned configuration

Status: Accepted for WP-01 foundation  
Date: 2026-08-09

## Context

Agents run on internal Windows hosts that may not accept inbound connections. Configuration must survive central outages and must not become active ambiguously.

## Decision

Agents authenticate outbound to the API, poll for a monotonically versioned configuration, validate and apply it atomically, retain the last-known-good payload, and acknowledge the applied version. Central state treats a new version as effective only after acknowledgement.

## Consequences

Configuration propagation is eventually consistent and observable. Rollback and invalid-payload tests are mandatory. The design creates no remote-command channel.

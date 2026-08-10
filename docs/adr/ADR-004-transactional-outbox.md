# ADR-004: Transactional outbox for notifications

Status: Accepted for WP-01 foundation  
Date: 2026-08-09

## Context

An incident transition and its notification intent must not diverge when a process or provider fails.

## Decision

Write status, incident mutation, and notification intent to PostgreSQL in one transaction. A Worker claims outbox records and records bounded delivery attempts. Deduplicate by incident, event type, channel, and policy.

## Consequences

Delivery is at least once internally and effectively once at the policy boundary. Provider calls never occur inside the domain transaction and require idempotency/redaction tests.

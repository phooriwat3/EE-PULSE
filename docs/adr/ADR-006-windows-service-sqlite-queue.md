# ADR-006: Windows Service Agent with SQLite queue

Status: Accepted for WP-01 foundation  
Date: 2026-08-09

## Context

Probing must continue without an interactive session and through central outages, while retaining completed results across Agent restarts.

## Decision

Host the Agent as a .NET 10 Windows Service. Install immutable binaries under Program Files and mutable identity/configuration/queue data under ProgramData. Persist results in SQLite WAL mode transactionally and remove them only after server acknowledgement.

## Consequences

Installer and service-account behavior require a Windows acceptance host. Queue quotas, disk-full behavior, dead letters, upgrade preservation, and 72-hour capacity must be tested and documented.

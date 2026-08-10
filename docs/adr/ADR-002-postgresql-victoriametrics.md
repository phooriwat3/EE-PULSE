# ADR-002: PostgreSQL metadata and VictoriaMetrics time series

Status: Accepted for WP-01 foundation  
Date: 2026-08-09

## Context

Configuration, workflow, audit, idempotency, and incident state require relational transactions, while high-volume probe samples need efficient time-series ingestion and range queries.

## Decision

Use PostgreSQL as the source of truth for metadata and workflow state. Use single-node VictoriaMetrics for raw and aggregate probe metrics in the MVP. Keep a durable PostgreSQL ingestion/processing record so VictoriaMetrics failure cannot be silently treated as success.

## Consequences

The system must define partial-failure retry semantics and rehearse backup/restore for both stores. Only bounded, allowlisted labels may enter VictoriaMetrics.

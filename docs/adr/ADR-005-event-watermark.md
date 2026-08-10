# ADR-005: Current-state event watermark

Status: Accepted for WP-01 foundation  
Date: 2026-08-09

## Context

Agents can buffer results for hours. Upload order and clocks cannot be trusted to match event order, but historical samples must still be retained.

## Decision

Track the last accepted state-driving event time and run identity per Probe. Persist safe late samples as history, mark excessive clock skew, and do not let an event at or behind the configured watermark mutate current state. Serialize state mutation per Probe and deduplicate run IDs.

## Consequences

The exact lateness/skew tolerance becomes a configurable WP-06 policy. Boundary, duplicate, and out-of-order tests are release gates.

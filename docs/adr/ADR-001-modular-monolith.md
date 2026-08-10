# ADR-001: Modular monolith for the MVP

Status: Accepted for WP-01 foundation  
Date: 2026-08-09

## Context

EE Pulse needs independently understandable domain, application, infrastructure, HTTP, background-processing, and Agent concerns without the deployment and consistency cost of microservices.

## Decision

Build the central platform as one modular-monolith codebase with separate API and Worker hosts. `Domain` has no project dependencies; `Application` depends on Domain and Contracts; `Infrastructure` implements Application/Domain ports; hosts compose the modules. Shared HTTP DTOs live in Contracts. The Agent is a separate deployable with Core and Infrastructure boundaries.

## Consequences

Transactions and schema evolution stay centralized. Project references make dependency violations visible at build time. Modules may be extracted later only with measured operational need.

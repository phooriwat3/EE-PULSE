# ADR-007: One-time Agent enrollment and opaque Agent credentials

Status: Accepted at the WP-03 contract-design gate  
Date: 2026-08-10

## Context

Agents need unattended outbound identity after a one-time bootstrap. The specification permits an Agent-specific credential or certificate, requires expiring one-time enrollment tokens, rotation and revocation, and names mTLS as a production-hardening target. The current application has only user-oriented Development authentication.

## Decision

Use a short-lived, one-time enrollment token followed by a per-Agent opaque bearer credential in API v1. Both secrets contain 256 random bits and a public UUID identifier. PostgreSQL stores only domain-separated SHA-256 digests and metadata; comparison is constant-time. Enrollment consumption, Agent creation, first credential creation, token-use recording, and audit commit in one transaction with row-level concurrency protection.

Credentials expire after 90 days by default and become rotation-due after 75 days. Rotation creates one pending replacement while the current credential remains active. First successful use of the pending credential atomically promotes it and revokes the prior credential. Revocation invalidates every Agent credential immediately.

The Windows Agent stores its credential in a DPAPI LocalMachine-protected file under ProgramData with an ACL limited to the service identity and Administrators. There is no plaintext Production fallback. Agent endpoints use a separate `AgentCredential` bearer scheme and cannot be authenticated with user tokens.

Production requires TLS at the trusted boundary and explicit Agent identity configuration; otherwise Agent endpoints fail closed and readiness reports not ready. mTLS remains a compatible future hardening option.

## Consequences

- Agent credentials are individually revocable and do not require a shared Server secret.
- Database theft does not reveal usable tokens or credentials.
- TLS termination, Windows ACLs, DPAPI recovery, and credential lifetime are operational dependencies.
- A network-isolated revoked Agent cannot learn revocation until it reconnects; Server operations are nevertheless rejected immediately.
- Enrollment, rotation, revocation, redaction, expiry, and concurrency require integration and security tests.

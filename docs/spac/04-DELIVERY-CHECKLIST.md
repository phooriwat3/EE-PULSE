# EE Pulse MVP — Delivery Checklist

Use this document as the acceptance gate. Do not mark an item as passed based only on a written claim. Every item requires evidence such as an automated test, command output, screenshot, or verified runbook.

## 1. Repository and build

- [ ] A clean checkout can restore and build the backend.
- [ ] Frontend install, lint, test, and production build pass.
- [ ] The Agent publishes successfully for Windows.
- [ ] No real secret exists in the repository or inspectable history.
- [ ] `.env.example` contains placeholders only.
- [ ] Dependency versions are pinned according to policy.
- [ ] README quick-start instructions can be followed successfully.

## 2. Central services

- [ ] Docker Compose starts with the documented command.
- [ ] PostgreSQL health check passes.
- [ ] VictoriaMetrics health check passes.
- [ ] API live and readiness checks pass.
- [ ] Migrations work against an empty database.
- [ ] Restarting containers preserves data.
- [ ] Only required ports are exposed to the host.

## 3. Inventory

- [ ] Site, Device, and Probe CRUD works according to role.
- [ ] IP/hostname and threshold validation is correct.
- [ ] Optimistic concurrency prevents lost updates.
- [ ] Disable operations do not delete history.
- [ ] CSV preview reports errors per row.
- [ ] Duplicate CSV-import behavior is explicit and tested.
- [ ] Configuration changes produce audit records.

## 4. Agent

- [ ] Installs and runs as a Windows Service.
- [ ] Starts automatically after reboot.
- [ ] Enrollment tokens cannot be reused and can expire.
- [ ] Credentials do not appear in logs.
- [ ] The Agent pulls and acknowledges a configuration version.
- [ ] Invalid configuration does not destroy the last-known-good version.
- [ ] The scheduler applies jitter and prevents overlap for the same Probe.
- [ ] Concurrency limits work.
- [ ] Graceful shutdown does not lose completed results.
- [ ] Allowed-network policy is enforced.

## 5. ICMP behavior

- [ ] Results include attempts, success count, loss, and min/avg/max RTT.
- [ ] Timeout is distinguished from unreachable and other errors.
- [ ] Unit tests do not rely on the public Internet.
- [ ] A target that blocks ICMP produces an understandable error category.
- [ ] Invalid address/configuration is rejected before scheduling.

## 6. Offline queue and ingestion

- [ ] Results enter SQLite while the API is unavailable.
- [ ] The queue survives Agent restart.
- [ ] The queue drains when the API returns.
- [ ] Batch retry does not create duplicate results.
- [ ] Partial-rejection behavior is explicit.
- [ ] Queue quota and dead-letter behavior are tested.
- [ ] Ingestion enforces request-size and rate limits.
- [ ] Late data enters history without moving current state backward.

## 7. Status and incidents

- [ ] UNKNOWN → UP behaves correctly.
- [ ] Failures below the threshold do not open an Incident.
- [ ] Reaching the failure threshold opens exactly one Incident.
- [ ] DOWN → RECOVERING → UP behaves correctly.
- [ ] Recovery resolves the Incident and emits one recovery event.
- [ ] Agent outage makes Targets UNKNOWN rather than causing a DOWN storm.
- [ ] Maintenance suppresses notifications while retaining results.
- [ ] Disabled Probes are not scheduled.
- [ ] Duplicate and out-of-order events are tested.
- [ ] Flapping does not create a notification storm.

## 8. Dashboard

- [ ] Summary counts match backend data.
- [ ] Filters and pagination work with 500 Targets.
- [ ] SignalR reconnect works.
- [ ] Stale data is visibly identified.
- [ ] Loading, empty, error, forbidden, and partial-failure states exist.
- [ ] Color is not the only status indicator.
- [ ] Device charts use the correct timezone.
- [ ] Incident acknowledgement/resolution updates promptly and is persisted.
- [ ] Critical paths pass Playwright tests.

## 9. Notifications

- [ ] Opening notification is sent once per policy.
- [ ] Reminders have a configured upper bound.
- [ ] Recovery notification is sent once.
- [ ] Maintenance and quiet hours suppress notifications according to policy.
- [ ] Retry does not duplicate notifications.
- [ ] Delivery logs contain no secrets.
- [ ] Webhooks enforce allowlist/SSRF protection.
- [ ] Tests use fake receivers only.

## 10. Security

- [ ] Production does not enable development login.
- [ ] Viewer cannot modify configuration through either UI or API.
- [ ] Operator can perform only the permitted workflow actions.
- [ ] Every administrative endpoint enforces an authorization policy.
- [ ] Enrollment and revocation are audited.
- [ ] CORS and TLS configuration are documented.
- [ ] CSV exports are protected against formula injection.
- [ ] Vulnerability scan has no unresolved critical or high finding.
- [ ] Secret scan passes.
- [ ] Threat model documents trust boundaries and mitigations.

## 11. Performance and resilience

- [ ] Load test runs 500 Targets every 30 seconds for at least 60 minutes.
- [ ] No queue grows without bound.
- [ ] Overview API p95 meets the target.
- [ ] Burst ingestion meets the target.
- [ ] A 30-minute central outage and subsequent recovery pass.
- [ ] PostgreSQL restart behavior is tested.
- [ ] VictoriaMetrics-unavailable behavior is tested and does not silently lose data.
- [ ] Disk-full and queue-full behavior produces an alert and is documented.

## 12. Operations

- [ ] Installation runbook
- [ ] Upgrade runbook
- [ ] Rollback runbook
- [ ] Backup runbook
- [ ] Restore rehearsal with evidence
- [ ] Agent troubleshooting runbook
- [ ] Central-platform troubleshooting runbook
- [ ] Firewall and port matrix
- [ ] Service-account and permission guide
- [ ] Retention and capacity guide
- [ ] Known limitations
- [ ] Release notes

## 13. Final acceptance test

Execute this sequence:

1. Install the central system in a clean environment.
2. Create a test Site and Device.
3. Install and enroll a Windows Agent.
4. Confirm the Device becomes UP and an RTT chart is populated.
5. Turn off the Target or block ICMP.
6. Confirm the failure threshold and Incident opening.
7. Acknowledge the Incident.
8. Restore the Target.
9. Confirm RECOVERING, then UP, followed by one recovery notification.
10. Stop the API for 30 minutes.
11. Confirm the Agent queue grows while probing continues.
12. Start the API and confirm the queue drains without duplicates.
13. Start a Maintenance Window and simulate a failure.
14. Confirm no notification is sent while metrics are still retained.
15. Restore metadata/database backup in a test environment.

## 14. Sign-off

```text
Product owner: __________________ Date: __________
EE representative: ______________ Date: __________
Technical lead: __________________ Date: __________
Security/IT: _____________________ Date: __________
Operations owner: ________________ Date: __________
```

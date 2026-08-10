# EE Pulse MVP — Product Requirements Document

## 1. Problem statement

The EE department operates many network-connected devices, including PCs, PLCs, HMIs, servers, printers, switches, gateways, and IoT equipment. It currently lacks a central view showing which devices are reachable, suffering abnormal latency, or unavailable. Manual Ping checks detect problems late, retain no reliable history, and cannot measure availability consistently.

## 2. Product goal

EE Pulse must provide continuous IP-device monitoring, detect abnormal conditions within a defined time, preserve diagnostic evidence, and retain enough history for reliable availability reporting.

## 3. Personas

### Viewer

Can view status and history but cannot change configuration.

### Operator

Can view dashboards, acknowledge incidents, add comments, and close incidents within granted permissions.

### Engineer

Can manage devices, probes, maintenance windows, and thresholds.

### Administrator

Can manage sites, Agents, users, roles, notifications, and system settings.

### Auditor

Has read-only access to incidents, reports, configuration history, and audit logs.

## 4. Functional requirements

### FR-01 Device inventory

- Create devices with name, IP/hostname, site, area, type, owner, criticality, and tags.
- Support IPv4 in the MVP while keeping the schema extensible for IPv6.
- Prevent duplicate devices within a Site according to an explicit policy.
- Import CSV files with preview, validation, and a row-level error report.
- Disable a device without deleting its history.
- Restrict permanent deletion to Administrators and record the operation in the audit log.

### FR-02 Probe configuration

- The schema must support multiple probes per device even though ICMP is the main MVP probe.
- Configure interval, timeout, attempts, warning RTT, critical RTT, failure threshold, and recovery threshold.
- Defaults: 30-second interval, 2-second timeout, three attempts, failure threshold of three, and recovery threshold of two.
- The scheduler must add jitter so all targets do not run simultaneously.
- A new cycle for the same Probe must not overlap an unfinished cycle.

### FR-03 Agent

- Run as a Windows Service and start automatically after reboot.
- Enroll using a one-time token, then use an Agent-specific credential or certificate.
- Pull versioned configuration and retain the last-known-good version.
- Send a heartbeat every 15–30 seconds.
- Store results in SQLite when the API is unavailable.
- Retry with exponential backoff and upload results in batches.
- The Server must handle repeated batches idempotently.
- Enforce concurrency and queue-size limits.
- Never accept arbitrary shell commands from the Server.

### FR-04 Status engine

- Support UNKNOWN, UP, DEGRADED, DOWN, RECOVERING, MAINTENANCE, and DISABLED.
- A single failure must not open an incident before the failure threshold is met.
- DOWN changes to RECOVERING after initial success and to UP after meeting the recovery threshold.
- An expired Agent heartbeat must make stale targets UNKNOWN, not DOWN.
- Maintenance windows suppress notifications while probe results continue to be collected.
- Store every status transition with timestamp and reason.

### FR-05 Incident management

- Open an incident when a target enters DOWN or meets a critical alert rule.
- Do not create another incident when one for the same Probe/rule is already open.
- Support OPEN, ACKNOWLEDGED, and RESOLVED.
- Store acknowledgement user/time/comment and resolution user/time/note.
- Permit policy-based automatic resolution after confirmed recovery.
- Show total downtime and occurrence count.

### FR-06 Dashboard

- Summary cards for UP, DEGRADED, DOWN, UNKNOWN, and MAINTENANCE.
- Filters for Site, Area, Type, Criticality, Tag, and Status.
- Show the most recent result and update status through SignalR.
- Show recently down targets, offline Agents, and open incidents.
- Provide an automatically refreshing NOC/TV mode.

### FR-07 Device details

- Display device data and Probe configuration.
- Display RTT, packet loss, and success history.
- Provide 1h, 6h, 24h, 7d, and custom time ranges.
- Display the status timeline and incident history.
- Display the probing Agent and latest received-result time.

### FR-08 Notifications

- Support SMTP email and generic webhook delivery.
- Send open, escalation, and recovery notifications.
- Deduplicate by Incident and channel.
- Configure reminder interval and maximum reminder count.
- Support quiet hours and maintenance suppression.
- Store delivery status, attempts, responses, and errors without storing secrets.

### FR-09 Authentication and authorization

- Production must support OIDC and group-to-role mapping.
- A seeded local administrator may exist only in the Development environment.
- Enforce authorization in both the UI and API.
- Record important configuration changes in the audit log.

### FR-10 Reporting

- Report availability by Device, Site, and time range.
- Report downtime and incident count.
- Export CSV.
- Separate planned maintenance from unplanned downtime.
- Display UNKNOWN coverage explicitly rather than hiding it inside the availability calculation.

## 5. Non-functional requirements

### NFR-01 Performance

- Support 500 targets at a 30-second interval in the MVP.
- Ingest at least 50 results/second on average and bursts of 250 results/second in the test environment.
- Overview API p95 latency must not exceed one second with 500 targets.
- Initial dashboard load must not exceed three seconds on the defined intranet test environment.

### NFR-02 Reliability

- Provide at least 72 hours of configurable Agent offline buffering.
- Repeated batches must not create duplicate results, transitions, or incidents.
- Service restart must not lose configuration or queued results.
- Database migrations must be repeatable where appropriate or fail clearly and safely.

### NFR-03 Security

- Use TLS for production traffic.
- Do not store secrets in source control, logs, frontend bundles, or sample configuration.
- Enrollment tokens expire and can be used only once.
- Restrict network ranges that an Agent is permitted to probe.
- Treat application audit records as append-only.
- Scan dependencies and container images for vulnerabilities.

### NFR-04 Observability

- Every service exposes health and readiness endpoints.
- Logs use structured JSON and include correlation IDs.
- Expose ingestion rate, error rate, queue depth, Agent heartbeat age, Probe duration, and notification-failure metrics.
- Alert when the monitoring platform itself is unhealthy.

### NFR-05 Maintainability

- Enable nullable reference types and enforce compiler-warning policy.
- Publish OpenAPI for the public application API.
- Version shared contracts.
- Cover important business logic with unit tests.
- Use a real database through containers for integration tests.

## 6. Business rules

- A DISABLED Device is not scheduled and does not create incidents.
- A Device in MAINTENANCE remains probed but does not notify.
- If no fresh result exists beyond `max(2 × interval, heartbeat grace)`, state becomes UNKNOWN.
- DOWN requires the configured failure threshold.
- Recovery requires the configured recovery threshold.
- New configuration takes effect only after the Agent acknowledges the new version.
- Late buffered data must not incorrectly move the current state backward; the Status Engine uses event time and a watermark policy.
- Availability reports must show monitoring coverage.

## 7. MVP acceptance scenarios

### Scenario A: Normal operation

Given a device responds to Ping and its Agent is online, when probes continue to succeed, the Device is UP and the dashboard displays its latest RTT.

### Scenario B: Device down

Given a failure threshold of three, when three consecutive cycles fail, the Device becomes DOWN, one Incident opens, and one opening notification is sent.

### Scenario C: Transient failure

When one cycle fails and the following cycle succeeds, no Incident opens and the Device returns to UP.

### Scenario D: Recovery

Given a DOWN Device and a recovery threshold of two, when two consecutive cycles succeed, the Incident resolves according to policy and one recovery notification is sent.

### Scenario E: Agent disconnected

When an Agent heartbeat expires and no fresh results arrive, the Agent becomes OFFLINE and its Targets become UNKNOWN without producing a mass of Device Down incidents.

### Scenario F: Central outage

When the API is unavailable for 30 minutes, the Agent continues probing and stores results in SQLite. When the API returns, the Agent uploads the backlog without creating duplicates.

### Scenario G: Maintenance

Given an active Maintenance Window, when a Probe fails, the system stores the result and displays MAINTENANCE but does not send a notification.

## 8. Out-of-scope guardrails

- The MVP does not scan CIDR ranges without explicit authorization.
- The MVP does not execute arbitrary commands.
- The MVP does not retain packet payloads.
- ICMP success is not presented as proof that an application is healthy.
- The MVP does not implement a complete HA cluster, but it must provide backup/restore and a design that can later scale.

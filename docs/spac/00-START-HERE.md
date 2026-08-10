# EE Pulse — MVP Agent Pack

This document set provides the instructions and scope required for AI coding agents to develop EE Pulse from an empty repository into an installable and verifiable MVP.

## MVP objective

Build an internal IP-device monitoring system. A Probe Agent runs as a Windows Service, performs ICMP checks every 30 seconds, sends results to the central platform, and allows users to view status, history, incidents, and device configuration through a web dashboard.

The MVP must prove five things:

1. The Agent continues monitoring without an interactive user session.
2. The system distinguishes a device outage from an Agent outage.
3. The dashboard displays status and latency close to real time.
4. The system retains history and creates incidents without sending a new alert every 30 seconds.
5. Installation, testing, backup, restore, and upgrades are repeatable from documentation.

## Documents in this pack

- `01-PRD.en.md` — Product requirements and acceptance criteria
- `02-TECHNICAL-SPEC.en.md` — Architecture, data model, API, security, and deployment
- `03-AI-AGENT-PROMPTS.en.md` — Ready-to-use prompts for AI agents
- `04-DELIVERY-CHECKLIST.en.md` — Definition of Done and acceptance checklist

The original Thai documents remain available beside these English versions.

## Locked MVP technology stack

| Area              | Technology                                                                     |
| ----------------- | ------------------------------------------------------------------------------ |
| Web               | React, TypeScript, Vite, Material UI, TanStack Query                           |
| Charts            | Apache ECharts                                                                 |
| API               | ASP.NET Core 10 Web API                                                        |
| Real-time updates | SignalR                                                                        |
| Agent             | .NET 10 Worker Service / Windows Service                                       |
| Metadata          | PostgreSQL                                                                     |
| Time-series data  | VictoriaMetrics                                                                |
| Local Agent queue | SQLite                                                                         |
| Authentication    | OIDC/Active Directory-ready; local login only for development                  |
| Logs              | Serilog structured JSON                                                        |
| Packaging         | Docker Compose for central services; MSI or PowerShell installer for the Agent |
| Tests             | xUnit, Testcontainers, Vitest, Playwright                                      |

## MVP scope

Included:

- Create, edit, disable, and import devices from CSV
- Group devices by Site, Area, Tag, and Criticality
- ICMP probes every 30 seconds with configurable interval, timeout, and attempts
- Windows Service Probe Agent
- Agent heartbeat and local offline queue
- `UNKNOWN`, `UP`, `DEGRADED`, `DOWN`, `RECOVERING`, `MAINTENANCE`, and `DISABLED` states
- Failure and recovery thresholds with basic flapping protection
- Dashboard, device details, incident list, and Agent list
- Incident acknowledgement and resolution with comments
- Maintenance windows
- Email and generic webhook notifications
- Audit logs for configuration changes
- Retention and platform health metrics
- Docker Compose, migrations, seed data, and backup/restore instructions

Excluded:

- SNMP, OPC UA, and Modbus TCP
- Automatic discovery or automatic subnet scanning
- Automatic network topology
- Native mobile applications
- Kubernetes
- Machine learning or anomaly detection
- Remote command execution on Agents
- Billing or multi-company SaaS functionality

## How to use this pack with AI agents

1. Require the Agent to read all documents before changing code.
2. Use the Master Prompt in `03-AI-AGENT-PROMPTS.en.md`.
3. Execute Work Packages in order from WP-00 through WP-11.
4. Every Work Package must end with tests, builds, a change summary, and a handoff note.
5. Do not let multiple Agents modify migrations, shared contracts, or the solution structure simultaneously.
6. The Lead/Integration Agent owns integration decisions and shared API/schema contracts.

## Quality targets

- No known unresolved critical or high-severity security findings
- Restarting the API or Agent does not lose results already placed in the local queue
- Retrying a batch does not create duplicate results
- 500 targets at a 30-second interval operate without sustained backlog growth
- The overview dashboard loads within three seconds on the defined internal test network
- After a 30-minute central outage, the Agent uploads buffered results when the platform returns
- Runbooks exist for installation, upgrade, backup, restore, and troubleshooting

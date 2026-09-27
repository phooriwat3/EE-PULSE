// Mock data for component/browser verification only. Never imported by the app.
import type { DashboardSummaryResponse, IncidentResponse, SiteResponse } from '../../src/api/generated';

export const site: SiteResponse = {
  id: '10000000-0000-4000-8000-000000000001', code: 'BKK', name: 'Bangkok assembly', timezone: 'Asia/Bangkok', enabled: true,
  createdAt: '2026-09-27T00:00:00Z', updatedAt: '2026-09-27T00:00:00Z', rowVersion: 1,
};

export const incident: IncidentResponse = {
  id: '20000000-0000-4000-8000-000000000001', probeId: '30000000-0000-4000-8000-000000000001',
  deviceId: '40000000-0000-4000-8000-000000000001', deviceName: 'Press controller 04', siteId: site.id, siteName: site.name,
  ruleKey: 'availability', status: 'Open', openedAt: '2026-09-27T01:15:00Z', acknowledgedAt: null, acknowledgedBy: null,
  acknowledgementComment: null, resolvedAt: null, resolvedBy: null, resolutionNote: null, occurrenceCount: 2, totalDowntimeSeconds: null,
};

export const summary: DashboardSummaryResponse = {
  schemaVersion: 1, appliedFilter: { siteId: null, area: null, deviceType: null, criticality: null, tag: null, status: null },
  statusCounts: [
    { status: 'Unknown', count: 3 }, { status: 'Up', count: 86 }, { status: 'Degraded', count: 2 }, { status: 'Down', count: 4 },
    { status: 'Recovering', count: 1 }, { status: 'Maintenance', count: 2 }, { status: 'Disabled', count: 0 },
  ],
  openIncidents: [{ incidentId: incident.id, deviceId: incident.deviceId, deviceName: incident.deviceName, probeId: incident.probeId,
    siteId: site.id, siteName: site.name, status: 'Open', openedAt: incident.openedAt, occurrenceCount: 2, criticality: 'High' }],
  recentlyDown: [{ deviceId: incident.deviceId, deviceName: incident.deviceName, probeId: incident.probeId, siteId: site.id,
    siteName: site.name, area: 'Assembly line 02', criticality: 'High', sinceAt: incident.openedAt, openIncidentId: incident.id }],
  offlineAgents: [{ agentId: '50000000-0000-4000-8000-000000000001', agentName: 'Assembly monitoring agent',
    agentGroupId: '60000000-0000-4000-8000-000000000001', status: 'Offline', selfHealth: 'Healthy', lastHeartbeatAt: null, lastReportedAt: null }],
};

export const emptySummary: DashboardSummaryResponse = {
  ...summary, statusCounts: summary.statusCounts.map(item => ({ ...item, count: 0 })), openIncidents: [], recentlyDown: [], offlineAgents: [],
};

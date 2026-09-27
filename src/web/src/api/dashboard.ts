import { apiRequest, queryString } from './client';
import type { DashboardSummaryResponse, IncidentResponse } from './generated';

export function getDashboard(siteId: string, signal: AbortSignal) {
  return apiRequest<DashboardSummaryResponse>(`/api/v1/dashboard/summary${queryString({ siteId: siteId || undefined })}`, { signal, cache: 'no-store' });
}

export async function getIncident(id: string, signal: AbortSignal) {
  return apiRequest<IncidentResponse>(`/api/v1/incidents/${encodeURIComponent(id)}`, { signal, cache: 'no-store' });
}

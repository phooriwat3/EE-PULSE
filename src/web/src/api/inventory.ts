import { apiRequest, jsonRequest, queryString } from './client';
import type {
  AgentGroupResponse,
  CreateDeviceRequest,
  CreateProbeRequest,
  CreateSiteRequest,
  CsvImportCommitResponse,
  CsvImportPreviewResponse,
  DeviceResponse,
  PagedResponse,
  ProbeResponse,
  SiteResponse,
  UpdateDeviceRequest,
  UpdateProbeRequest,
  UpdateSiteRequest,
} from './contracts';

export interface DeviceFilters {
  page: number;
  pageSize: number;
  siteId?: string;
  area?: string;
  deviceType?: string;
  criticality?: 0 | 1 | 2 | 3;
  tag?: string;
  enabled?: boolean;
  search?: string;
}

export const inventoryApi = {
  sites(signal?: AbortSignal) {
    return apiRequest<PagedResponse<SiteResponse>>('/api/v1/sites?page=1&pageSize=200', { signal });
  },
  createSite(body: CreateSiteRequest) {
    return apiRequest<SiteResponse>('/api/v1/sites', jsonRequest('POST', body));
  },
  updateSite(id: string, body: UpdateSiteRequest) {
    return apiRequest<SiteResponse>(`/api/v1/sites/${id}`, jsonRequest('PUT', body));
  },
  devices(filters: DeviceFilters, signal?: AbortSignal) {
    return apiRequest<PagedResponse<DeviceResponse>>(`/api/v1/devices${queryString({ ...filters })}`, { signal });
  },
  createDevice(body: CreateDeviceRequest) {
    return apiRequest<DeviceResponse>('/api/v1/devices', jsonRequest('POST', body));
  },
  updateDevice(id: string, body: UpdateDeviceRequest) {
    return apiRequest<DeviceResponse>(`/api/v1/devices/${id}`, jsonRequest('PUT', body));
  },
  agentGroups(signal?: AbortSignal) {
    return apiRequest<PagedResponse<AgentGroupResponse>>('/api/v1/agent-groups?page=1&pageSize=200&enabled=true', { signal });
  },
  probes(deviceId: string, signal?: AbortSignal) {
    return apiRequest<PagedResponse<ProbeResponse>>(
      `/api/v1/probes${queryString({ page: 1, pageSize: 200, deviceId })}`,
      { signal },
    );
  },
  createProbe(body: CreateProbeRequest) {
    return apiRequest<ProbeResponse>('/api/v1/probes', jsonRequest('POST', body));
  },
  updateProbe(id: string, body: UpdateProbeRequest) {
    return apiRequest<ProbeResponse>(`/api/v1/probes/${id}`, jsonRequest('PUT', body));
  },
  previewCsv(csv: string) {
    return apiRequest<CsvImportPreviewResponse>('/api/v1/devices/import/preview', {
      method: 'POST',
      headers: { 'Content-Type': 'text/csv' },
      body: csv,
    });
  },
  commitCsv(previewToken: string) {
    return apiRequest<CsvImportCommitResponse>(
      '/api/v1/devices/import/commit',
      jsonRequest('POST', { previewToken }),
    );
  },
};

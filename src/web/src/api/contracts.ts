// Types in this file mirror docs/api/openapi-v1.json (v1.0.0).
export type ApiInteger = number | string;

export interface ProblemDetails {
  type?: string | null;
  title?: string | null;
  status?: ApiInteger | null;
  detail?: string | null;
  instance?: string | null;
}

export interface PagedResponse<T> {
  items: T[];
  page: ApiInteger;
  pageSize: ApiInteger;
  totalCount: ApiInteger;
}

export interface SiteResponse {
  id: string;
  code: string;
  name: string;
  timezone: string;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
  rowVersion: ApiInteger;
}

export interface CreateSiteRequest {
  code: string;
  name: string;
  timezone: string;
}

export interface UpdateSiteRequest extends CreateSiteRequest {
  enabled: boolean;
  rowVersion: ApiInteger;
}

export interface DeviceResponse {
  id: string;
  siteId: string;
  name: string;
  address: string;
  hostname: string | null;
  deviceType: string;
  area: string | null;
  owner: string | null;
  criticality: string;
  tags: string[];
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
  rowVersion: ApiInteger;
}

export interface CreateDeviceRequest {
  siteId: string;
  name: string;
  address: string;
  hostname: string | null;
  deviceType: string;
  area: string | null;
  owner: string | null;
  criticality: string;
  tags: string[];
}

export interface UpdateDeviceRequest extends CreateDeviceRequest {
  enabled: boolean;
  rowVersion: ApiInteger;
}

export interface AgentGroupResponse {
  id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
  rowVersion: ApiInteger;
}

export interface ProbeResponse {
  id: string;
  deviceId: string;
  agentGroupId: string;
  type: string;
  intervalSeconds: ApiInteger;
  timeoutMilliseconds: ApiInteger;
  attemptCount: ApiInteger;
  warningRttMilliseconds: ApiInteger | null;
  criticalRttMilliseconds: ApiInteger | null;
  failureThreshold: ApiInteger;
  recoveryThreshold: ApiInteger;
  enabled: boolean;
  configVersion: ApiInteger;
  rowVersion: ApiInteger;
}

export interface CreateProbeRequest {
  deviceId: string;
  agentGroupId: string;
  intervalSeconds: number;
  timeoutMilliseconds: number;
  attemptCount: number;
  warningRttMilliseconds: number | null;
  criticalRttMilliseconds: number | null;
  failureThreshold: number;
  recoveryThreshold: number;
}

export interface UpdateProbeRequest extends Omit<CreateProbeRequest, 'deviceId'> {
  enabled: boolean;
  rowVersion: ApiInteger;
}

export interface DeviceImportRow {
  siteCode: string;
  name: string;
  address: string;
  hostname: string | null;
  deviceType: string;
  area: string | null;
  owner: string | null;
  criticality: string;
  tags: string[];
}

export interface CsvImportError {
  field: string;
  code: string;
  message: string;
}

export interface CsvImportPreviewRow {
  rowNumber: ApiInteger;
  normalized: DeviceImportRow | null;
  errors: CsvImportError[];
}

export interface CsvImportPreviewResponse {
  previewToken: string;
  expiresAt: string;
  totalRows: ApiInteger;
  validRows: ApiInteger;
  invalidRows: ApiInteger;
  rows: CsvImportPreviewRow[];
}

export interface CsvImportCommitResponse {
  previewToken: string;
  created: ApiInteger;
  skipped: ApiInteger;
  deviceIds: string[];
  errors: CsvImportPreviewRow[];
  alreadyCommitted: boolean;
}

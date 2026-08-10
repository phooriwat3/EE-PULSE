export interface HealthResponse {
  schemaVersion: number;
  service: string;
  status: string;
  checkedAt: string;
  version: string;
}

export async function getApiHealth(signal?: AbortSignal): Promise<HealthResponse> {
  const response = await fetch('/health/ready', {
    headers: { Accept: 'application/json' },
    signal,
  });

  if (!response.ok) {
    throw new Error(`Health check failed with HTTP ${response.status}`);
  }

  return (await response.json()) as HealthResponse;
}

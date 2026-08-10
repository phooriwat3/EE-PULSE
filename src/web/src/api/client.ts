import type { ProblemDetails } from './contracts';

export type DevelopmentRole =
  | 'Viewer'
  | 'Operator'
  | 'Engineer'
  | 'Administrator'
  | 'Auditor';

export interface DevelopmentSession {
  role: DevelopmentRole;
  actorId?: string;
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem?: ProblemDetails,
  ) {
    super(problem?.detail ?? problem?.title ?? `Request failed with HTTP ${status}`);
    this.name = 'ApiError';
  }
}

let session: DevelopmentSession | null = null;

export function setApiSession(value: DevelopmentSession | null) {
  session = import.meta.env.DEV ? value : null;
}

function headers(init?: HeadersInit) {
  const result = new Headers(init);
  result.set('Accept', 'application/json');
  if (import.meta.env.DEV && session) {
    result.set('X-EE-Pulse-Role', session.role);
    if (session.actorId) result.set('X-EE-Pulse-Actor', session.actorId);
  }
  return result;
}

export async function apiRequest<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, { ...init, headers: headers(init.headers) });
  if (!response.ok) {
    let problem: ProblemDetails | undefined;
    if (response.headers.get('content-type')?.includes('json')) {
      problem = (await response.json()) as ProblemDetails;
    }
    throw new ApiError(response.status, problem);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export function jsonRequest(method: 'POST' | 'PUT', body: unknown): RequestInit {
  return {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  };
}

export function queryString(values: Record<string, string | number | boolean | undefined>) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

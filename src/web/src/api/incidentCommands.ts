import { ApiError, apiHeaders, apiRequest } from './client';
import type { AddIncidentCommentRequest, AcknowledgeIncidentRequest, IncidentActionResponse, IncidentCommentResponse, IncidentResponse, ResolveIncidentRequest, StatusEnrichedDeviceResponse } from './generated';

export type CommandKind = 'acknowledge' | 'comments' | 'resolve';
export type IncidentSnapshot = { incident: IncidentResponse; etag: string | null };
export type CommandIntent = Readonly<{ kind: CommandKind; incidentId: string; key: string; etag: string; body: string }>;
// Exactly ADR-013 §2.2, not JavaScript trim (which removes U+FEFF but not U+0085).
const padding = /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g;
export function normalizeIncidentText(raw: string) { return raw.replace(padding, ''); }
export function incidentTextError(raw: string): string | undefined {
  if (raw.length < 1 || !normalizeIncidentText(raw)) return 'Enter a comment or note.';
  if (raw.length > 2000) return 'Use at most 2,000 characters, counting spaces and emoji as two characters.';
  for (const scalar of raw) {
    const value = scalar.codePointAt(0)!;
    if (value >= 0xd800 && value <= 0xdfff) return 'The text contains an incomplete Unicode character.';
  }
  if (/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/.test(normalizeIncidentText(raw))) return 'Remove line breaks and control characters inside the text.';
}
export function isStrongIncidentEtag(value: string | null): value is string { return value !== null && /^"[\x21\x23-\x7e]+"$/.test(value); }
export async function getIncidentSnapshot(id: string, signal: AbortSignal): Promise<IncidentSnapshot> {
  // Keep the HTTP header together with the representation. Never derive an ETag from DTO fields.
  const response = await fetch(`/api/v1/incidents/${encodeURIComponent(id)}`, { signal, cache: 'no-store', headers: apiHeaders() });
  if (!response.ok) throw await commandHttpError(response, signal);
  const etag = response.headers.get('ETag');
  return { incident: await response.json() as IncidentResponse, etag: isStrongIncidentEtag(etag) ? etag : null };
}
export function getIncidentDeviceStatus(deviceId: string, signal: AbortSignal) {
  return apiRequest<StatusEnrichedDeviceResponse>(`/api/v1/devices/${encodeURIComponent(deviceId)}/status`, { signal, cache: 'no-store' });
}
export function createCommandIntent(kind: CommandKind, incidentId: string, etag: string, raw: string): CommandIntent {
  if (incidentTextError(raw) || !isStrongIncidentEtag(etag)) throw new Error('Command input is not valid.');
  const body: AcknowledgeIncidentRequest | AddIncidentCommentRequest | ResolveIncidentRequest = kind === 'resolve' ? { note: raw } : { comment: raw };
  return Object.freeze({ kind, incidentId, etag, key: crypto.randomUUID().toLowerCase(), body: JSON.stringify(body) });
}
type IncidentProblem = { code?: string };
export function incidentProblemCode(error: ApiError) { return (error.problem as IncidentProblem | undefined)?.code; }
async function commandHttpError(response: Response, signal: AbortSignal) {
  let problem;
  try { problem = await response.json(); } catch (error) { if (signal.aborted) throw error; }
  return new ApiError(response.status, problem);
}
export async function sendIncidentCommand(intent: CommandIntent, signal: AbortSignal) {
  const response = await fetch(`/api/v1/incidents/${encodeURIComponent(intent.incidentId)}/${intent.kind}`, {
    method: 'POST', signal, cache: 'no-store', body: intent.body,
    headers: apiHeaders({ 'Content-Type': 'application/json', 'If-Match': intent.etag, 'Idempotency-Key': intent.key }),
  });
  if (!response.ok) throw await commandHttpError(response, signal);
  if (response.status !== (intent.kind === 'comments' ? 201 : 200)) throw new Error('Command response was not confirmed.');
  // If body decoding fails after commit, retain the intent for exact retry, not a new command.
  return await response.json() as IncidentActionResponse | IncidentCommentResponse;
}

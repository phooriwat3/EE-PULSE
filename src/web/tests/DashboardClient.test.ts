import { afterEach, expect, it, vi } from 'vitest';
import { getDashboard, getIncident } from '../src/api/dashboard';
import { ApiError, setApiSession } from '../src/api/client';

afterEach(() => { setApiSession(null); vi.unstubAllGlobals(); });

it('uses the shared development auth boundary, signal, and no-store for snapshot reads', async () => {
  setApiSession({ role: 'Viewer' });
  const signal = new AbortController().signal;
  const fetcher = vi.fn(() => Promise.resolve(new Response('{}', { headers: { 'Content-Type': 'application/json' } })));
  vi.stubGlobal('fetch', fetcher);
  await getDashboard('10000000-0000-4000-8000-000000000001', signal);
  const [path, request] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
  expect(path).toBe('/api/v1/dashboard/summary?siteId=10000000-0000-4000-8000-000000000001');
  expect(request.signal).toBe(signal);
  expect(request.cache).toBe('no-store');
  expect(new Headers(request.headers).get('X-EE-Pulse-Role')).toBe('Viewer');
});

it('encodes incident identifiers, and preserves HTTP failure classification for malformed problem bodies', async () => {
  const fetcher = vi.fn(() => Promise.resolve(new Response('not-json', { status: 503, headers: { 'Content-Type': 'application/problem+json' } })));
  vi.stubGlobal('fetch', fetcher);
  await expect(getIncident('unsafe/id', new AbortController().signal)).rejects.toMatchObject({ name: 'ApiError', status: 503 });
  expect(fetcher.mock.calls[0]).toEqual(['/api/v1/incidents/unsafe%2Fid', expect.objectContaining({ cache: 'no-store' })]);
});

it('propagates caller cancellation rather than translating it to a dependency error', async () => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new DOMException('Aborted', 'AbortError'))));
  const controller = new AbortController();
  controller.abort();
  const error = await getDashboard('', controller.signal).catch((value: unknown) => value);
  expect(error).toBeInstanceOf(DOMException);
  expect(error).not.toBeInstanceOf(ApiError);
});

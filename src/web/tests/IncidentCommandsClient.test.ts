import { afterEach, expect, it, vi } from 'vitest';
import { createCommandIntent, getIncidentSnapshot, incidentTextError, normalizeIncidentText, sendIncidentCommand } from '../src/api/incidentCommands';
import { setApiSession } from '../src/api/client';

afterEach(() => { setApiSession(null); vi.unstubAllGlobals(); });
it.each(['acknowledge', 'comments', 'resolve'] as const)('serializes %s with lowercase DTO names and immutable exact retry headers/body', async kind => {
  setApiSession({ role: 'Operator', actorId: '00000000-0000-4000-8000-000000000002' });
  const fetcher = vi.fn(() => Promise.resolve(new Response('{}', { status: kind === 'comments' ? 201 : 200 })));
  vi.stubGlobal('fetch', fetcher);
  const intent = createCommandIntent(kind, '00000000-0000-4000-8000-000000000030', '"authoritative"', '  é😀<  ');
  expect(Object.isFrozen(intent)).toBe(true);
  expect(intent.key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  expect(intent.key).not.toBe('00000000-0000-0000-0000-000000000000');
  const signal = new AbortController().signal;
  await sendIncidentCommand(intent, signal); await sendIncidentCommand(intent, signal);
  for (const [path, init] of fetcher.mock.calls as unknown as [string, RequestInit][]) {
    expect(path).toBe(`/api/v1/incidents/${intent.incidentId}/${kind}`);
    expect(init.body).toBe(JSON.stringify(kind === 'resolve' ? { note: '  é😀<  ' } : { comment: '  é😀<  ' }));
    const headers = new Headers(init.headers);
    expect(headers.get('If-Match')).toBe('"authoritative"'); expect(headers.get('Idempotency-Key')).toBe(intent.key);
    expect(headers.get('X-EE-Pulse-Role')).toBe('Operator'); expect(headers.get('X-EE-Pulse-Actor')).toBe('00000000-0000-4000-8000-000000000002');
    expect(headers.get('Content-Type')).toBe('application/json'); expect(init.signal).toBe(signal);
  }
});
it('obtains detail ETag from the response, rejects weak/missing tags for commands, and creates new keys only for new intents', async () => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('{"id":"real"}', { headers: { ETag: '"opaque"' } }))));
  const snapshot = await getIncidentSnapshot('real', new AbortController().signal);
  expect(snapshot.etag).toBe('"opaque"');
  const first = createCommandIntent('comments', 'real', snapshot.etag!, 'x');
  const second = createCommandIntent('comments', 'real', snapshot.etag!, 'x');
  expect(first.key).not.toBe(second.key);
  expect(() => createCommandIntent('comments', 'real', 'W/"weak"', 'x')).toThrow();
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('{}'))));
  expect((await getIncidentSnapshot('real', new AbortController().signal)).etag).toBeNull();
});
it.each(['', ' ', '\u0085\u2000', 'x'.repeat(2001), ' '.repeat(2) + 'x'.repeat(2000), '\ud800', '\udc00', 'a\nb', 'a\u0000b', 'a\u0085b', 'a\u2028b', 'a\u2029b'])('rejects frozen invalid raw/Unicode/text boundary %j', raw => {
  expect(incidentTextError(raw)).toBeDefined();
});
it.each(['x', 'x'.repeat(2000), '😀'.repeat(1000), '\uFEFF', '\u0085é😀\u3000', '\t x \n', 'é<"\\�'])('accepts frozen valid text %j without Unicode normalization', raw => {
  expect(incidentTextError(raw)).toBeUndefined();
});
it('uses the exact .NET trim set and preserves internal whitespace and FEFF', () => {
  const set = '\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000';
  expect(normalizeIncidentText(set + 'e\u0301  😀\uFEFF' + set)).toBe('e\u0301  😀\uFEFF');
  expect(incidentTextError('😀'.repeat(1000) + 'x')).toBeDefined();
});
it('does not translate cancellation or a malformed success body into confirmed success', async () => {
  const intent = createCommandIntent('comments', 'id', '"e"', 'x');
  vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new DOMException('Aborted', 'AbortError'))));
  await expect(sendIncidentCommand(intent, new AbortController().signal)).rejects.toHaveProperty('name', 'AbortError');
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('broken', { status: 201 }))));
  await expect(sendIncidentCommand(intent, new AbortController().signal)).rejects.toThrow();
});

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { OperationsDashboard } from '../src/dashboard/OperationsDashboard';

const summary = {
  schemaVersion: 1, appliedFilter: {},
  statusCounts: ['Unknown', 'Up', 'Degraded', 'Down', 'Recovering', 'Maintenance', 'Disabled'].map(status => ({ status, count: status === 'Up' ? 12 : 0 })),
  recentlyDown: [], offlineAgents: [],
  openIncidents: [{ incidentId: 'incident-1', deviceName: 'Assembly robot 04', siteName: 'Plant A', status: 'Open', criticality: 'High', occurrenceCount: 2, openedAt: '2026-09-27T08:00:00Z' }],
};
function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ETag: '"opaque"' } }); }
function mount() { return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><OperationsDashboard sessionKey="test-session" /></QueryClientProvider>); }
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('loads mocked API snapshots and opens incident detail without mutation', async () => {
  const fetcher = vi.fn((input: RequestInfo | URL) => Promise.resolve(json(String(input).includes('/sites') ? { items: [] } : String(input).includes('/incidents/') ? { id: 'incident-1', deviceName: 'Assembly robot 04', siteName: 'Plant A', status: 'Open', occurrenceCount: 2, ruleKey: 'availability', openedAt: '2026-09-27T08:00:00Z', acknowledgedAt: null, resolvedAt: null } : summary)));
  vi.stubGlobal('fetch', fetcher);
  mount();
  expect(await screen.findByText('Assembly robot 04')).toBeInTheDocument();
  expect(screen.getByText('12')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'View incident for Assembly robot 04' }));
  expect(await screen.findByText('availability')).toBeInTheDocument();
  expect(screen.getByRole('dialog')).toBeInTheDocument();
  expect(fetcher.mock.calls.every(([input]) => String(input).startsWith('/api/v1/'))).toBe(true);
});

it('shows a safe dependency error instead of presenting zero equipment as healthy', async () => {
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => Promise.resolve(String(input).includes('/sites') ? json({ items: [] }) : json({ title: 'Unavailable' }, 503))));
  mount();
  expect(await screen.findByText(/service is temporarily unavailable/i)).toBeInTheDocument();
  expect(screen.queryByText('No open incidents in this snapshot')).not.toBeInTheDocument();
});

it('shows explicit empty lists only for a successful snapshot', async () => {
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => Promise.resolve(json(String(input).includes('/sites') ? { items: [] } : { ...summary, openIncidents: [] }))));
  mount();
  expect(await screen.findByText('No open incidents in this snapshot')).toBeInTheDocument();
  expect(screen.getByText('No offline agents in this snapshot.')).toBeInTheDocument();
});

it('retains a labelled stale snapshot on refresh dependency failure and recovers on retry', async () => {
  let reads = 0;
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
    if (String(input).includes('/sites')) return Promise.resolve(json({ items: [] }));
    return Promise.resolve(++reads === 2 ? json({}, 503) : json(summary));
  }));
  mount();
  await screen.findByText('Assembly robot 04');
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
  expect(await screen.findByText(/may be stale/)).toBeInTheDocument();
  expect(screen.getByText('Assembly robot 04')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Retry snapshot' }));
  await waitFor(() => expect(screen.queryByText(/may be stale/)).not.toBeInTheDocument());
});

it.each([401, 403])('hides previously loaded data after access denial %s', async status => {
  let reads = 0;
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => Promise.resolve(String(input).includes('/sites') ? json({ items: [] }) : ++reads === 1 ? json(summary) : json({}, status))));
  mount();
  await screen.findByText('Assembly robot 04');
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
  await screen.findByText(status === 401 ? /session is no longer authorized/ : /role cannot access/);
  expect(screen.queryByText('Assembly robot 04')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Retry snapshot' })).not.toBeInTheDocument();
});

it('shows loading without fabricated counts, and cancels snapshot requests on unmount', async () => {
  let requestSignal: AbortSignal | undefined;
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input).includes('/sites')) return Promise.resolve(json({ items: [] }));
    requestSignal = init?.signal as AbortSignal;
    return new Promise<Response>((_, reject) => requestSignal!.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError'))));
  }));
  const mounted = mount();
  expect(await screen.findByText('Loading operations snapshot')).toBeInTheDocument();
  expect(screen.queryByText('12')).not.toBeInTheDocument();
  mounted.unmount();
  expect(requestSignal?.aborted).toBe(true);
});

it('cancels pending detail on close and exposes a labelled dialog', async () => {
  let detailSignal: AbortSignal | undefined;
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input).includes('/incidents/')) {
      detailSignal = init?.signal as AbortSignal;
      return new Promise<Response>((_, reject) => detailSignal!.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError'))));
    }
    return Promise.resolve(json(String(input).includes('/sites') ? { items: [] } : summary));
  }));
  mount();
  await screen.findByText('Assembly robot 04');
  fireEvent.click(screen.getByRole('button', { name: 'View incident for Assembly robot 04' }));
  expect(await screen.findByRole('dialog', { name: /Incident details/ })).toBeInTheDocument();
  await waitFor(() => expect(detailSignal).toBeDefined());
  fireEvent.click(screen.getByRole('button', { name: 'Close' }));
  await waitFor(() => expect(detailSignal?.aborted).toBe(true));
});

it('refuses to display an unsafe integer as an exact probe count', async () => {
  const data = { ...summary, statusCounts: summary.statusCounts.map(item => ({ ...item, count: item.status === 'Up' ? Number.MAX_SAFE_INTEGER + 1 : 0 })) };
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => Promise.resolve(json(String(input).includes('/sites') ? { items: [] } : data))));
  mount();
  expect(await screen.findByText('Count unavailable')).toBeInTheDocument();
});

it('isolates cached snapshots across session keys', async () => {
  let reads = 0;
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
    if (String(input).includes('/sites')) return Promise.resolve(json({ items: [] }));
    return Promise.resolve(json(++reads === 1 ? summary : { ...summary, openIncidents: [] }));
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const mounted = render(<QueryClientProvider client={client}><OperationsDashboard sessionKey="first" /></QueryClientProvider>);
  await screen.findByText('Assembly robot 04');
  mounted.rerender(<QueryClientProvider client={client}><OperationsDashboard sessionKey="second" /></QueryClientProvider>);
  await screen.findByText('No open incidents in this snapshot');
  expect(screen.queryByText('Assembly robot 04')).not.toBeInTheDocument();
  expect(reads).toBe(2);
});

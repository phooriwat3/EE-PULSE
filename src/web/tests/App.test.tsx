import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App, ProductionAuthenticationRequired } from '../src/App';

const site = {
  id: '10000000-0000-4000-8000-000000000001',
  code: 'BKK',
  name: 'Bangkok Lab',
  timezone: 'Asia/Bangkok',
  enabled: true,
  createdAt: '2026-08-10T00:00:00Z',
  updatedAt: '2026-08-10T00:00:00Z',
  rowVersion: 1,
};

const device = {
  id: '20000000-0000-4000-8000-000000000001',
  siteId: site.id,
  name: 'Synthetic PLC',
  address: '192.0.2.10',
  hostname: 'plc.example.test',
  deviceType: 'PLC',
  area: 'Test cell',
  owner: 'Development',
  criticality: 'High',
  tags: ['synthetic'],
  enabled: true,
  createdAt: '2026-08-10T00:00:00Z',
  updatedAt: '2026-08-10T00:00:00Z',
  rowVersion: 2,
};

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function paged(items: unknown[]) {
  return { items, page: 1, pageSize: 20, totalCount: items.length };
}

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}><App /></QueryClientProvider>);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('WP-02 inventory App', () => {
  it('renders a fail-closed production authentication state without a role chooser', () => {
    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <ProductionAuthenticationRequired />
      </QueryClientProvider>,
    );

    expect(screen.getByRole('heading', { name: 'Authentication required' })).toBeInTheDocument();
    expect(screen.getByText(/OIDC authentication is not configured/i)).toBeInTheDocument();
    expect(screen.queryByLabelText('Role')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Use synthetic role' })).not.toBeInTheDocument();
  });

  it('requires a synthetic session and renders accessible empty and read-only states', async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      expect(new Headers(init?.headers).get('X-EE-Pulse-Role')).toBe('Viewer');
      const url = String(input);
      return Promise.resolve(json(url.includes('/sites') ? paged([]) : paged([])));
    });
    vi.stubGlobal('fetch', fetchMock);
    renderApp();

    expect(screen.getByRole('heading', { name: 'Development access' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Use synthetic role' }));

    expect(await screen.findByText('No Devices match these filters')).toBeInTheDocument();
    expect(screen.getByText(/read-only role/i)).toBeInTheDocument();
  });

  it('shows server Device data with textual state and Site context', async () => {
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      return Promise.resolve(json(url.includes('/sites') ? paged([site]) : paged([device])));
    }));
    renderApp();
    fireEvent.click(screen.getByRole('button', { name: 'Use synthetic role' }));

    expect(await screen.findByRole('heading', { name: 'Synthetic PLC' })).toBeInTheDocument();
    expect(screen.getByText('Enabled')).toBeInTheDocument();
    expect(screen.getByText(/Bangkok Lab · PLC/)).toBeInTheDocument();
    expect(screen.getByText(/1 Devices · page 1 of 1/)).toBeInTheDocument();
  });

  it('emits the OpenAPI integer value for a readable criticality filter', async () => {
    const urls: string[] = [];
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      urls.push(url);
      return Promise.resolve(json(url.includes('/sites') ? paged([site]) : paged([])));
    }));
    renderApp();
    fireEvent.click(screen.getByRole('button', { name: 'Use synthetic role' }));
    await screen.findByText('No Devices match these filters');

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Criticality' }));
    fireEvent.click(screen.getByRole('option', { name: 'High' }));

    await waitFor(() => expect(urls.some((url) => url.includes('criticality=2'))).toBe(true));
    expect(screen.getByRole('combobox', { name: 'Criticality' })).toHaveTextContent('High');
  });

  it('renders an explicit forbidden CSV state for a Viewer', async () => {
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) =>
      Promise.resolve(json(String(input).includes('/sites') ? paged([site]) : paged([])))));
    renderApp();
    fireEvent.click(screen.getByRole('button', { name: 'Use synthetic role' }));
    await screen.findByText('No Devices match these filters');
    fireEvent.click(screen.getByRole('tab', { name: 'CSV import' }));

    expect(screen.getByText(/permission denied/i)).toBeInTheDocument();
    expect(screen.getByText(/current role is Viewer/i)).toBeInTheDocument();
  });

  it('previews CSV row-level errors using the exact text/csv contract', async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/devices/import/preview')) {
        expect(new Headers(init?.headers).get('Content-Type')).toBe('text/csv');
        expect(new Headers(init?.headers).get('X-EE-Pulse-Role')).toBe('Engineer');
        expect(new Headers(init?.headers).get('X-EE-Pulse-Actor')).toMatch(/^[0-9a-f-]{36}$/);
        return Promise.resolve(json({
          previewToken: '30000000-0000-4000-8000-000000000001',
          expiresAt: '2026-08-10T12:15:00Z',
          totalRows: 1,
          validRows: 0,
          invalidRows: 1,
          rows: [{
            rowNumber: 2,
            normalized: null,
            errors: [{ field: 'address', code: 'validation', message: 'Address is invalid.' }],
          }],
        }));
      }
      return Promise.resolve(json(url.includes('/sites') ? paged([site]) : paged([])));
    });
    vi.stubGlobal('fetch', fetchMock);
    renderApp();

    fireEvent.mouseDown(screen.getByLabelText('Role'));
    fireEvent.click(screen.getByRole('option', { name: 'Engineer' }));
    fireEvent.click(screen.getByRole('button', { name: 'Use synthetic role' }));
    await screen.findByText('No Devices match these filters');
    fireEvent.click(screen.getByRole('tab', { name: 'CSV import' }));

    const file = new File(['siteCode,name,address,hostname,deviceType,area,owner,criticality,tags\nBKK,Bad,nope,,PLC,,,High,'], 'devices.csv', { type: 'text/csv' });
    Object.defineProperty(file, 'text', { value: () => Promise.resolve('siteCode,name,address,hostname,deviceType,area,owner,criticality,tags\nBKK,Bad,nope,,PLC,,,High,') });
    const input = document.querySelector('input[type="file"]');
    expect(input).not.toBeNull();
    fireEvent.change(input!, { target: { files: [file] } });
    await screen.findByText('Selected: devices.csv');
    fireEvent.click(screen.getByRole('button', { name: 'Preview import' }));

    expect(await screen.findByText(/Invalid: 1 error/)).toBeInTheDocument();
    expect(screen.getByText(/address is invalid/i)).toBeInTheDocument();
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/devices/import/preview',
      expect.objectContaining({ method: 'POST' }),
    ));
  });

  it('closes a stale Site editor and refetches when Reload latest is selected', async () => {
    let siteReads = 0;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/sites') && (!init?.method || init.method === 'GET')) {
        siteReads += 1;
        return Promise.resolve(json(paged([site])));
      }
      if (url.includes(`/sites/${site.id}`) && init?.method === 'PUT') {
        return Promise.resolve(json({ title: 'Conflict', status: 409, detail: 'The Site was changed by another user.' }, 409));
      }
      return Promise.resolve(json(paged([])));
    }));
    renderApp();

    fireEvent.mouseDown(screen.getByLabelText('Role'));
    fireEvent.click(screen.getByRole('option', { name: 'Administrator' }));
    fireEvent.click(screen.getByRole('button', { name: 'Use synthetic role' }));
    await screen.findByText('No Devices match these filters');
    fireEvent.click(screen.getByRole('tab', { name: 'Sites' }));
    await screen.findByRole('heading', { name: 'Bangkok Lab' });
    fireEvent.click(screen.getByRole('button', { name: 'Edit Site' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/Site update conflict/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reload latest' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit Site' })).not.toBeInTheDocument());
    await waitFor(() => expect(siteReads).toBeGreaterThanOrEqual(2));
  });
});

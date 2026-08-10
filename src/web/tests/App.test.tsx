import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App } from '../src/App';

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('App', () => {
  it('shows the versioned API health result', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            schemaVersion: 1,
            service: 'ee-pulse-api',
            status: 'ready',
            checkedAt: '2026-08-09T10:30:00.000Z',
            version: '0.1.0',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    );

    renderApp();

    expect(await screen.findByText('Ready')).toBeInTheDocument();
    expect(screen.getByText(/ee-pulse-api contract v1/i)).toBeInTheDocument();
  });

  it('exposes an understandable unavailable state', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 503 })));

    renderApp();

    expect(await screen.findByText('Unavailable')).toBeInTheDocument();
    expect(screen.getByText(/health endpoint is unavailable/i)).toBeInTheDocument();
  });
});

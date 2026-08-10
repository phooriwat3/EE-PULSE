// The Playwright package is owned by src/web; this relative entry keeps the root free of a second npm workspace.
import { expect, test, type Page, type Route } from '../../src/web/node_modules/@playwright/test/index.js';

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

function paged(items: unknown[]) {
  return { items, page: 1, pageSize: 20, totalCount: items.length };
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function signInAsEngineer(page: Page) {
  await page.goto('/');
  await page.getByLabel('Role').click();
  await page.getByRole('option', { name: 'Engineer' }).click();
  await page.getByRole('button', { name: 'Use synthetic role' }).click();
}

test('Engineer creates and soft-disables a Device through the v1 contract', async ({ page }) => {
  let devices: Record<string, unknown>[] = [];
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    expect(request.headers()['x-ee-pulse-role']).toBe('Engineer');
    expect(request.headers()['x-ee-pulse-actor']).toMatch(/^[0-9a-f-]{36}$/);
    if (url.pathname === '/api/v1/sites') return json(route, paged([site]));
    if (url.pathname === '/api/v1/devices' && request.method() === 'GET') return json(route, paged(devices));
    if (url.pathname === '/api/v1/devices' && request.method() === 'POST') {
      const requestBody = request.postDataJSON();
      const created = {
        ...requestBody,
        id: '20000000-0000-4000-8000-000000000001',
        enabled: true,
        createdAt: '2026-08-10T01:00:00Z',
        updatedAt: '2026-08-10T01:00:00Z',
        rowVersion: 1,
      };
      devices = [created];
      return json(route, created, 201);
    }
    if (url.pathname.startsWith('/api/v1/devices/') && request.method() === 'PUT') {
      const updated = {
        ...devices[0],
        ...request.postDataJSON(),
        updatedAt: '2026-08-10T01:01:00Z',
        rowVersion: 2,
      };
      devices = [updated];
      return json(route, updated);
    }
    return json(route, { title: 'Unexpected route', status: 404 }, 404);
  });

  await signInAsEngineer(page);
  await expect(page.getByText('No Devices match these filters')).toBeVisible();
  await page.getByRole('button', { name: 'Create Device' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create Device' });
  await dialog.getByLabel('Site').click();
  await page.getByRole('option', { name: /Bangkok Lab/ }).click();
  await dialog.getByLabel('Device name').fill('Synthetic PLC');
  await dialog.getByLabel('IPv4 address').fill('192.0.2.50');
  await dialog.getByLabel('Device type').fill('PLC');
  await dialog.getByRole('button', { name: 'Save Device' }).click();

  await expect(page.getByRole('heading', { name: 'Synthetic PLC' })).toBeVisible();
  await expect(page.getByText('Enabled', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Soft-disable' }).click();
  await expect(page.getByText('Disabled', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Re-enable' })).toBeVisible();
});

test('Engineer sees row errors before committing valid CSV rows', async ({ page }) => {
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname === '/api/v1/sites') return json(route, paged([site]));
    if (url.pathname === '/api/v1/devices' && request.method() === 'GET') return json(route, paged([]));
    if (url.pathname === '/api/v1/devices/import/preview') {
      expect(request.headers()['content-type']).toContain('text/csv');
      return json(route, {
        previewToken: '30000000-0000-4000-8000-000000000001',
        expiresAt: '2026-08-10T12:15:00Z',
        totalRows: 2,
        validRows: 1,
        invalidRows: 1,
        rows: [
          { rowNumber: 2, normalized: { siteCode: 'BKK', name: 'Good PLC', address: '192.0.2.60', hostname: null, deviceType: 'PLC', area: null, owner: null, criticality: 'Normal', tags: [] }, errors: [] },
          { rowNumber: 3, normalized: null, errors: [{ field: 'address', code: 'validation', message: 'Address is invalid.' }] },
        ],
      });
    }
    if (url.pathname === '/api/v1/devices/import/commit') {
      expect(request.postDataJSON()).toEqual({ previewToken: '30000000-0000-4000-8000-000000000001' });
      return json(route, {
        previewToken: '30000000-0000-4000-8000-000000000001',
        created: 1,
        skipped: 1,
        deviceIds: ['20000000-0000-4000-8000-000000000002'],
        errors: [{ rowNumber: 3, normalized: null, errors: [{ field: 'address', code: 'validation', message: 'Address is invalid.' }] }],
        alreadyCommitted: false,
      });
    }
    return json(route, { title: 'Unexpected route', status: 404 }, 404);
  });

  await signInAsEngineer(page);
  await page.getByRole('tab', { name: 'CSV import' }).click();
  await page.locator('input[type=file]').setInputFiles({
    name: 'devices.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from('siteCode,name,address,hostname,deviceType,area,owner,criticality,tags\nBKK,Good PLC,192.0.2.60,,PLC,,,Normal,'),
  });
  await page.getByRole('button', { name: 'Preview import' }).click();
  await expect(page.getByText(/Invalid: 1 error/)).toBeVisible();
  await expect(page.getByText(/Address is invalid/)).toBeVisible();
  await page.getByRole('button', { name: 'Commit 1 valid rows' }).click();
  await expect(page.getByText(/Created 1 Devices; skipped 1 rows/)).toBeVisible();
});

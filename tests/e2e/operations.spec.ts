import { expect, test, type Page } from '../../src/web/node_modules/@playwright/test/index.js';
import { emptySummary, incident, site, summary } from '../../src/web/tests/fixtures/operations';

const evidence = process.env.UI_EVIDENCE;
async function capture(page: Page, name: string) {
  if (evidence) await page.screenshot({ path: `${evidence}/${name}.png`, fullPage: true });
}
async function openOperations(page: Page) {
  await page.goto('/');
  await page.getByRole('button', { name: 'Use synthetic role' }).click();
  await page.getByRole('button', { name: 'Operations overview' }).click();
}

test('mocked desktop snapshot, site filter, read-only detail, keyboard focus, and logout isolation', async ({ page }) => {
  const calls: string[] = [];
  await page.route('**/api/v1/**', async route => {
    expect(route.request().method()).toBe('GET');
    expect(route.request().headers()['x-ee-pulse-role']).toBe('Viewer');
    const url = new URL(route.request().url());
    calls.push(url.pathname + url.search);
    const body = url.pathname.includes('/sites') ? { items: [site], page: 1, pageSize: 200, totalCount: 1 }
      : url.pathname.includes('/dashboard/summary') ? summary
        : url.pathname.includes('/incidents/') ? incident : { items: [], page: 1, pageSize: 20, totalCount: 0 };
    await route.fulfill({ json: body });
  });
  await openOperations(page);
  await expect(page.getByRole('table')).toBeVisible();
  await capture(page, 'desktop-populated');
  await page.getByRole('combobox', { name: 'Site', exact: true }).click();
  await page.getByRole('option', { name: site.name }).click();
  await expect.poll(() => calls.some(url => url.includes(`siteId=${site.id}`))).toBe(true);
  const view = page.getByRole('button', { name: `View incident for ${incident.deviceName}`, exact: true });
  await view.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: /Incident details/ });
  await expect(dialog.getByText('availability', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('button')).toHaveCount(1);
  await capture(page, 'desktop-incident-detail');
  await page.keyboard.press('Escape');
  await expect(dialog).not.toBeVisible();
  await expect(view).toBeFocused();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Development access' })).toBeVisible();
  await expect(page.getByText(incident.deviceName, { exact: true })).toHaveCount(0);
});

test('mocked loading, stale refresh, dependency retry, empty snapshot and mobile layout', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  let resolveSnapshot: (() => void) | undefined;
  let mode: 'pending' | 'data' | 'failure' | 'empty' = 'pending';
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.includes('/sites')) return route.fulfill({ json: { items: [site], page: 1, pageSize: 200, totalCount: 1 } });
    if (path.includes('/incidents/')) return route.fulfill({ json: incident });
    if (!path.includes('/dashboard/summary')) return route.fulfill({ json: { items: [], page: 1, pageSize: 20, totalCount: 0 } });
    if (mode === 'pending') await new Promise<void>(resolve => { resolveSnapshot = resolve; });
    await route.fulfill(mode === 'failure' ? { status: 503, json: { title: 'Unavailable' } } : { json: mode === 'empty' ? emptySummary : summary });
  });
  await openOperations(page);
  await expect(page.getByText('Loading operations snapshot', { exact: true })).toBeVisible();
  await capture(page, 'mobile-loading');
  mode = 'data';
  resolveSnapshot!();
  await expect(page.getByRole('table')).toBeVisible();
  await capture(page, 'mobile-populated');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.getByRole('button', { name: `View incident for ${incident.deviceName}`, exact: true }).click();
  await expect(page.getByRole('dialog', { name: /Incident details/ }).getByText('availability', { exact: true })).toBeVisible();
  await capture(page, 'mobile-incident-detail');
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  mode = 'failure';
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByText(/may be stale/)).toBeVisible();
  await capture(page, 'mobile-stale-refresh');
  mode = 'empty';
  await page.getByRole('button', { name: 'Retry snapshot' }).click();
  await expect(page.getByText('No open incidents in this snapshot')).toBeVisible();
  await expect(page.getByText(/may be stale/)).toHaveCount(0);
  await capture(page, 'mobile-empty');
});

test('mocked authorization and dependency errors never become a healthy empty state', async ({ page }) => {
  let status = 403;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    await route.fulfill(path.includes('/dashboard/summary') ? { status, json: { title: 'Failure' } } : { json: { items: [], page: 1, pageSize: 200, totalCount: 0 } });
  });
  await openOperations(page);
  await expect(page.getByText(/Your role cannot access/)).toBeVisible();
  await capture(page, 'desktop-forbidden');
  await expect(page.getByText('No open incidents in this snapshot')).toHaveCount(0);
  status = 503;
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(page.getByText(/service is temporarily unavailable/)).toBeVisible();
  await capture(page, 'desktop-dependency-error');
});

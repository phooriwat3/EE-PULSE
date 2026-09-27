import { expect, test, type APIRequestContext, type Page, type Response } from '../../../src/web/node_modules/@playwright/test/index.js';
import type { DashboardSummaryResponse, IncidentResponse } from '../../../src/web/src/api/generated';

const evidence = process.env.UI_EVIDENCE!;
const north = '00000000-0000-4000-8000-000000000001';
const south = '00000000-0000-4000-8000-000000000002';
const empty = '00000000-0000-4000-8000-000000000003';
const incidentId = '00000000-0000-4000-8000-00000000001e';
const roles = ['Viewer', 'Operator', 'Engineer', 'Administrator', 'Auditor'] as const;

async function control(request: APIRequestContext, operation: string) {
  const response = await request.post(`${process.env.UI_REAL_CONTROL_URL}/${operation}`, {
    headers: { Authorization: `Bearer ${process.env.UI_REAL_CONTROL_TOKEN}` },
  });
  expect(response.status(), await response.text()).toBe(204);
}
function summaryResponse(page: Page, siteId?: string) {
  return page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.pathname === '/api/v1/dashboard/summary' && (siteId === undefined || url.searchParams.get('siteId') === siteId);
  });
}
async function summaryBody(response: Response) {
  expect(response.status()).toBe(200);
  expect(response.headers()['etag']).toMatch(/^".+"$/);
  return await response.json() as DashboardSummaryResponse;
}
async function signIn(page: Page, role: typeof roles[number] = 'Viewer') {
  await page.getByRole('combobox', { name: 'Role', exact: true }).click();
  await page.getByRole('option', { name: role, exact: true }).click();
  const response = summaryResponse(page);
  await page.getByRole('button', { name: 'Use synthetic role' }).click();
  await page.getByRole('button', { name: 'Operations overview', exact: true }).click();
  return summaryBody(await response);
}
async function selectSite(page: Page, name: string, id: string) {
  const response = summaryResponse(page, id);
  await page.getByRole('combobox', { name: 'Site', exact: true }).click();
  await page.getByRole('option', { name, exact: true }).click();
  return summaryBody(await response);
}
async function capture(page: Page, name: string) {
  await page.screenshot({
    path: `${evidence}/real-${name}.png`,
    fullPage: await page.getByRole('dialog').count() === 0,
    animations: 'disabled',
  });
}

test.beforeEach(async ({ request, page }) => {
  await control(request, 'database-on');
  await control(request, 'reset');
  await page.goto('/');
});
test.afterEach(async ({ request, page }, info) => {
  await control(request, 'database-on');
  const responses = await page.evaluate(() => performance.getEntriesByType('resource')
    .map(entry => entry.name).filter(name => new URL(name).pathname.startsWith('/api/')));
  await info.attach('actual-api-resource-urls', { body: JSON.stringify(responses), contentType: 'application/json' });
});

test('real populated/filter/empty/detail and preserved inventory reads for every supported role', async ({ page }, info) => {
  const methods: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) methods.push(request.method()); });
  for (const role of roles) {
    const body = await signIn(page, role);
    expect(body.statusCounts.find(row => row.status === 'Down')?.count).toBe(2);
    expect(body.statusCounts.find(row => row.status === 'Up')?.count).toBe(1);
    expect(body.openIncidents.map(row => row.deviceName)).toEqual(['North press', 'South press']);
    expect(body.offlineAgents.map(row => row.agentName)).toEqual(['Acceptance offline agent']);
    await expect(page.getByRole('table')).toContainText('North press');
    await expect(page.getByRole('table')).toContainText('South press');
    if (role !== 'Viewer') {
      const response = page.waitForResponse(value => new URL(value.url()).pathname === `/api/v1/incidents/${incidentId}`);
      await page.getByRole('button', { name: 'View incident for North press', exact: true }).click();
      expect((await response).status()).toBe(200);
      await expect(page.getByRole('dialog', { name: /Incident details/ }).getByRole('heading', { name: 'North press' })).toBeVisible();
      await page.getByRole('button', { name: 'Close', exact: true }).click();
    }
    if (role === 'Viewer') {
      await capture(page, 'desktop-populated');
      const filtered = await selectSite(page, 'North assembly', north);
      expect(filtered.appliedFilter.siteId).toBe(north);
      expect(filtered.openIncidents.map(row => row.deviceName)).toEqual(['North press']);
      expect(filtered.statusCounts.find(row => row.status === 'Down')?.count).toBe(1);
      await expect(page.getByRole('table')).not.toContainText('South press');
      const detailResponse = page.waitForResponse(response => new URL(response.url()).pathname === `/api/v1/incidents/${incidentId}`);
      const view = page.getByRole('button', { name: 'View incident for North press', exact: true });
      await view.focus(); await page.keyboard.press('Enter');
      const detail = await detailResponse;
      expect(detail.status()).toBe(200);
      const incident = await detail.json() as IncidentResponse;
      expect(incident.id).toBe(incidentId); expect(incident.ruleKey).toBe('availability-down'); expect(incident.status).toBe('Open');
      const dialog = page.getByRole('dialog', { name: /Incident details/ });
      await expect(dialog.getByRole('heading', { name: 'North press' })).toBeVisible();
      await expect(dialog.getByText('availability-down', { exact: true })).toBeVisible();
      await capture(page, 'desktop-detail');
      await info.attach('actual-summary-and-detail', { body: JSON.stringify({ filtered, incident }), contentType: 'application/json' });
      await page.keyboard.press('Escape'); await expect(view).toBeFocused();
      const emptyBody = await selectSite(page, 'Empty acceptance site', empty);
      expect(emptyBody.statusCounts.every(row => row.count === 0)).toBe(true);
      expect(emptyBody.openIncidents).toEqual([]); expect(emptyBody.recentlyDown).toEqual([]); expect(emptyBody.offlineAgents).toEqual([]);
      await expect(page.getByText('No open incidents in this snapshot')).toBeVisible();
      await capture(page, 'desktop-empty');
      const devices = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/devices');
      await page.getByRole('button', { name: 'Equipment inventory', exact: true }).click();
      expect((await devices).status()).toBe(200);
      await expect(page.getByRole('heading', { name: 'North press', exact: true })).toBeVisible();
      await expect(page.getByRole('heading', { name: 'South press', exact: true })).toBeVisible();
      await capture(page, 'inventory-read');
    }
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Development access' })).toBeVisible();
    await expect(page.getByText('North press', { exact: true })).toHaveCount(0);
  }
  // All supported UI roles are readers; denial is anonymous / unsupported role, not an invented restriction.
  const denials = await page.evaluate(async () => {
    const results: number[] = [];
    for (const path of ['/api/v1/dashboard/summary', '/api/v1/incidents/00000000-0000-4000-8000-00000000001e']) {
      results.push((await fetch(path)).status);
      results.push((await fetch(path, { headers: { 'X-EE-Pulse-Role': 'Guest' } })).status);
    }
    return results;
  });
  expect(denials).toEqual([401, 403, 401, 403]);
  expect(methods.every(method => method === 'GET')).toBe(true);
  await info.attach('actual-denials-and-methods', { body: JSON.stringify({ denials, methods }), contentType: 'application/json' });
});

test('real database change, manual refresh, dependency failure/recovery, and new-session isolation', async ({ page, request }, info) => {
  await signIn(page);
  await control(request, 'rename');
  await expect(page.getByRole('table')).toContainText('North press');
  await expect(page.getByRole('table')).not.toContainText('North press updated');
  const refreshed = summaryResponse(page);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  expect((await summaryBody(await refreshed)).openIncidents[0].deviceName).toBe('North press updated');
  await expect(page.getByRole('table')).toContainText('North press updated');
  await control(request, 'database-off');
  const failure = summaryResponse(page);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  const unavailable = await failure;
  expect(unavailable.status()).toBe(503);
  expect((await unavailable.json()).code).toBe('dashboard-summary-unavailable');
  await expect(page.getByText(/Previously loaded data is shown below and may be stale/)).toBeVisible();
  await expect(page.getByRole('table')).toContainText('North press updated');
  await capture(page, 'desktop-stale-dependency');
  await info.attach('actual-dependency-problem', { body: await unavailable.body(), contentType: 'application/json' });
  await control(request, 'database-on');
  const recovery = summaryResponse(page);
  await page.getByRole('button', { name: 'Retry snapshot', exact: true }).click();
  await summaryBody(await recovery);
  await expect(page.getByText(/may be stale/)).toHaveCount(0);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page.getByText('North press updated', { exact: true })).toHaveCount(0);
  await control(request, 'reset');
  const nextSession = await signIn(page, 'Auditor');
  expect(nextSession.openIncidents[0].deviceName).toBe('North press');
  await expect(page.getByRole('table')).not.toContainText('North press updated');
  await expect(page.getByRole('table')).toContainText('North press');
});

test('real mobile snapshot, South site isolation, detail and empty state', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(page, 'Operator');
  await expect(page.getByRole('table')).toBeVisible();
  await capture(page, 'mobile-populated');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const filtered = await selectSite(page, 'South assembly', south);
  expect(filtered.appliedFilter.siteId).toBe(south);
  expect(filtered.openIncidents.map(row => row.deviceName)).toEqual(['South press']);
  await expect(page.getByRole('table')).not.toContainText('North press');
  await page.getByRole('button', { name: 'View incident for South press', exact: true }).click();
  await expect(page.getByRole('dialog', { name: /Incident details/ }).getByRole('heading', { name: 'South press' })).toBeVisible();
  await capture(page, 'mobile-detail');
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  await selectSite(page, 'Empty acceptance site', empty);
  await expect(page.getByText('No open incidents in this snapshot')).toBeVisible();
  await capture(page, 'mobile-empty');
});

import { expect, test } from '../../src/web/node_modules/@playwright/test/index.js';

test('production bundle denies access without OIDC and sends no API or synthetic auth request', async ({ page }) => {
  const requests: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/')) requests.push(request.url());
    expect(request.headers()['x-ee-pulse-role']).toBeUndefined();
    expect(request.headers()['x-ee-pulse-actor']).toBeUndefined();
  });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Authentication required' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Role' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Use synthetic role' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Operations overview' })).toHaveCount(0);
  expect(requests).toEqual([]);
  if (process.env.UI_EVIDENCE) await page.screenshot({ path: `${process.env.UI_EVIDENCE}/production-authentication.png`, fullPage: true });
});

import { expect, test, type APIRequestContext, type APIResponse, type Page, type Response } from '../../../src/web/node_modules/@playwright/test/index.js';
import { writeFile } from 'node:fs/promises';

const north = '00000000-0000-4000-8000-00000000001e';
const south = '00000000-0000-4000-8000-00000000001f';
const actor = '00000000-0000-4000-8000-000000000002';
const evidence = process.env.UI_EVIDENCE!;
async function control(request: APIRequestContext, operation: string) {
  expect((await request.post(`${process.env.UI_REAL_CONTROL_URL}/${operation}`, { headers: { Authorization: `Bearer ${process.env.UI_REAL_CONTROL_TOKEN}` } })).status()).toBe(204);
}
async function signIn(page: Page, role = 'Operator') {
  await page.goto('/'); await page.getByRole('combobox', { name: 'Role', exact: true }).click();
  await page.getByRole('option', { name: role, exact: true }).click(); await page.getByRole('button', { name: 'Use synthetic role' }).click();
  await page.getByRole('button', { name: 'Operations overview', exact: true }).click();
  await expect(page.getByRole('table')).toContainText('North press');
}
async function detail(page: Page, name: string) {
  await page.getByRole('button', { name: `View incident for ${name}`, exact: true }).click();
  await expect(page.getByRole('dialog', { name: /Incident details/ }).getByRole('heading', { name })).toBeVisible();
}
async function choose(page: Page, name: string) {
  await page.getByRole('combobox', { name: 'Action', exact: true }).click(); await page.getByRole('option', { name, exact: true }).click();
}
function commandResponse(page: Page, kind: string) {
  return page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname.endsWith(`/${kind}`));
}
async function record(response: Response | APIResponse) {
  return { status: response.status(), contentType: response.headers()['content-type'], etag: response.headers()['etag'], body: (await response.body()).toString('utf8') };
}
async function exactReplay(page: Page, request: APIRequestContext, fresh: Response) {
  const sent = fresh.request();
  const original = await record(fresh);
  const replay = await request.post(sent.url(), { headers: sent.headers(), data: sent.postData()! });
  expect(await record(replay)).toEqual(original);
  await writeFile(`${evidence}/replay-${new URL(sent.url()).pathname.split('/').pop()}.json`, JSON.stringify({ original, replay: await record(replay), key: sent.headers()['idempotency-key'] }, null, 2));
  await expect(page.getByText(/Request confirmed/)).toBeVisible();
}
test.afterEach(async ({ request }) => { await control(request, 'database-on'); });

test('real role visibility and authoritative command denial without mutation', async ({ page, request }) => {
  for (const role of ['Viewer', 'Engineer', 'Auditor', 'Operator', 'Administrator']) {
    await signIn(page, role); await detail(page, 'North press');
    if (role === 'Operator' || role === 'Administrator') await expect(page.getByRole('button', { name: 'Submit comment' })).toBeEnabled();
    else {
      await expect(page.getByText(/Read-only access/)).toBeVisible();
      expect((await request.post(`/api/v1/incidents/${north}/comments`, { headers: { 'X-EE-Pulse-Role': role, 'X-EE-Pulse-Actor': actor }, data: { comment: 'must not persist' } })).status()).toBe(403);
    }
    await page.getByRole('button', { name: 'Close', exact: true }).click(); await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  }
  expect((await request.post(`/api/v1/incidents/${north}/comments`, { data: { comment: 'must not persist' } })).status()).toBe(401);
});

test('real commands, persisted success, replay, stale state, dependency recovery and lost-response exact retry', async ({ page, request }) => {
  await signIn(page); await detail(page, 'North press');
  await page.getByRole('textbox', { name: 'Comment', exact: true }).fill('  UI comment 😀  ');
  await page.screenshot({ path: `${evidence}/commands-desktop-comment.png` });
  let waiting = commandResponse(page, 'comments');
  await page.getByRole('button', { name: 'Submit comment' }).focus(); await page.keyboard.press('Enter');
  const comment = await waiting; expect(comment.status()).toBe(201); expect((await comment.json()).comment).toBe('UI comment 😀');
  expect(comment.headers()['etag']).not.toBe(comment.request().headers()['if-match']);
  await exactReplay(page, request, comment);

  // Database outage is confined to this owned fixture; identical intent is explicitly retried after recovery.
  await page.getByRole('textbox', { name: 'Comment', exact: true }).fill('Dependency retry');
  await expect(page.getByRole('button', { name: 'Submit comment' })).toBeEnabled(); await control(request, 'database-off');
  waiting = commandResponse(page, 'comments'); await page.getByRole('button', { name: 'Submit comment' }).click();
  const unavailable = await waiting; expect(unavailable.status()).toBe(503);
  const unavailableRequest = unavailable.request();
  await expect(page.getByText(/service is temporarily unavailable/)).toBeVisible(); await control(request, 'database-on');
  waiting = commandResponse(page, 'comments'); await page.getByRole('button', { name: 'Retry saved request' }).click();
  const recovered = await waiting; expect(recovered.status()).toBe(201);
  expect(recovered.request().postData()).toBe(unavailableRequest.postData());
  expect(recovered.request().headers()['idempotency-key']).toBe(unavailableRequest.headers()['idempotency-key']);
  expect(recovered.request().headers()['if-match']).toBe(unavailableRequest.headers()['if-match']);
  await expect(page.getByText(/Request confirmed/)).toBeVisible();

  await choose(page, 'Acknowledge'); await page.getByRole('textbox', { name: 'Acknowledgement comment' }).fill(' Seen on floor ');
  waiting = commandResponse(page, 'acknowledge'); await page.getByRole('button', { name: 'Acknowledge incident' }).click();
  const acknowledgement = await waiting; expect(acknowledgement.status()).toBe(200); await exactReplay(page, request, acknowledgement);
  await expect(page.getByRole('dialog', { name: /Incident details/ }).getByText('Seen on floor', { exact: true })).toBeVisible();
  await choose(page, 'Add comment');

  // A real concurrent writer advances the current version after this browser's detail read.
  const current = await request.get(`/api/v1/incidents/${north}`, { headers: { 'X-EE-Pulse-Role': 'Operator' } });
  expect((await request.post(`/api/v1/incidents/${north}/comments`, { headers: { 'X-EE-Pulse-Role': 'Operator', 'X-EE-Pulse-Actor': actor, 'If-Match': current.headers()['etag'], 'Idempotency-Key': crypto.randomUUID() }, data: { comment: 'Concurrent update' } })).status()).toBe(201);
  await page.getByRole('textbox', { name: 'Comment', exact: true }).fill('Stale request');
  waiting = commandResponse(page, 'comments'); await page.getByRole('button', { name: 'Submit comment' }).click();
  expect((await waiting).status()).toBe(412); await expect(page.getByText(/This incident changed/)).toBeVisible();
  await page.getByRole('button', { name: 'Refresh incident', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Submit comment' })).toBeEnabled();
  waiting = commandResponse(page, 'comments'); await page.getByRole('button', { name: 'Submit comment' }).click(); expect((await waiting).status()).toBe(201);
  await expect(page.getByText(/Request confirmed/)).toBeVisible();

  // Transport fault only: forward the actual command through Vite/API/Postgres, then drop its committed response.
  // No invented API response and no production test route. Retry reaches the actual durable receipt.
  let dropped: Awaited<ReturnType<typeof record>> | undefined;
  let lostHeaders: Record<string, string> | undefined; let lostBody: string | null = null;
  await page.route(`**/api/v1/incidents/${north}/comments`, async route => {
    if (route.request().method() !== 'POST') return route.continue();
    lostHeaders = route.request().headers(); lostBody = route.request().postData();
    const committed = await route.fetch(); expect(committed.status()).toBe(201); dropped = await record(committed);
    await route.abort('failed');
  }, { times: 1 });
  await page.getByRole('textbox', { name: 'Comment', exact: true }).fill(' Lost response 😀 ');
  await page.getByRole('button', { name: 'Submit comment' }).click();
  await expect(page.getByText(/outcome is not confirmed/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Close', exact: true })).toBeDisabled();
  await page.screenshot({ path: `${evidence}/commands-desktop-uncertain.png` });
  waiting = commandResponse(page, 'comments'); await page.getByRole('button', { name: 'Retry saved request' }).click();
  const replay = await waiting; expect(await record(replay)).toEqual(dropped);
  expect(replay.request().postData()).toBe(lostBody); expect(replay.request().headers()['idempotency-key']).toBe(lostHeaders!['idempotency-key']);
  expect(replay.request().headers()['if-match']).toBe(lostHeaders!['if-match']);
  await writeFile(`${evidence}/lost-response-replay.json`, JSON.stringify({ committed: dropped, replay: await record(replay), key: lostHeaders!['idempotency-key'] }, null, 2));
  const reused = await request.post(`/api/v1/incidents/${north}/comments`, { headers: { 'X-EE-Pulse-Role': 'Operator', 'X-EE-Pulse-Actor': actor, 'If-Match': lostHeaders!['if-match'], 'Idempotency-Key': lostHeaders!['idempotency-key'] }, data: { comment: 'Different request' } });
  expect(reused.status()).toBe(409); expect((await reused.json()).code).toBe('idempotency-key-reuse-conflict');
  await expect(page.getByText(/Request confirmed/)).toBeVisible();
  // Original acknowledgement replay still returns its original ETag after later mutations.
  await exactReplay(page, request, acknowledgement);
  await page.getByRole('button', { name: 'Close', exact: true }).click();

  await detail(page, 'South press'); await choose(page, 'Resolve');
  await page.getByRole('textbox', { name: 'Resolution note' }).fill(' Verified safe recovery ');
  await expect(page.getByRole('button', { name: 'Review resolution' })).toBeEnabled();
  await control(request, 'resolution-blocked');
  await page.getByRole('button', { name: 'Review resolution' }).click();
  await expect(page.getByRole('dialog', { name: 'Confirm incident resolution' }).getByRole('button', { name: 'Back' })).toBeFocused();
  waiting = commandResponse(page, 'resolve'); await page.getByRole('button', { name: 'Confirm resolution' }).click();
  expect((await waiting).status()).toBe(409); await expect(page.getByText(/Resolution is not allowed/)).toBeVisible();
  await page.getByRole('button', { name: 'Refresh incident', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Review resolution' })).toBeDisabled();
  await control(request, 'resolution-permitted'); await page.getByRole('button', { name: 'Refresh incident', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Review resolution' })).toBeEnabled();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: `${evidence}/commands-mobile-resolution.png` });
  await page.getByRole('button', { name: 'Review resolution' }).click();
  await expect(page.getByRole('dialog', { name: 'Confirm incident resolution' }).getByRole('button', { name: 'Back' })).toBeFocused();
  await page.screenshot({ path: `${evidence}/commands-mobile-confirmation.png` });
  waiting = commandResponse(page, 'resolve'); await page.getByRole('button', { name: 'Confirm resolution' }).click();
  const resolved = await waiting; expect(resolved.status()).toBe(200); await exactReplay(page, request, resolved);
  await expect(page.getByRole('dialog', { name: /Incident details/ }).getByText('Verified safe recovery', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('table')).not.toContainText('South press');
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await signIn(page, 'Viewer'); await detail(page, 'North press');
  await expect(page.getByText(/Read-only access/)).toBeVisible(); await expect(page.getByText('Lost response 😀', { exact: true })).toHaveCount(0);
});

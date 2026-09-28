import { defineConfig, devices } from '@playwright/test';

const port = Number(process.env.UI_REAL_WEB_PORT);
const evidence = process.env.UI_EVIDENCE;
if (!evidence || !Number.isInteger(port) || port < 1024 || port > 65535 || !process.env.UI_REAL_API_URL
  || !process.env.UI_REAL_CONTROL_URL || !process.env.UI_REAL_CONTROL_TOKEN) {
  throw new Error('Run this suite through OperationsBrowserAcceptanceTests, with its isolated fixture environment.');
}
const url = `http://127.0.0.1:${port}`;
export default defineConfig({
  testDir: '../../tests/e2e/real-backend',
  testMatch: process.env.UI_REAL_COMMANDS === 'true' ? '**/commands.spec.ts' : '**/operations.spec.ts',
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 60_000,
  reporter: [['line'], ['json', { outputFile: `${evidence}/real-browser.json` }]],
  outputDir: `${evidence}/real-playwright`,
  use: { ...devices['Desktop Chrome'], baseURL: url, trace: 'retain-on-failure' },
  webServer: {
    command: `npm run dev -- --config vite.real-backend.config.ts --host 127.0.0.1 --port ${port} --strictPort`,
    url,
    reuseExistingServer: false,
    timeout: 60_000,
  },
});

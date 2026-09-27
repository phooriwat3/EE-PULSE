import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '../../tests/e2e',
  fullyParallel: false,
  retries: 0,
  reporter: 'line',
  use: {
    baseURL: 'http://127.0.0.1:4174',
    trace: 'retain-on-failure',
  },
  webServer: [{
    command: 'npm run dev -- --host 127.0.0.1 --port 4174',
    url: 'http://127.0.0.1:4174',
    reuseExistingServer: false,
    timeout: 120_000,
  }, {
    command: 'npm run preview -- --host 127.0.0.1 --port 4175',
    url: 'http://127.0.0.1:4175',
    reuseExistingServer: false,
    timeout: 120_000,
  }],
  projects: [
    { name: 'chromium', testIgnore: '**/production-auth.spec.ts', use: { ...devices['Desktop Chrome'] } },
    { name: 'production-auth', testMatch: '**/production-auth.spec.ts', use: { ...devices['Desktop Chrome'], baseURL: 'http://127.0.0.1:4175' } },
  ],
});

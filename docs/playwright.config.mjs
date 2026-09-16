import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './browser-tests',
  fullyParallel: true,
  workers: 2,
  timeout: 120_000,
  expect: { timeout: 30_000 },
  retries: 0,
  reporter: 'list',
  outputDir: '../artifacts/browser-results',
  use: {
    browserName: 'chromium',
    baseURL: 'http://127.0.0.1:4322',
    viewport: { width: 1280, height: 1000 },
    actionTimeout: 30_000,
    navigationTimeout: 60_000,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'pnpm preview --host 127.0.0.1 --port 4322',
    // Keep Astro's agent detection from detaching the server that Playwright owns.
    env: { ASTRO_PREVIEW_BACKGROUND: '1' },
    url: 'http://127.0.0.1:4322/try/',
    reuseExistingServer: false,
    timeout: 30_000,
  },
});

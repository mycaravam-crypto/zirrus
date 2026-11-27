import { defineConfig, devices } from "@playwright/test";

// End-to-end tests against the running Compose stack (CI compose job): Caddy on http://localhost,
// Keycloak test realm, real worker, renderer and ClamAV. Not part of `pnpm test`.
export default defineConfig({
  testDir: "e2e",
  timeout: 10 * 60_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  use: {
    baseURL: process.env.ZIRRUS_E2E_BASE_URL ?? "http://localhost",
    trace: "retain-on-failure",
    acceptDownloads: true,
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});

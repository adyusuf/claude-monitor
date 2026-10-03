import { defineConfig, devices } from "@playwright/test";
import { e2e } from "./config";

// Against E2E_BASE_URL (the test environment) when set; otherwise against a local stack that serve-local.sh starts.
export default defineConfig({
  testDir: "specs",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: { baseURL: e2e.baseUrl, trace: "retain-on-failure", locale: "en-GB" },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } },
  ],
  webServer: e2e.local
    ? { command: "bash serve-local.sh", url: `${e2e.baseUrl}/api/version`, timeout: 240_000, reuseExistingServer: !process.env.CI }
    : undefined,
});

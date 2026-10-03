"use strict";
const { defineConfig, devices } = require("@playwright/test");
const { PORT, BASE_URL, PYTHON } = require("./config.js");

// One real board server on an isolated registry and a seeded temporary project (serve.py), so the suite
// never touches ~/.cache/claude-board or a real board. Serial: the specs change the shared seeded board
// (remove a task, queue a message), each on rows of its own.
module.exports = defineConfig({
  testDir: "./specs",
  fullyParallel: false,
  workers: 1,
  retries: 0,            // a flaky test is fixed, not retried (global: no retry to hide flakiness)
  timeout: 30000,
  reporter: [["list"]],
  use: { baseURL: BASE_URL, trace: "retain-on-failure" },
  // WebKit is the engine of the macOS desktop window (Tauri's WKWebView) and of Safari: the page runs in both.
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } },
  ],
  webServer: {
    command: `${PYTHON} serve.py`,
    url: `${BASE_URL}/api/projects`,
    env: { E2E_PORT: String(PORT) },
    reuseExistingServer: false,
    timeout: 30000,
  },
});

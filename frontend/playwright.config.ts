import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  timeout: 60000,
  expect: { timeout: 15000 },
  use: {
    baseURL: process.env["PLAYWRIGHT_BASE_URL"] || "http://127.0.0.1:4200",
    viewport: { width: 1440, height: 1000 },
    trace: "retain-on-failure",
  },
  reporter: "list",
});

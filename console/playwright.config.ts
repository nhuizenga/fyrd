import { defineConfig, devices } from "@playwright/test";

const PORT = 5199;

/** Browser tests run against the Vite dev server; each spec mocks `/api/*`, so no Fyrd server or Claude login is needed. */
export default defineConfig({
  testDir: "e2e",
  fullyParallel: true,
  reporter: [["list"]],
  use: { launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH || undefined }, baseURL: `http://127.0.0.1:${PORT}`, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } }],
  webServer: {
    command: `npx vite --port ${PORT} --strictPort --host 127.0.0.1`,
    url: `http://127.0.0.1:${PORT}`,
    reuseExistingServer: !process.env.CI,
  },
});

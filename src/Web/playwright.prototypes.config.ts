import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./prototype-tests",
  fullyParallel: true,
  workers: 2,
  retries: 0,
  reporter: "list",
  outputDir: "./test-results/prototypes",
  use: {
    baseURL: "http://127.0.0.1:5418",
    ...(process.platform === "win32" ? { channel: "msedge" } : {}),
  },
  projects: [
    { name: "desktop-light", use: { ...devices["Desktop Chrome"], colorScheme: "light" } },
    { name: "desktop-dark", use: { ...devices["Desktop Chrome"], colorScheme: "dark" } },
    { name: "mobile-light", use: { ...devices["Pixel 7"], colorScheme: "light" } },
    { name: "mobile-dark", use: { ...devices["Pixel 7"], colorScheme: "dark" } },
  ],
  webServer: {
    command: 'python -m http.server 5418 --bind 127.0.0.1 --directory "..\\..\\prototypes"',
    url: "http://127.0.0.1:5418",
    reuseExistingServer: false,
  },
});

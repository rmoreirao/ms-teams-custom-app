import { defineConfig, devices } from "@playwright/test";
const port = Number(process.env.REFERENCE_TEST_PORT ?? 5367);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("REFERENCE_TEST_PORT must be an integer from 1024 to 65535.");
const localOrigin = `http://localhost:${port}`;
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: localOrigin,
    trace: "off",
    screenshot: "only-on-failure",
    ...(process.platform === "win32" ? { channel: "msedge" } : {}),
  },
  projects: [{ name: "desktop", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: `npm run build -- --outDir dist && vite preview --outDir dist --host localhost --port ${port} --strictPort`,
    url: localOrigin,
    reuseExistingServer: false,
  },
});

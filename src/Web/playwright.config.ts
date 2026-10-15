import { defineConfig, devices } from "@playwright/test";
import { pbkdf2Sync, randomBytes } from "node:crypto";
import { resolve } from "node:path";

const password = process.env.REFERENCE_TEST_PASSWORD ?? randomBytes(32).toString("hex");
const salt = randomBytes(16);
process.env.REFERENCE_TEST_PASSWORD = password;
const external = process.env.REFERENCE_BASE_URL;
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: external ?? "http://localhost:5367",
    trace: "off",
    screenshot: "only-on-failure",
    ...(process.platform === "win32" ? { channel: "msedge" } : {}),
  },
  projects: [{ name: "desktop", use: { ...devices["Desktop Chrome"] } }],
  webServer: external ? undefined : {
    command: `dotnet run --project "${resolve(import.meta.dirname, "..", "Teams.Host", "Teams.Host.csproj")}" --no-launch-profile --no-build --configuration ${process.env.DOTNET_CONFIGURATION ?? "Debug"} --urls http://localhost:5367`,
    url: "http://localhost:5367/health/ready",
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: "Development",
      LocalAuth__PasswordHash: pbkdf2Sync(password, salt, 210000, 32, "sha256").toString("hex"),
      LocalAuth__PasswordSalt: salt.toString("hex"),
    },
  },
});

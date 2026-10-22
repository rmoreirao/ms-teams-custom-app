import { test, expect } from "./live-fixture";
import type { CacheCleanup, CacheSnapshot } from "../src/api";

for (const scenario of [
  { name: "desktop both stores", width: 1280, height: 900, dark: false, cosmos: true, redis: true, capture: true },
  { name: "mobile both stores", width: 390, height: 844, dark: true, cosmos: true, redis: true, capture: true },
  { name: "Redis only", width: 1280, height: 900, dark: false, cosmos: false, redis: true, capture: false },
  { name: "Cosmos only", width: 1280, height: 900, dark: false, cosmos: true, redis: false, capture: false },
]) {
  test(`cache inspection, deliberate cleanup, and fresh-read validation: ${scenario.name}`, async ({ page, live }, testInfo) => {
    let operation: CacheCleanup | null = null;
    let cosmosMessages = 30;
    let pages = 1;
    let generation = 0;
    let revision = 1;
    let cold = false;
    let pending = false;
    let warm = true;
    let contentReads = 0;
    const commands: { operationId: string; cosmos: boolean; redis: boolean }[] = [];
    const snapshot = (): CacheSnapshot => ({
      observedAt: new Date().toISOString(), revision, generation, cosmosMessages, cosmosCountComplete: true,
      coverageComplete: !cold, readViewComplete: !cold, cosmosError: null,
      redis: { status: "available", pages, maximumTtlSeconds: pages ? 12 : 0, complete: true }, cleanup: operation,
    });
    await page.route("**/api/conversations/chat-handover/cache-diagnostics**", async route => {
      if (route.request().method() !== "POST") return route.fulfill({ json: snapshot() });
      const command = route.request().postDataJSON();
      expect(route.request().headers()["x-csrf-token"]).toBe(live.state.session!.csrf);
      commands.push(command);
      if (command.cosmos) { cosmosMessages = 0; generation++; revision += 2; cold = true; pending = true; warm = false; }
      if (command.redis) { pages = 0; warm = false; }
      operation = { id: command.operationId, cosmos: command.cosmos, redis: command.redis, targetRevision: 1,
        startedAt: new Date().toISOString(), cosmosState: command.cosmos ? "completed" : "not-selected",
        redisState: command.redis ? "completed" : "not-selected", removedMessages: command.cosmos ? 30 : 0,
        removedPages: command.redis ? 1 : 0, cosmosError: null, redisError: null, complete: true };
      await route.fulfill({ json: operation });
    });
    await page.route("**/api/conversations/chat-handover/messages**", async route => {
      if (route.request().method() !== "GET") return route.fallback();
      contentReads++;
      const access = { epoch: 1, historyFrom: new Date(Date.now() - 7 * 86400_000).toISOString(),
        validUntil: new Date(Date.now() + 300000).toISOString() };
      const diagnostics = { kind: "page", observedAt: new Date().toISOString(), hydrationRequested: pending,
        steps: [{ layer: "redis", outcome: warm ? "hit" : "miss", count: warm ? 20 : 0, milliseconds: 0.8 },
          ...(warm ? [] : [{ layer: "cosmos", outcome: "content-query", count: pending ? 0 : 20, milliseconds: 4.2 }])] };
      if (pending) {
        pending = false;
        return route.fulfill({ status: 202, json: { state: "pending", retryAfterMs: 1000, access, diagnostics } });
      }
      cold = false; cosmosMessages = 30; warm = true; pages = 1;
      return route.fulfill({ json: { messages: live.state.messages.get("chat-handover")!.slice(-20),
        cursor: null, revision, window: "test-command-window", parentMessage: null, access, diagnostics } });
    });
    await page.setViewportSize(scenario);
    await page.goto(scenario.dark ? "/?clawpilotTheme=dark" : "/");
    await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
    const composer = page.getByRole("textbox", { name: "Write a message" });
    await expect(composer).toBeEnabled();
    await composer.fill("Keep my diagnostic draft");
    const trigger = page.getByRole("button", { name: "Cache Diagnostics", exact: true });
    await trigger.click();
    const dialog = page.getByRole("dialog", { name: "Cache Diagnostics", exact: true });
    await expect(dialog.getByRole("button", { name: "Inspect storage" })).toBeEnabled();
    await expect(dialog.getByRole("button", { name: "Close", exact: true })).toBeFocused();
    await expect(dialog.getByRole("checkbox", { name: "Cosmos message copies" })).not.toBeChecked();
    await expect(dialog.getByRole("checkbox", { name: "Redis recent pages" })).not.toBeChecked();
    await expect(dialog.getByRole("button", { name: "Clean selected caches" })).toBeDisabled();
    const before = contentReads;
    await dialog.getByRole("button", { name: "Inspect storage" }).click();
    await expect(dialog.getByRole("button", { name: "Inspect storage" })).toBeEnabled();
    expect(contentReads).toBe(before);
    expect(commands).toHaveLength(0);
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(scenario.width);
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    if (scenario.capture) {
      await dialog.evaluate(element => { element.scrollTop = 0; });
      await page.screenshot({ path: testInfo.outputPath("cache-diagnostics.png") });
    }
    await page.keyboard.press("Escape");
    await expect(dialog).not.toBeVisible();
    await expect(trigger).toBeFocused();
    expect(live.state.sendRequests).toHaveLength(0);
    await trigger.click();
    await expect(dialog.getByRole("button", { name: "Inspect storage" })).toBeEnabled();
    if (scenario.cosmos) await dialog.getByRole("checkbox", { name: "Cosmos message copies" }).check();
    if (scenario.redis) await dialog.getByRole("checkbox", { name: "Redis recent pages" }).check();
    await dialog.getByRole("button", { name: "Clean selected caches" }).click();
    expect(commands).toHaveLength(0);
    await dialog.getByRole("button", { name: "Confirm cleanup" }).click();
    await expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeEnabled();
    expect(commands).toEqual([{ operationId: expect.any(String), cosmos: scenario.cosmos, redis: scenario.redis }]);
    expect(cosmosMessages).toBe(scenario.cosmos ? 0 : 30);
    expect(pages).toBe(scenario.redis ? 0 : 1);
    await dialog.getByRole("button", { name: "Reload and validate" }).click();
    await expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeEnabled({ timeout: 15000 });
    await expect(dialog.getByRole("heading", { name: "Observation history" })).toBeVisible();
    const history = dialog.getByRole("region", { name: "Observation history" });
    await expect(history.getByText("miss", { exact: true }).first()).toBeVisible();
    if (scenario.cosmos) await expect(history.getByText(
      "Background Teams hydration was accepted for processing, not confirmed complete.", { exact: true })).toBeVisible();
    await dialog.getByRole("button", { name: "Reload and validate" }).click();
    await expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeEnabled();
    await expect(history.getByText("hit", { exact: true }).first()).toBeVisible();
    await dialog.getByRole("button", { name: "Close", exact: true }).click();
    await expect(composer).toHaveValue("Keep my diagnostic draft");
    expect(live.state.sendRequests).toHaveLength(0);
  });
}

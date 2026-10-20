import { expect, test, message } from "./live-fixture";

test("a preparing chat recovers after a minute without reloading the catalog or sending a message", async ({ page }) => {
  let ready = false;
  let catalogReads = 0;
  let messageReads = 0;
  let messageSends = 0;
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (path === "/api/conversations") catalogReads++;
    if (path.endsWith("/messages") && request.method() === "POST") messageSends++;
  });
  await page.route("**/api/conversations/chat-handover/messages*", async route => {
    messageReads++;
    if (ready) await route.fallback();
    else await route.fulfill({ status: 503, json: {
      code: "projection_preparing", message: "Recent content is being synchronized. Try again.",
    } });
  });
  await page.goto("/");
  await expect(page.getByText("Live", { exact: true })).toBeVisible();
  await page.clock.install();
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await expect(page.getByRole("heading", { name: "Synchronizing recent context", exact: true })).toBeVisible();
  const catalogBefore = catalogReads;
  await page.getByRole("textbox", { name: "Write a message" }).fill("This draft must not be sent.");
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
  for (const delay of [1000, 2000, 4000, 8000, 15000, 30000]) {
    const response = page.waitForResponse(response =>
      new URL(response.url()).pathname === "/api/conversations/chat-handover/messages" && response.status() === 503);
    await page.clock.fastForward(delay);
    await response;
    await expect(page.getByText("Synchronizing recent context...", { exact: true })).toHaveCount(0);
  }
  expect(messageReads).toBe(7);
  ready = true;
  await page.clock.fastForward(30_000);
  await expect(page.getByRole("heading", { name: "Synchronizing recent context", exact: true })).toHaveCount(0);
  await expect(page.getByText("Conversation context 30.", { exact: true })).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("This draft must not be sent.");
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
  expect(messageReads).toBe(8);
  expect(catalogReads).toBe(catalogBefore);
  expect(messageSends).toBe(0);
});

test("pushed changes update only their conversation and preserve loaded history without 30-second polling", async ({ page, live }) => {
  const catalogRequests: string[] = [];
  const changesRequests: string[] = [];
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (path === "/api/conversations") catalogRequests.push(path);
    if (path.endsWith("/changes")) changesRequests.push(path);
  });
  await page.goto("/");
  await expect(page.getByText("Live", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("Conversation context 10.", { exact: true })).toBeVisible();
  const before = catalogRequests.length;
  const changed = page.waitForResponse(response => new URL(response.url()).pathname === "/api/conversations/chat-handover/changes");
  live.update("chat-handover", [...live.state.messages.get("chat-handover")!, message("200", "A pushed Teams update.")]);
  const delta = await (await changed).json();
  expect(delta.resetRequired).toBe(false);
  expect(delta.messages.some((message: { body: string }) => message.body === "A pushed Teams update.")).toBe(true);
  await expect(page.getByText("A pushed Teams update.")).toBeVisible();
  await expect(page.getByText("Conversation context 10.", { exact: true })).toBeAttached();
  expect(catalogRequests).toHaveLength(before);
  const currentChanges = changesRequests.length;
  live.update("channel-updates", [...live.state.messages.get("channel-updates")!, message("201", "Unselected conversation update.")]);
  await page.clock.install();
  await page.clock.fastForward(30_000);
  expect(catalogRequests).toHaveLength(before);
  expect(changesRequests).toHaveLength(currentChanges);
});

test("reconnect catches up missed changes while retaining loaded history and an unsent draft", async ({ page, live }) => {
  await page.goto("/");
  await expect(page.locator(".connection")).toHaveText("Live");
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Keep this draft");
  // Change server-side data without delivering its hint, as when a connection is lost.
  live.state.messages.get("chat-handover")!.push(message("202", "Recovered missed update."));
  live.state.conversations[0].revision++;
  await page.evaluate(() => {
    window.dispatchEvent(new Event("offline"));
    window.dispatchEvent(new Event("online"));
  });
  await expect(page.locator(".connection")).toHaveText("Live");
  await expect(page.getByText("Recovered missed update.", { exact: true })).toBeVisible();
  await expect(page.getByText("Conversation context 10.", { exact: true })).toBeAttached();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("Keep this draft");
  expect(live.state.sendRequests).toHaveLength(0);
});

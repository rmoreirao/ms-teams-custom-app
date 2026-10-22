import { test, expect } from "./live-fixture";

for (const viewport of [
  { name: "desktop", width: 1280, height: 900, dark: false },
  { name: "mobile", width: 390, height: 844, dark: true },
]) {
  test(`personal-chat popup, keyboard focus, and custom test messages on ${viewport.name}`, async ({ page, live }, testInfo) => {
    await page.setViewportSize(viewport);
    await page.goto(viewport.dark ? "/?clawpilotTheme=dark" : "/");
    await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
    const composer = page.getByRole("textbox", { name: "Write a message" });
    await composer.fill("Keep my personal draft");
    const trigger = page.getByRole("button", { name: "Send test messages", exact: true });
    const actions = page.locator(".conversation-header .conversation-actions");
    await expect(actions.getByRole("button", { name: "Send test messages", exact: true })).toBeVisible();
    await expect(actions.getByRole("button", { name: "Change chat", exact: true })).toBeVisible();
    expect(await page.locator(".composer-wrap").getByRole("button", { name: "Send test messages", exact: true }).count()).toBe(0);
    await trigger.click();
    const dialog = page.getByRole("dialog", { name: "Send test messages" });
    await expect(dialog.getByRole("spinbutton", { name: "Number of messages" })).toHaveValue("30");
    await expect(dialog.getByRole("spinbutton", { name: "Concurrent messages" })).toHaveValue("30");
    await expect(dialog.getByRole("textbox", { name: "Test Message" })).toHaveValue("Test message");
    await expect(dialog.getByRole("spinbutton", { name: "Number of messages" })).toBeFocused();
    await page.screenshot({ path: testInfo.outputPath(`${viewport.name}-popup.png`) });
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width);
    await page.keyboard.press("Tab");
    await expect(dialog.getByRole("spinbutton", { name: "Concurrent messages" })).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(dialog.getByRole("textbox", { name: "Test Message" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(dialog).not.toBeVisible();
    await expect(trigger).toBeFocused();
    expect(live.state.sendRequests).toHaveLength(0);

    await trigger.click();
    await dialog.getByRole("spinbutton", { name: "Number of messages" }).fill("3");
    await dialog.getByRole("textbox", { name: "Test Message" }).fill("Custom test");
    await dialog.getByRole("button", { name: "Send messages", exact: true }).click();
    await expect(dialog.getByRole("status")).toContainText("3 of 3 attempted. 3 confirmed");
    expect(live.state.sendRequests.map(item => item.body.body)).toEqual(["Custom test - 1", "Custom test - 2", "Custom test - 3"]);
    expect(new Set(live.state.sendRequests.map(item => item.body.operationId)).size).toBe(3);
    expect(live.state.sendRequests.every(item => item.path === "/api/conversations/chat-handover/messages" &&
      item.body.parentId === null && item.body.window === "test-command-window")).toBe(true);
    await dialog.getByRole("button", { name: "Close", exact: true }).click();
    await expect(composer).toHaveValue("Keep my personal draft");
    await expect(trigger).toBeFocused();
    const sent = page.locator(".message").filter({ hasText: "Custom test - 3" });
    await expect(sent.getByRole("status", { name: "Sent", exact: true })).toBeVisible();
    const bubble = await sent.locator(".message-text").boundingBox();
    const receipt = await sent.getByRole("status", { name: "Sent", exact: true }).boundingBox();
    expect(bubble).not.toBeNull();
    expect(receipt).not.toBeNull();
    expect(receipt!.x).toBeGreaterThanOrEqual(bubble!.x + bubble!.width);
    expect(receipt!.width).toBeLessThanOrEqual(16);
    await page.screenshot({ path: testInfo.outputPath(`${viewport.name}-confirmations.png`) });
  });
}

test("manual sends remain independent while confirmations arrive out of order", async ({ page }) => {
  const requests: { body: string; finish: () => Promise<void> }[] = [];
  await page.route("**/api/conversations/chat-handover/messages", async route => {
    if (route.request().method() !== "POST") return route.fallback();
    const command = route.request().postDataJSON();
    let complete!: () => void;
    const response = new Promise<void>(resolve => { complete = resolve; });
    requests.push({ body: command.body, finish: async () => {
      await route.fulfill({ status: 201, json: { id: command.operationId, state: "confirmed", messageId: command.operationId, error: null } });
      complete();
    } });
    await response;
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  const composer = page.getByRole("textbox", { name: "Write a message" });
  for (const body of ["First optimistic message", "Second optimistic message"]) {
    await composer.fill(body);
    await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
    await composer.press("Enter");
    await expect(composer).toHaveValue("");
    await expect(page.getByText(body, { exact: true })).toBeVisible();
  }
  await expect.poll(() => requests.length).toBe(2);
  expect(requests.map(item => item.body)).toEqual(["First optimistic message", "Second optimistic message"]);
  const first = page.locator(".message").filter({ hasText: "First optimistic message" });
  const second = page.locator(".message").filter({ hasText: "Second optimistic message" });
  await expect(first.getByRole("status", { name: "Sending", exact: true })).toBeVisible();
  await expect(second.getByRole("status", { name: "Sending", exact: true })).toBeVisible();
  await requests[1].finish();
  await expect(second.getByRole("status", { name: "Sent", exact: true })).toBeVisible();
  await expect(first.getByRole("status", { name: "Sending", exact: true })).toBeVisible();
  await requests[0].finish();
  await expect(first.getByRole("status", { name: "Sent", exact: true })).toBeVisible();
  expect(requests).toHaveLength(2);
});

test("stopping a test batch cancels only remaining numbered sends", async ({ page, live }) => {
  let release!: () => void;
  const response = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/conversations/chat-handover/messages", async route => {
    if (route.request().method() === "POST") await response;
    await route.fallback();
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await page.getByRole("button", { name: "Send test messages", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("spinbutton", { name: "Concurrent messages" }).fill("1");
  await dialog.getByRole("button", { name: "Send messages", exact: true }).click();
  await expect(dialog.getByRole("status")).toContainText("1 of 30 attempted.");
  await dialog.getByRole("button", { name: "Stop sending" }).click();
  release();
  await expect(dialog.getByRole("status")).toContainText("Batch stopped.");
  await expect(dialog.getByRole("status")).toContainText("29 messages were not attempted.");
  expect(live.state.sendRequests).toHaveLength(1);
});

test("the default batch submits all thirty messages before any confirmation", async ({ page }) => {
  const requests: { finish: () => Promise<void> }[] = [];
  await page.route("**/api/conversations/chat-handover/messages", async route => {
    if (route.request().method() !== "POST") return route.fallback();
    const command = route.request().postDataJSON();
    let release!: () => void;
    const response = new Promise<void>(resolve => { release = resolve; });
    requests.push({ finish: async () => {
      await route.fulfill({ status: 201, json: { id: command.operationId, state: "confirmed",
        messageId: command.operationId, error: null, attempts: 1, throttles: 0, firstStatus: 201 } });
      release();
    } });
    await response;
  });
  await page.goto("/");
  await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await page.getByRole("button", { name: "Send test messages", exact: true }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("button", { name: "Send messages", exact: true }).click();
  await expect.poll(() => requests.length).toBe(30);
  await expect(dialog.getByRole("status")).toContainText("30 of 30 attempted. 0 confirmed");
  await Promise.all(requests.map(request => request.finish()));
  await expect(dialog.getByRole("status")).toContainText("30 of 30 attempted. 30 confirmed");
  await expect(dialog.getByRole("status")).toContainText("Reported initial Graph responses: 30 accepted, 0 throttled.");
});

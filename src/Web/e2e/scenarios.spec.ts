import { test, expect, message } from "./live-fixture";
import type { Page } from "@playwright/test";

async function openChat(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toBeFocused();
}
async function submitMessage(page: Page, button: "Send" | "Reply", status: number) {
  const completed = page.waitForResponse(response => response.request().method() === "POST" &&
    new URL(response.url()).pathname.endsWith("/messages"));
  await page.getByRole("button", { name: button, exact: true }).click();
  const response = await completed;
  expect(response.status()).toBe(status);
  return response.json();
}

test("browse, page, send, preserve drafts, and sign out using the Live contract", async ({ page, live }) => {
  await openChat(page);
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("Conversation context 10.", { exact: true })).toBeVisible();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Draft stays in memory");
  await page.getByRole("button", { name: /New conversation Chat/ }).click();
  await expect(page.getByRole("heading", { name: "No messages yet" })).toBeVisible();
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("Draft stays in memory");
  expect((await submitMessage(page, "Send", 201)).state).toBe("confirmed");
  await expect(page.locator(".message").filter({ hasText: "Draft stays in memory" }).locator("time")).toBeVisible();
  expect(live.state.sendRequests).toHaveLength(1);
  expect(live.state.sendRequests[0].body.window).toBe("test-command-window");
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("link", { name: "Sign in with Microsoft" })).toBeVisible();
  await expect(page.locator(".message-text")).toHaveCount(0);
});

test("channel threads, pushed external changes, and markup safety", async ({ page, live }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
  await expect(page.getByText(/Text-safety fixture:/)).toBeVisible();
  expect(await page.locator(".message-text img, .message-text script").count()).toBe(0);
  await page.getByRole("button", { name: "Open in panel" }).nth(1).click();
  await page.getByRole("textbox", { name: "Write a reply" }).fill("A browser-validated thread reply");
  expect((await submitMessage(page, "Reply", 201)).state).toBe("confirmed");
  await expect(page.getByText("A browser-validated thread reply", { exact: true })).toBeVisible();
  expect(live.state.sendRequests[0].body.parentId).toBe("33");
  await page.getByRole("button", { name: "Close thread" }).click();
  live.update("channel-updates", [...live.state.messages.get("channel-updates")!, message("200", "An external Teams update.")]);
  await expect(page.getByText("An external Teams update.", { exact: true })).toBeVisible();
});

test("uncertainty resolves only the original operation without another send", async ({ page, live }) => {
  live.state.nextSend = "unknown";
  await openChat(page);
  await page.getByRole("textbox", { name: "Write a message" }).fill("Uncertain original operation");
  const operation = await submitMessage(page, "Send", 202);
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Change chat" }).click();
  await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Must not replay the uncertain send");
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Check original send" }).click();
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toBeVisible();
  live.state.operations.set(operation.id, { ...operation, state: "confirmed", messageId: "201" });
  live.update("chat-handover", [...live.state.messages.get("chat-handover")!, message("201", "Uncertain original operation")]);
  await page.getByRole("button", { name: "Check original send" }).click();
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Uncertain original operation", { exact: true })).toHaveCount(1);
  expect(live.state.sendRequests).toHaveLength(1);
});

test("restricted catalog and history are rendered without inventing inaccessible content", async ({ page, live }) => {
  live.state.conversations = live.state.conversations.filter(item => item.id === "chat-project");
  live.state.messages.set("chat-project", [message("31", "The team plan is ready for review.")]);
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Open chat: Opening team", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /Sam Rivera/ })).toHaveCount(0);
  await page.getByRole("button", { name: "Open chat: Opening team", exact: true }).click();
  await expect(page.getByText("The team plan is ready for review.", { exact: true })).toBeVisible();
  await expect(page.getByText("Earlier context, visible only to existing members.", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Load earlier messages" })).toHaveCount(0);
});

test("Entra is the only sign-in and no retired capability endpoint is requested", async ({ page, live }) => {
  live.state.session = null;
  const requests: string[] = [];
  page.on("request", request => requests.push(new URL(request.url()).pathname));
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Sign in with Microsoft" })).toHaveAttribute("href", "/auth/signin");
  await expect(page.getByRole("button", { name: /local|scenario/i })).toHaveCount(0);
  await expect(page.locator('input[type="password"], select')).toHaveCount(0);
  expect(requests.filter(path => /\/auth\/(?:config|local)|\/simulator\//.test(path))).toEqual([]);
});

test("offboarding clears open messages and private drafts on session verification", async ({ page, live }) => {
  await openChat(page);
  await page.getByRole("textbox", { name: "Write a message" }).fill("Private draft");
  live.state.session = null;
  await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));
  await expect(page.getByRole("link", { name: "Sign in with Microsoft" })).toBeVisible();
  await expect(page.locator(".message-text")).toHaveCount(0);
  await expect(page.getByRole("textbox")).toHaveCount(0);
});

test("rejection and offline mode preserve an editable in-page draft but never auto-retry", async ({ page, context, live }) => {
  live.state.nextSend = "rejected";
  await openChat(page);
  await page.getByRole("textbox", { name: "Write a message" }).fill("A deliberately rejected message");
  expect((await submitMessage(page, "Send", 422)).error).toContain("Teams rejected");
  await expect(page.getByText("Not sent", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Edit before a new attempt" }).click();
  await context.setOffline(true);
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("A deliberately rejected message");
  await context.setOffline(false);
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
  expect(live.state.sendRequests).toHaveLength(1);
});

for (const [outcome, status, uncertainty] of [
  ["csrf", 403, false], ["forbidden", 404, false], ["throttle", 429, false], ["gateway", 502, true],
] as const) {
  test(`${outcome} response remains explicit and is never automatically resent`, async ({ page, live }) => {
    live.state.nextSend = outcome;
    await openChat(page);
    await page.getByRole("textbox", { name: "Write a message" }).fill("One controlled request");
    await submitMessage(page, "Send", status);
    await expect(page.getByText(uncertainty ? "Outcome unknown - do not resend" : "Not sent", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
    expect(live.state.sendRequests).toHaveLength(1);
    expect(live.state.messages.get("chat-handover")).toHaveLength(30);
  });
}

test("unreadable gateway response is uncertain rather than a safe-to-resend rejection", async ({ page, live }) => {
  live.state.nextSend = "unreadable";
  await openChat(page);
  await page.getByRole("textbox", { name: "Write a message" }).fill("Unreadable result");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Check original send" })).toBeVisible();
  expect(live.state.sendRequests).toHaveLength(1);
});

test("external edits, deletions and tightened history replace previously visible content", async ({ page, live }) => {
  await openChat(page);
  live.update("chat-handover", live.state.messages.get("chat-handover")!.map(item =>
    item.id === "30" ? { ...item, body: "Edited Teams update." } : item));
  await expect(page.getByText("Edited Teams update.", { exact: true })).toBeVisible();
  live.update("chat-handover", live.state.messages.get("chat-handover")!.map(item =>
    item.id === "30" ? { ...item, deleted: true, body: "" } : item));
  await expect(page.getByText("This message was deleted.", { exact: true })).toBeVisible();
  live.state.historyFrom = message("29", "").createdAt;
  live.hint("chat-handover");
  await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));
  await expect(page.getByText("Conversation context 28.", { exact: true })).toHaveCount(0);
});

test("conversation authorization denial clears previously loaded content", async ({ page, live }) => {
  await openChat(page);
  live.state.denied.add("chat-handover");
  live.state.conversations[0].revision++;
  live.hint("chat-handover");
  await expect(page.getByRole("heading", { name: "Content is unavailable" })).toBeVisible();
  await expect(page.locator(".message-text")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
});

test("chat search has honest failure, retry and no-match states", async ({ page }) => {
  let unavailable = true;
  await page.route("**/api/conversations", async route => unavailable ?
    route.fulfill({ status: 503, json: { code: "unavailable", message: "Chat access is temporarily unavailable." } }) :
    route.fallback());
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Chats are unavailable" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "No chats available" })).toHaveCount(0);
  unavailable = false;
  await page.getByRole("button", { name: "Try chat search again" }).click();
  await expect(page.getByRole("button", { name: /^Open chat:/ })).toHaveCount(3);
  await page.getByRole("searchbox", { name: "To", exact: true }).fill("nobody@example.com");
  await expect(page.getByRole("heading", { name: "No matching chats" })).toBeVisible();
  await page.getByRole("button", { name: "Clear chat search" }).click();
  await expect(page.getByRole("searchbox", { name: "To", exact: true })).toBeFocused();
});

for (const theme of ["light", "dark"]) {
  for (const width of [1280, 390, 320]) {
    test(`Split View keyboard, drafts and responsive layout at ${width}px in ${theme}`, async ({ page, live }, testInfo) => {
      const errors: string[] = [];
      page.on("pageerror", error => errors.push(error.message));
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ reducedMotion: "reduce" });
      await page.goto(`/?clawpilotTheme=${theme}`);
      await expect(page.getByRole("searchbox", { name: "To", exact: true })).toBeFocused();
      await expect(page.getByRole("button", { name: "Close chat search" })).toBeInViewport();
      if (width > 700) await expect(page.getByText("Development · Microsoft Teams", { exact: true })).toBeVisible();
      await expect(page.locator(".app-footer")).toContainText("Development");
      await expect(page.locator(".app-footer")).toContainText("Microsoft Teams");
      const contrast = await page.evaluate(() => {
        const luminance = (value: string) => value.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(value => {
          const channel = value / 255;
          return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
        }).reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
        return [".discovery-title h1", ".discovery-header > p", ".search-hint", ".capability-note",
          ".person-copy strong", ".person-copy span", ".person-affordance", ".directory-note", "#chat-search"].map(selector => {
          const element = document.querySelector(selector)!;
          let ancestor: Element | null = element;
          while (ancestor && getComputedStyle(ancestor).backgroundColor === "rgba(0, 0, 0, 0)") ancestor = ancestor.parentElement;
          const background = luminance(getComputedStyle(ancestor!).backgroundColor);
          const foreground = luminance(getComputedStyle(element).color);
          return { selector, ratio: (Math.max(background, foreground) + 0.05) / (Math.min(background, foreground) + 0.05) };
        });
      });
      for (const sample of contrast) expect(sample.ratio, `${sample.selector} contrast`).toBeGreaterThanOrEqual(4.5);
      await page.getByRole("searchbox", { name: "To", exact: true }).fill("Sam");
      await page.keyboard.press("Tab");
      await expect(page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true })).toBeFocused();
      await page.keyboard.press("Enter");
      const composer = page.getByRole("textbox", { name: "Write a message" });
      await expect(composer).toBeFocused();
      await composer.fill("Keyboard draft");
      await composer.dispatchEvent("keydown", { key: "Enter", code: "Enter", isComposing: true });
      await composer.press("Shift+Enter");
      await expect(composer).toHaveValue("Keyboard draft\n");
      await page.getByRole("button", { name: "Change chat" }).click();
      await page.keyboard.press("Escape");
      await expect(page.getByRole("button", { name: "Change chat" })).toBeFocused();
      await expect(composer).toHaveValue("Keyboard draft\n");
      await expect(composer).toBeInViewport();
      await expect(page.getByRole("button", { name: "Send", exact: true })).toBeInViewport();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath(`split-view-${width}-${theme}.png`) });
      if (width <= 700) {
        await page.getByRole("button", { name: "Back", exact: true }).click();
        await page.getByRole("button", { name: "New chat", exact: true }).click();
        await page.keyboard.press("Escape");
        await expect(page.getByRole("button", { name: "New chat", exact: true })).toBeFocused();
      }
      expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
      expect(live.state.sendRequests).toHaveLength(0);
      expect(errors).toEqual([]);
    });
  }
}

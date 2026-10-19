import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";

let lastSendCompleted = 0;
test.beforeEach(async ({ page }) => {
  // Cases share one fixture actor; preserve its real 1.25-second send budget.
  await expect.poll(() => Date.now() - lastSendCompleted, { timeout: 5000, intervals: [100, 200] })
    .toBeGreaterThanOrEqual(1250);
  page.on("response", response => {
    if (response.request().method() === "POST" && new URL(response.url()).pathname.endsWith("/messages"))
      lastSendCompleted = Date.now();
  });
});

async function login(page: Page, actor = "alex") {
  await page.goto("/");
  await page.getByRole("button", { name: "Local development sign-in" }).click();
  await page.getByLabel("Test persona").selectOption(actor);
  await page.getByLabel("Development password", { exact: true }).fill(process.env.REFERENCE_TEST_PASSWORD!);
  await page.getByRole("button", { name: "Open local workspace" }).click();
  await expect(page.getByRole("heading", { name: "New chat", exact: true })).toBeVisible();
}
async function submitMessage(page: Page, button: "Send" | "Reply", status: number) {
  const completed = page.waitForResponse(response => response.request().method() === "POST" &&
    new URL(response.url()).pathname.endsWith("/messages"));
  await page.getByRole("button", { name: button, exact: true }).click();
  const response = await completed;
  expect(response.status()).toBe(status);
  return response.json();
}
test("browse, page, send, preserve drafts, and sign out", async ({ page }) => {
  await login(page);
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await expect(page.getByRole("heading", { name: "Recent messages" })).toBeAttached();
  await page.getByRole("button", { name: "Load earlier messages" }).click();
  await expect(page.getByText("Synthetic conversation context 10.", { exact: false })).toBeVisible();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Draft stays in memory");
  await page.getByRole("button", { name: /New conversation Chat/ }).click();
  await expect(page.getByRole("heading", { name: "No messages yet" })).toBeVisible();
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("Draft stays in memory");
  expect((await submitMessage(page, "Send", 201)).state).toBe("confirmed");
  await expect(page.getByText("Draft stays in memory", { exact: true })).toBeVisible();
  await expect(page.locator(".message").filter({ hasText: "Draft stays in memory" }).locator("time")).toBeVisible();
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("link", { name: "Sign in with Microsoft" })).toBeVisible();
});
test("channel threads, external events, and markup safety", async ({ page }) => {
  await login(page);
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  await page.getByRole("button", { name: /General Northwind/ }).click();
  await expect(page.getByText(/Synthetic text-safety fixture:/)).toBeVisible();
  expect(await page.locator(".message-text img, .message-text script").count()).toBe(0);
  await page.getByRole("button", { name: "Open thread" }).nth(1).click();
  await expect(page.getByRole("complementary", { name: "Channel thread" })).toBeVisible();
  await page.getByRole("textbox", { name: "Write a reply" }).fill("A browser-validated thread reply");
  expect((await submitMessage(page, "Reply", 201)).state).toBe("confirmed");
  await expect(page.getByText("A browser-validated thread reply", { exact: true })).toBeVisible();
  await expect(page.locator(".message").filter({ hasText: "A browser-validated thread reply" }).locator("time")).toBeVisible();
  await page.getByRole("button", { name: "Close thread" }).click();
  await page.getByRole("button", { name: "Test scenarios" }).click();
  await page.getByRole("button", { name: "Receive external update" }).click();
  await expect(page.getByText("A new synthetic update arrived through the notification pipeline.")).toBeVisible();
});
test("uncertain send resolves the original operation without duplicate creation", async ({ page }) => {
  await login(page);
  const writes: string[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && new URL(request.url()).pathname.endsWith("/messages"))
      writes.push(request.url());
  });
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await page.getByRole("button", { name: "Test scenarios" }).click();
  await page.getByLabel("Next send outcome").selectOption("unknown");
  await expect(page.getByRole("status").filter({ hasText: 'The next send will use the "unknown" simulator outcome.' })).toBeVisible();
  await page.getByRole("button", { name: "Close controls" }).click();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Uncertain original operation");
  expect((await submitMessage(page, "Send", 202)).state).toBe("unknown");
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Change chat" }).click();
  await page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toBeVisible();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Must not replay the uncertain send");
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
  expect(writes).toHaveLength(1);
  await page.getByRole("button", { name: "Check original send" }).click();
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Uncertain original operation", { exact: true })).toHaveCount(1);
  expect(writes).toHaveLength(1);
});
test("restricted persona cannot discover another conversation", async ({ page }) => {
  await login(page, "jordan");
  await expect(page.getByRole("button", { name: /Restricted fixture Chat/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Sam Rivera Chat/ })).toHaveCount(0);
  const response = await page.request.get("/api/conversations/chat-handover/messages");
  expect(response.status()).toBe(404);
});
test("local sign-in exposes exactly three fixture personas", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Local development sign-in" }).click();
  const options = page.getByLabel("Test persona").locator("option");
  expect(await options.evaluateAll(values => values.map(value => value.getAttribute("value")))).toEqual(["alex", "sam", "jordan"]);
});
test("Jordan sees only permitted group history while remaining excluded from the handover", async ({ page }) => {
  await login(page, "jordan");
  await page.getByRole("button", { name: "Opening team Group chat", exact: true }).click();
  await expect(page.getByText("The team plan is ready for review.", { exact: true })).toBeVisible();
  await expect(page.getByText("Earlier context, visible only to existing members.", { exact: true })).toHaveCount(0);
  const history = await page.request.get("/api/conversations/chat-project/messages").then(response => response.json());
  expect(history.messages).toHaveLength(2);
  expect((await page.request.get("/api/conversations/chat-handover/messages")).status()).toBe(404);
});
test("offboarding denies existing sessions and clears the open browser view", async ({ page, browser }) => {
  await login(page);
  const member = await browser.newContext();
  const memberPage = await member.newPage();
  await login(memberPage, "sam");
  await memberPage.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  const session = await page.request.get("/api/session").then(response => response.json());
  const action = (value: string) => page.request.post("/api/simulator/offboarding", {
    headers: { "X-CSRF-TOKEN": session.csrf }, data: { action: value },
  });
  try {
    expect((await action("offboard")).status()).toBe(200);
    expect((await memberPage.request.get("/api/conversations")).status()).toBe(401);
    await expect(memberPage.getByRole("link", { name: "Sign in with Microsoft" })).toBeVisible();
    await expect(memberPage.locator(".message-text")).toHaveCount(0);
  } finally { await action("restore"); await member.close(); }
});
test("rejection creates no message and offline mode preserves only an in-page draft", async ({ page, context }) => {
  await login(page);
  await page.getByRole("button", { name: /New conversation Chat/ }).click();
  await page.getByRole("button", { name: "Test scenarios" }).click();
  await page.getByLabel("Next send outcome").selectOption("reject");
  await expect(page.getByRole("status").filter({ hasText: 'The next send will use the "reject" simulator outcome.' })).toBeVisible();
  await page.getByRole("button", { name: "Close controls" }).click();
  await page.getByRole("textbox", { name: "Write a message" }).fill("A deliberately rejected message");
  expect((await submitMessage(page, "Send", 422)).error).toContain("simulator rejected");
  await expect(page.getByText("Not sent", { exact: true })).toBeVisible();
  const data = await page.request.get("/api/conversations/chat-empty/messages").then(response => response.json());
  expect(data.messages).toHaveLength(0);
  await page.getByRole("button", { name: "Edit before a new attempt" }).click();
  await context.setOffline(true);
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeDisabled();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("A deliberately rejected message");
  await context.setOffline(false);
  await expect(page.getByRole("button", { name: "Send", exact: true })).toBeEnabled();
});
test("external edits and deletions update the open conversation", async ({ page }) => {
  await login(page);
  await page.getByRole("button", { name: /General Northwind/ }).click();
  await page.getByRole("button", { name: "Test scenarios" }).click();
  await page.getByRole("button", { name: "Edit a message" }).click();
  await expect(page.getByText("Edited synthetic update.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Delete a message" }).click();
  await expect(page.getByText("This message was deleted.", { exact: true })).toBeVisible();
});
test("desktop and mobile Split View remain usable without persistent business data", async ({ page }, testInfo) => {
  await login(page);
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("split-view-desktop.png") });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole("button", { name: "Back", exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.getByRole("button", { name: "Toggle light and dark theme" }).click();
  await page.screenshot({ path: testInfo.outputPath("split-view-mobile.png") });
  await page.getByRole("button", { name: "Back", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Messages", exact: true })).toBeVisible();
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
});

test("new chat searches authorized chats, reuses the composer and keeps independent drafts", async ({ page }) => {
  await login(page);
  const writes: string[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && new URL(request.url()).pathname.startsWith("/api/conversations"))
      writes.push(new URL(request.url()).pathname);
  });
  const results = page.getByRole("region", { name: "Find a chat" });
  await expect(results.getByRole("button", { name: /^Open chat:/ })).toHaveCount(3);
  await expect(results).toContainText("Creating a chat and looking up people by email are not available");
  await page.getByRole("searchbox", { name: "To", exact: true }).fill("  SAM  ");
  await results.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toBeFocused();
  await page.getByRole("textbox", { name: "Write a message" }).fill("A draft stays in the original chat");
  await page.getByRole("button", { name: "Change chat" }).click();
  await page.getByRole("searchbox", { name: "To", exact: true }).fill("new conversation");
  await results.getByRole("button", { name: "Open chat: New conversation", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("");
  await page.getByRole("button", { name: "Change chat" }).click();
  await page.getByRole("searchbox", { name: "To", exact: true }).fill("Sam");
  await results.getByRole("button", { name: "Open chat: Sam Rivera", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveValue("A draft stays in the original chat");
  expect(writes).toEqual([]);
  expect(await page.locator(".conversation-row").count()).toBe(5);
  expect((await submitMessage(page, "Send", 201)).state).toBe("confirmed");
  await expect(page.getByText("A draft stays in the original chat", { exact: true })).toHaveCount(1);
  expect(writes).toEqual(["/api/conversations/chat-handover/messages"]);
});

test("chat search has honest no-match, retry and restricted-access states", async ({ page }) => {
  let unavailable = true;
  await page.route("**/api/conversations", async route => {
    if (unavailable) await route.fulfill({ status: 503, json: { code: "unavailable", message: "Chat access is temporarily unavailable." } });
    else await route.continue();
  });
  await login(page, "jordan");
  const results = page.getByRole("region", { name: "Find a chat" });
  await expect(results.getByRole("heading", { name: "Chats are unavailable" })).toBeVisible();
  await expect(results.getByRole("heading", { name: "No chats available" })).toHaveCount(0);
  unavailable = false;
  await results.getByRole("button", { name: "Try chat search again" }).click();
  await expect(results.getByRole("button", { name: /^Open chat:/ })).toHaveCount(2);
  await expect(results.getByRole("button", { name: /Sam Rivera/ })).toHaveCount(0);
  await page.getByRole("searchbox", { name: "To", exact: true }).fill("nobody@example.com");
  await expect(results.getByRole("heading", { name: "No matching chats" })).toBeVisible();
  await results.getByRole("button", { name: "Clear chat search" }).click();
  await expect(page.getByRole("searchbox", { name: "To", exact: true })).toBeFocused();
});

for (const theme of ["light", "dark"]) {
  for (const width of [1280, 390, 320]) {
    test(`new chat keyboard and responsive layout at ${width}px in ${theme} theme`, async ({ page }, testInfo) => {
      const errors: string[] = [];
      page.on("pageerror", error => errors.push(error.message));
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ reducedMotion: "reduce" });
      await login(page);
      await page.evaluate(value => { document.documentElement.dataset.theme = value; }, theme);
      await expect(page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true })).toBeVisible();
      await expect(page.getByRole("searchbox", { name: "To", exact: true })).toBeFocused();
      await expect(page.getByRole("button", { name: "Close chat search" })).toBeInViewport();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      const contrast = await page.evaluate(() => {
        const rgb = (value: string) => value.match(/[\d.]+/g)!.map(Number);
        const luminance = (channels: number[]) => channels.slice(0, 3).map(value => {
          const channel = value / 255;
          return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
        }).reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
        return [".discovery-title h1", ".discovery-header > p", ".search-hint", ".capability-note",
          ".person-copy strong", ".person-copy span", ".person-affordance", ".directory-note", "#chat-search"].map(selector => {
          const element = document.querySelector(selector)!;
          let ancestor: Element | null = element;
          while (ancestor && getComputedStyle(ancestor).backgroundColor === "rgba(0, 0, 0, 0)") ancestor = ancestor.parentElement;
          const background = luminance(rgb(getComputedStyle(ancestor!).backgroundColor));
          const foreground = luminance(rgb(getComputedStyle(element).color));
          const ratio = (Math.max(background, foreground) + 0.05) / (Math.min(background, foreground) + 0.05);
          return { selector, ratio };
        });
      });
      for (const sample of contrast) expect(sample.ratio, `${sample.selector} text contrast`).toBeGreaterThanOrEqual(4.5);
      await page.screenshot({ path: testInfo.outputPath(`new-chat-${width}-${theme}.png`) });
      await page.getByRole("searchbox", { name: "To", exact: true }).fill("Sam");
      await page.keyboard.press("Tab");
      await expect(page.getByRole("button", { name: "Open chat: Sam Rivera", exact: true })).toBeFocused();
      await page.keyboard.press("Enter");
      const composer = page.getByRole("textbox", { name: "Write a message" });
      await expect(composer).toBeFocused();
      await composer.fill("Keyboard draft");
      await composer.dispatchEvent("keydown", { key: "Enter", code: "Enter", isComposing: true });
      await expect(composer).toHaveValue("Keyboard draft");
      await composer.press("Shift+Enter");
      await expect(composer).toHaveValue("Keyboard draft\n");
      await page.getByRole("button", { name: "Change chat" }).click();
      await page.keyboard.press("Escape");
      await expect(page.getByRole("button", { name: "Change chat" })).toBeFocused();
      await expect(composer).toHaveValue("Keyboard draft\n");
      await expect(composer).toBeInViewport();
      await expect(page.getByRole("button", { name: "Send", exact: true })).toBeInViewport();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (width <= 700) {
        await page.getByRole("button", { name: "Back", exact: true }).click();
        await page.getByRole("button", { name: "New chat", exact: true }).click();
        await page.keyboard.press("Escape");
        await expect(page.getByRole("button", { name: "New chat", exact: true })).toBeFocused();
      }
      expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
      expect(errors).toEqual([]);
    });
  }
}

test("mocked GraphPilot metadata keeps the reference flow within its dedicated group-chat scope", async ({ page }) => {
  const businessWrites: string[] = [];
  await page.route("**/api/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (route.request().method() === "POST" && path !== "/api/realtime/negotiate") businessWrites.push(path);
    if (path === "/api/session") await route.fulfill({ json: {
      id: "mock-alex", name: "Alex (local test metadata)", simulated: false, localAuth: false,
      scenarioAdmin: false, basicValidation: true, csrf: "local-metadata-test",
    } });
    else if (path === "/api/conversations") await route.fulfill({ json: [
      { id: "graph-pilot-chat", title: "Dedicated validation group chat", kind: "chat", team: null, revision: 1 },
      { id: "graph-pilot-channel", title: "General", kind: "channel", team: "Dedicated test team", revision: 1 },
    ] });
    else if (path === "/api/realtime/negotiate") await route.fulfill({ json: { transport: "poll", intervalSeconds: 30 } });
    else if (path === "/api/conversations/graph-pilot-chat/messages") await route.fulfill({ json: {
      messages: [], cursor: null, revision: 1, window: "mock-window", simulated: false, parentMessage: null,
    } });
    else await route.fulfill({ status: 404, json: { code: "outside_test_scope", message: "Not in the local metadata fixture." } });
  });
  await page.goto("/");
  await expect(page.getByText("Basic Teams validation", { exact: true })).toBeVisible();
  await expect(page.getByText("Updates every 30s", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /^Open chat:/ })).toHaveCount(1);
  await expect(page.getByRole("region", { name: "Find a chat" })).toContainText("New one-to-one chats and directory lookup are not enabled");
  await expect(page.getByRole("button", { name: "Test scenarios" })).toHaveCount(0);
  const navigation = page.getByRole("navigation", { name: "Conversations", exact: true });
  const pilotChat = navigation.getByRole("button").filter({ hasText: "Dedicated validation group chat" });
  await expect(pilotChat).toHaveCount(1);
  await pilotChat.click();
  await expect(page.getByRole("heading", { name: "Dedicated validation group chat" })).toBeVisible();
  await page.getByRole("button", { name: "Change chat" }).click();
  await page.getByRole("button", { name: "Open chat: Dedicated validation group chat", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Dedicated validation group chat" })).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toBeFocused();
  await expect(page.getByText("Direct Teams reads", { exact: false })).toBeVisible();
  expect(businessWrites).toEqual([]);
});

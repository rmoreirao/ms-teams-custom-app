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
  await expect(page.getByRole("heading", { name: "Your conversations" })).toBeVisible();
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
  await page.getByRole("button", { name: /Sam Rivera Chat/ }).click();
  await page.getByRole("button", { name: "Test scenarios" }).click();
  await page.getByLabel("Next send outcome").selectOption("unknown");
  await expect(page.getByRole("status").filter({ hasText: 'The next send will use the "unknown" simulator outcome.' })).toBeVisible();
  await page.getByRole("button", { name: "Close controls" }).click();
  await page.getByRole("textbox", { name: "Write a message" }).fill("Uncertain original operation");
  expect((await submitMessage(page, "Send", 202)).state).toBe("unknown");
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Check original send" }).click();
  await expect(page.getByText("Outcome unknown - do not resend", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Uncertain original operation", { exact: true })).toHaveCount(1);
});
test("restricted persona cannot discover another conversation", async ({ page }) => {
  await login(page, "jordan");
  await expect(page.getByRole("button", { name: /Restricted fixture Chat/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Sam Rivera Chat/ })).toHaveCount(0);
  const response = await page.request.get("/api/conversations/chat-handover/messages");
  expect(response.status()).toBe(404);
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
  await expect(page.getByRole("heading", { name: "Your conversations" })).toBeVisible();
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
});

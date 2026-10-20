import { expect, test } from "./live-fixture";
import type { Page } from "@playwright/test";

async function approvedPeople(page: Page, failure = { people: false, open: false }) {
  const requests = { opens: 0, messagePosts: 0 };
  await page.route("**/api/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/session") return route.fulfill({ json: {
      id: "jordan", name: "Jordan Lee", environment: "Development", source: "Microsoft Teams",
      scopedValidation: true, canStartDirectChats: true, canCreateDirectChats: false, csrf: "synthetic-csrf",
    } });
    if (path === "/api/realtime/negotiate") return route.fulfill({ json: {
      transport: "webpubsub", url: "wss://realtime.example.test/client/hubs/test",
    } });
    if (path === "/api/conversations") return route.fulfill({ json: [
      { id: "graph-pilot-chat", title: "Teams Reference Validation group", kind: "chat", team: null, revision: 1 },
    ] });
    if (path === "/api/people") return failure.people ?
      route.fulfill({ status: 503, json: { code: "graph_unavailable", message: "Teams could not supply people. Try again." } }) :
      route.fulfill({ json: [
        { id: "alex", name: "Alex Morgan", email: "alex@example.test" },
        { id: "sam", name: "Sam Rivera", email: "sam@example.test" },
      ] });
    if (path === "/api/chats/direct") {
      requests.opens++;
      expect(request.postDataJSON()).toEqual({ personId: "alex" });
      expect(request.headers()["x-csrf-token"]).toBe("synthetic-csrf");
      return failure.open ? route.fulfill({ status: 503, json: {
        code: "resource_preparing", message: "Your approved chat is being prepared. No message was sent. Try again.",
      } }) : route.fulfill({ json: {
        id: "graph-pilot-dm-synthetic-pair", title: "Alex Morgan", kind: "chat", team: null, revision: 1,
      } });
    }
    if (path.endsWith("/messages") && request.method() === "POST") {
      requests.messagePosts++;
      return route.fulfill({ status: 409, json: { code: "not_part_of_test", message: "This fixture does not send messages." } });
    }
    if (path.endsWith("/messages")) return route.fulfill({ json: {
      messages: [], cursor: null, revision: 1, window: "synthetic-command-window", parentMessage: null,
    } });
    return route.fulfill({ status: 404, json: { code: "outside_fixture", message: "Not in the local metadata fixture." } });
  });
  return requests;
}

for (const variant of [{ width: 1280, theme: "light" }, { width: 390, theme: "dark" }]) {
  test(`Jordan finds Alex and reuses an approved chat (${variant.width}px ${variant.theme}, controlled Live API)`, async ({ page }, testInfo) => {
    const requests = await approvedPeople(page);
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.setViewportSize({ width: variant.width, height: 844 });
    await page.goto(`/?clawpilotTheme=${variant.theme}`);
    const input = page.getByRole("searchbox", { name: "To", exact: true });
    await expect(input).toBeFocused();
    await expect(page.getByRole("button", { name: /^Choose:/ })).toHaveCount(2);
    expect(requests.opens).toBe(0);
    await input.fill("alex");
    const alex = page.getByRole("button", { name: "Choose: Alex Morgan, alex@example.test", exact: true });
    await expect(alex).toBeVisible();
    await expect(page.locator(".conversation-row").filter({ hasText: "Alex" })).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath("jordan-finds-alex-mocked.png") });
    await input.fill("alex@example.test");
    await page.keyboard.press("Tab");
    await expect(alex).toBeFocused();
    await page.keyboard.press("Enter");
    const composer = page.getByRole("textbox", { name: "Write a message", exact: true });
    await expect(composer).toBeFocused();
    await expect(page.getByRole("heading", { name: "Alex Morgan", exact: true })).toBeVisible();
    await expect(page.getByText("alex@example.test", { exact: false })).toBeVisible();
    expect(requests.opens).toBe(1);
    expect(requests.messagePosts).toBe(0);
    await composer.fill("Draft for Alex - never sent by this fixture");
    await page.getByRole("button", { name: "Change person" }).click();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("button", { name: "Change person" })).toBeFocused();
    await page.getByRole("button", { name: "Change person" }).click();
    await alex.click();
    await expect(composer).toHaveValue("Draft for Alex - never sent by this fixture");
    await expect(page.locator(".conversation-row").filter({ hasText: "Alex Morgan" })).toHaveCount(1);
    expect(requests.messagePosts).toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
    expect(errors).toEqual([]);
  });
}

test("people failure and preparing chat remain explicit and never cause an automatic POST retry", async ({ page }) => {
  const failure = { people: true, open: true };
  const requests = await approvedPeople(page, failure);
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "People are unavailable" })).toBeVisible();
  failure.people = false;
  await page.getByRole("button", { name: "Try people search again" }).click();
  await page.getByRole("button", { name: "Choose: Alex Morgan, alex@example.test", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("No message was sent");
  expect(requests.opens).toBe(1);
  expect(requests.messagePosts).toBe(0);
  await expect(page.getByRole("textbox", { name: "Write a message" })).toHaveCount(0);
  failure.open = false;
  await page.getByRole("button", { name: "Choose: Alex Morgan, alex@example.test", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "Write a message" })).toBeFocused();
  expect(requests.opens).toBe(2);
  expect(requests.messagePosts).toBe(0);
});

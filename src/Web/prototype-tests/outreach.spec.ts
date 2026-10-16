import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";

const variants = [
  { file: "05-new-chat.html", mode: "new" },
  { file: "06-people-directory.html", mode: "directory" },
  { file: "07-contextual-profile.html", mode: "profile" },
] as const;
type Variant = typeof variants[number];

async function findPerson(page: Page, variant: Variant, person: "priya" | "maya") {
  if (variant.mode === "profile") {
    await page.locator(`#member-${person}`).click();
  } else {
    if (!(await page.locator("#discovery").isVisible())) await page.locator("#reach-entry").click();
    await page.locator("#people-search").fill(person);
    await page.locator(`#pick-${person}`).click();
  }
  if (variant.mode !== "new") {
    await expect(page.locator("#profile-name")).toHaveText(person === "priya" ? "Priya Nair" : "Maya Chen");
    await expect(page.locator("#profile-name")).toBeInViewport();
    await expect(page.locator("#person-panel [data-outreach-action='chat']")).toBeInViewport();
    if (page.viewportSize()!.width <= 720) await expect(page.locator("#discovery")).toBeHidden();
    await page.locator("#person-panel [data-outreach-action='chat']").click();
  }
}

for (const variant of variants) {
  test(`${variant.mode}: discover, verify, draft, send, and reuse`, async ({ page }) => {
    const errors: string[] = [];
    const externalRequests: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("request", request => {
      if (new URL(request.url()).hostname !== "127.0.0.1") externalRequests.push(request.url());
    });
    await page.goto(`/${variant.file}`);
    if (variant.mode !== "profile") {
      await expect(page.getByRole("button", { name: "Close people search", exact: true })).toBeInViewport();
    }
    const count = await page.locator(".conversation-row").count();
    await findPerson(page, variant, "priya");
    await expect(page.locator("#conversation-title")).toHaveText("Priya Nair");
    await expect(page.locator(".conversation-subtitle")).toContainText("priya.nair@example.com");
    await expect(page.locator("#message-input")).toBeFocused();
    await expect(page.locator(".conversation-row")).toHaveCount(count);
    await page.locator("#message-input").fill("A draft for Priya");
    await page.keyboard.press("Escape");
    if (variant.mode === "profile") {
      await page.locator("#nav-design").click();
    } else {
      await page.locator("#reach-entry").click();
    }
    await findPerson(page, variant, "priya");
    await expect(page.locator("#message-input")).toHaveValue("A draft for Priya");
    await page.locator("#message-input").fill("<img src=x onerror=alert(1)>\nLet's compare notes.");
    await page.getByRole("button", { name: "Send message", exact: true }).click();
    await expect(page.locator(".delivery")).toContainText("Sent (demo)");
    await expect(page.locator(".conversation-row")).toHaveCount(count + 1);
    await expect(page.locator(".message-text")).toContainText("<img src=x onerror=alert(1)>");
    await expect(page.locator(".message-text img")).toHaveCount(0);
    await expect(page.locator("#message-input")).toHaveValue("");
    await page.keyboard.press("Escape");
    if (variant.mode === "profile") await page.locator("#nav-design").click();
    await findPerson(page, variant, "priya");
    await expect(page.locator(".message-text")).toContainText("Let's compare notes.");
    await expect(page.locator(".conversation-row")).toHaveCount(count + 1);
    await page.keyboard.press("Escape");
    if (variant.mode === "profile") await page.locator("#nav-design").click();
    await findPerson(page, variant, "maya");
    await expect(page.locator("#conversation-title")).toHaveText("Maya Chen");
    await expect(page.locator("#message-list .message")).toHaveCount(5);
    await expect(page.locator(".conversation-row")).toHaveCount(count + 1);
    expect(errors).toEqual([]);
    expect(externalRequests).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });

  test(`${variant.mode}: offline, uncertain delivery, threads, and reset`, async ({ page }) => {
    await page.goto(`/${variant.file}`);
    await findPerson(page, variant, "priya");
    await page.locator("#message-input").fill("Keep this draft offline");
    await page.locator("#demo-menu summary").click();
    await page.locator("#connection-control").click();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("button", { name: "Send message", exact: true })).toBeDisabled();
    await page.getByRole("button", { name: "Reconnect", exact: true }).click();
    await expect(page.locator("#message-input")).toHaveValue("Keep this draft offline");
    await page.locator("#demo-menu summary").click();
    await page.locator("#send-outcome").selectOption("unknown");
    await page.keyboard.press("Escape");
    await page.locator("#message-input").press("Enter");
    await expect(page.locator(".delivery")).toContainText("Delivery unknown (demo)");
    await page.getByRole("button", { name: "Check status", exact: true }).click();
    await expect(page.locator(".delivery")).toContainText("Sent (demo)");
    await expect(page.locator("#message-list .message")).toHaveCount(1);
    await page.keyboard.press("Escape");
    await page.locator("#nav-design").click();
    await page.locator("#reply-d1").click();
    await expect(page.locator("#thread")).toBeVisible();
    await page.locator("#reply-input").fill("A channel reply");
    await page.getByRole("button", { name: "Send reply", exact: true }).click();
    await expect(page.locator("#thread .delivery")).toContainText("Sent (demo)");
    await page.keyboard.press("Escape");
    await page.locator("#demo-menu summary").click();
    await page.getByRole("button", { name: "Reset demo", exact: true }).click();
    await expect(page.locator("#nav-person-priya")).toHaveCount(0);
    await expect(page.locator(".conversation-row")).toHaveCount(6);
  });
}

for (const variant of variants.filter(item => item.mode !== "profile")) {
  test(`${variant.mode}: search, ambiguous identities, unavailable account, and dismissal`, async ({ page }) => {
    await page.goto(`/${variant.file}`);
    await page.locator("#people-search").fill("nobody-matches");
    await expect(page.getByRole("heading", { name: "No colleagues found" })).toBeVisible();
    await page.getByRole("button", { name: "Clear people search", exact: true }).click();
    await page.locator("#people-search").fill("Jordan Lee");
    await expect(page.locator(".person-row")).toHaveCount(2);
    await expect(page.locator("#pick-jordan")).toContainText("Frontend engineer");
    await expect(page.locator("#pick-jordan-ops")).toContainText("jordan.lee.ops@example.com");
    if (variant.mode === "directory") {
      await page.locator("#department").selectOption("Operations");
      await expect(page.locator(".person-row")).toHaveCount(1);
      await page.locator("#pick-jordan-ops").click();
      await expect(page.locator("#person-panel")).toContainText("Workplace coordinator");
      await page.keyboard.press("Escape");
      await expect(page.locator("#pick-jordan-ops")).toBeFocused();
      await page.locator("#people-search").fill("IT support");
      await expect(page.locator("#pick-theo")).toBeVisible();
      await page.locator("#department").selectOption("all");
    }
    await page.locator("#people-search").fill("Avery");
    await page.locator("#pick-avery").click();
    await expect(page.locator("#person-panel")).toContainText("Messaging is unavailable");
    await expect(page.locator("#profile-name")).toBeInViewport();
    if (page.viewportSize()!.width <= 720) await expect(page.locator("#discovery")).toBeHidden();
    await expect(page.locator("#person-panel [data-outreach-action='chat']")).toBeDisabled();
    await page.keyboard.press("Escape");
    await expect(page.locator("#pick-avery")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.locator("#discovery")).toBeHidden();
    await expect(page.locator("#reach-entry")).toBeFocused();
  });
}

test("contextual profile restores channel context and keyboard focus", async ({ page }) => {
  await page.goto("/07-contextual-profile.html");
  await page.locator("#member-priya").focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#profile-name")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(page.locator("#member-priya")).toBeFocused();
  await page.locator("#author-d4").click();
  await expect(page.locator("#person-panel")).toContainText("priya.nair@example.com");
  await page.getByRole("button", { name: "View shared channel", exact: true }).click();
  await expect(page.locator("#conversation-title")).toHaveText("Design review");
  await expect(page.locator("#person-panel")).toBeHidden();
});

test("comparison links preserve the selected theme", async ({ page }) => {
  await page.goto("/index.html?clawpilotTheme=dark");
  for (const variant of variants) {
    const link = page.locator(`a[href*="${variant.file}"]`);
    await expect(link).toHaveAttribute("href", new RegExp(`${variant.file.replace(".", "\\.")}\\?clawpilotTheme=dark$`));
  }
  await page.getByRole("button", { name: "Switch to light theme", exact: true }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
});

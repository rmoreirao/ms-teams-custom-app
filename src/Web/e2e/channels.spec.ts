import { test, expect, message } from "./live-fixture";

test("discovery is metadata-only, preparation is event-driven, and opening never posts", async ({ page, live }) => {
  live.state.session!.canBrowseChannels = true;
  live.state.session!.channelRegistry = true;
  await page.goto("/");
  await expect(page.getByText("Live", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  expect(live.state.channelRequests).toHaveLength(0);
  await page.getByRole("button", { name: "Browse channels", exact: true }).click();
  const browser = page.getByRole("region", { name: "Browse channels" });
  await browser.getByRole("button", { name: "Northwind", exact: true }).click();
  await browser.getByRole("button", { name: "Open channel: Northwind / General" }).click();
  await expect(browser.getByText("Preparing channel: Northwind / #General", { exact: true })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const open = [...live.state.channelOperations.values()].find(item => item.kind === "open")!;
  expect(open.state).toBe("preparing");
  expect(live.state.sendRequests).toHaveLength(0);
  const reads = live.state.reads.filter(path => path.startsWith("/api/channel-operations/")).length;
  await page.clock.install();
  await page.clock.fastForward(60_000);
  expect(live.state.reads.filter(path => path.startsWith("/api/channel-operations/"))).toHaveLength(reads);
  expect(live.state.channelRequests).toHaveLength(3);
  live.state.channelOperations.set(open.id, { ...open, revision: 2, state: "ready",
    conversation: live.state.conversations.find(item => item.id === "channel-updates")! });
  live.event({ type: "channel.operation.changed", schemaVersion: 1, revision: 2, operationId: open.id });
  await page.clock.fastForward(100);
  await expect(page.getByRole("heading", { name: "General", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "New post", exact: true })).toBeEnabled();
  expect(live.state.sendRequests).toHaveLength(0);
  expect(live.state.reads.filter(path => path.startsWith("/api/channel-operations/"))).toHaveLength(reads + 1);
});

test("a same-revision root readiness event completes known-empty preparation without message polling", async ({ page, live }) => {
  live.state.session!.channelRegistry = true;
  live.state.messages.set("channel-updates", []);
  live.state.preparingViews.add("channel-updates:");
  await page.goto("/");
  await expect(page.getByText("Live", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  await page.clock.install();
  await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
  await expect(page.getByText("Preparing channel posts. Waiting for a live update.", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "No posts yet" })).toHaveCount(0);
  const before = live.state.reads.filter(path => path.endsWith("/channel-updates/messages")).length;
  await page.clock.fastForward(60_000);
  expect(live.state.reads.filter(path => path.endsWith("/channel-updates/messages"))).toHaveLength(before);
  live.state.preparingViews.delete("channel-updates:");
  live.event({ type: "channel.view.ready", schemaVersion: 1, revision: 1, sequence: 0,
    generation: 1, conversationId: "channel-updates", parentId: null });
  await page.clock.fastForward(100);
  await expect(page.getByRole("heading", { name: "No posts yet" })).toBeVisible();
  expect(live.state.reads.filter(path => path.endsWith("/channel-updates/messages"))).toHaveLength(before + 1);
});

for (const width of [1280, 390]) {
  test(`channel drafts, focused Back navigation, and explicit root/thread composition at ${width}px`, async ({ page, live }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/");
    await expect(page.getByText("Live", { exact: true })).toBeVisible();
    if (width <= 700) await page.getByRole("button", { name: "Close chat search" }).click();
    await page.getByRole("button", { name: "Channels", exact: true }).click();
    await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
    const inline = page.locator('[data-message-id="33"]').getByRole("region", { name: "Replies to Sam Rivera" });
    await expect(inline.getByText("Existing thread reply.", { exact: true })).toBeVisible();
    await expect(page.getByRole("complementary")).toHaveCount(0);
    await page.getByRole("button", { name: "New post", exact: true }).click();
    const composer = page.getByRole("textbox", { name: "Start a discussion in #General" });
    await expect(composer).toBeFocused();
    await composer.fill("Unsent root discussion");
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByRole("button", { name: "New post", exact: true })).toBeFocused();
    await page.getByRole("button", { name: "New post", exact: true }).click();
    await expect(composer).toHaveValue("Unsent root discussion");
    await inline.getByRole("button", { name: "Reply to Sam Rivera", exact: true }).click();
    await inline.getByRole("textbox", { name: "Reply to Sam Rivera" }).fill("One shared inline draft");
    await page.screenshot({ path: testInfo.outputPath(`channels-inline-${width}.png`) });
    await page.getByRole("button", { name: "Open in panel" }).nth(1).click();
    const thread = page.getByRole("complementary", { name: "Channel thread" });
    await expect(thread.getByText("Northwind / #General", { exact: true })).toBeVisible();
    const reply = thread.getByRole("textbox", { name: "Write a reply" });
    await expect(reply).toHaveValue("One shared inline draft");
    await reply.fill("Unsent thread reply");
    await expect(thread.getByRole("button", { name: "Reply", exact: true })).toBeEnabled();
    await expect(thread.getByRole("button", { name: "Post", exact: true })).toHaveCount(0);
    await thread.getByRole("button", { name: "Close thread" }).click();
    await expect(inline.getByRole("textbox", { name: "Reply to Sam Rivera" })).toHaveValue("Unsent thread reply");
    await expect(page.getByRole("button", { name: "Open in panel" }).nth(1)).toBeFocused();
    if (width <= 700) {
      await page.getByRole("button", { name: "Back", exact: true }).click();
      await expect(page.getByRole("button", { name: "Open channel: Northwind / General" })).toBeFocused();
    }
    await page.getByRole("button", { name: "Chats", exact: true }).click();
    await page.getByRole("button", { name: "Channels", exact: true }).click();
    await expect(composer).toHaveValue("Unsent root discussion");
    await page.getByRole("button", { name: "Open in panel" }).nth(1).click();
    await expect(reply).toHaveValue("Unsent thread reply");
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
    expect(live.state.sendRequests).toHaveLength(0);
    await page.screenshot({ path: testInfo.outputPath(`channels-${width}.png`) });
  });

  test(`cold inline replies finish from the targeted readiness event without recurring reads at ${width}px`, async ({ page, live }) => {
    await page.setViewportSize({ width, height: 900 });
    live.state.session!.channelRegistry = true;
    live.state.preparingViews.add("channel-updates:33");
    await page.goto("/");
    if (width <= 700) await page.getByRole("button", { name: "Close chat search" }).click();
    await page.getByRole("button", { name: "Channels", exact: true }).click();
    await page.clock.install();
    await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
    const inline = page.locator('[data-message-id="33"]').getByRole("region", { name: "Replies to Sam Rivera" });
    await expect(inline.getByText("Loading replies...", { exact: true })).toBeVisible();
    await expect(inline.getByText("No recent replies", { exact: true })).toHaveCount(0);
    const reads = () => live.state.messageReads.filter(path => path.includes("parent=33"));
    const before = reads().length;
    await page.clock.fastForward(60_000);
    expect(reads()).toHaveLength(before);
    live.state.preparingViews.delete("channel-updates:33");
    live.event({ type: "channel.view.ready", schemaVersion: 1, revision: 1, sequence: 0,
      generation: 1, conversationId: "channel-updates", parentId: "33" });
    await page.clock.fastForward(100);
    await expect(inline.getByText("Existing thread reply.", { exact: true })).toBeVisible();
    await expect(inline.getByText("Loading replies...", { exact: true })).toHaveCount(0);
    expect(reads()).toHaveLength(before + 1);
    expect(live.state.sendRequests).toHaveLength(0);
  });

  test(`long inline threads page within the post, collapse to two replies and receive live updates at ${width}px`, async ({ page, live }) => {
    await page.setViewportSize({ width, height: 900 });
    live.state.messages.get("channel-updates")!.push(...Array.from({ length: 25 }, (_, index) =>
      message(String(100 + index), `Paged reply ${index}.`, "33")));
    await page.goto("/");
    if (width <= 700) await page.getByRole("button", { name: "Close chat search" }).click();
    await page.getByRole("button", { name: "Channels", exact: true }).click();
    await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
    const inline = page.locator('[data-message-id="33"]').getByRole("region", { name: "Replies to Sam Rivera" });
    await expect(inline.getByText("Paged reply 24.", { exact: true })).toBeVisible();
    await expect(inline.getByRole("article")).toHaveCount(2);
    await expect(inline.getByText("Recent replies", { exact: true })).toBeVisible();
    await inline.getByRole("button", { name: "View all replies", exact: true }).click();
    await expect(inline.getByRole("article")).toHaveCount(20);
    await inline.getByRole("button", { name: "Load earlier messages" }).click();
    await expect(inline.getByRole("article")).toHaveCount(26);
    await expect(inline.getByText("26 recent replies", { exact: true })).toBeVisible();
    await inline.getByRole("button", { name: "Collapse replies" }).click();
    await expect(inline.getByRole("article")).toHaveCount(2);
    live.update("channel-updates", [...live.state.messages.get("channel-updates")!,
      message("200", "Live reply under its original post.", "33")]);
    await expect(inline.getByText("Live reply under its original post.", { exact: true })).toBeVisible();
    await expect(inline.getByRole("article")).toHaveCount(2);
    await expect(inline.getByRole("button", { name: "View all replies" })).toHaveAttribute("aria-expanded", "false");
    await expect(page.getByRole("complementary")).toHaveCount(0);
    expect(live.state.sendRequests).toHaveLength(0);
  });

  test(`known-empty thread completion uses a same-revision event at ${width}px`, async ({ page, live }) => {
    await page.setViewportSize({ width, height: 900 });
    live.state.session!.channelRegistry = true;
    live.state.preparingViews.add("channel-updates:33");
    live.state.messages.set("channel-updates", live.state.messages.get("channel-updates")!.filter(item => item.parentId !== "33"));
    await page.goto("/");
    if (width <= 700) await page.getByRole("button", { name: "Close chat search" }).click();
    await page.getByRole("button", { name: "Channels", exact: true }).click();
    await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
    const inline = page.locator('[data-message-id="33"]').getByRole("region", { name: "Replies to Sam Rivera" });
    await expect(inline.getByText("Loading replies...", { exact: true })).toBeVisible();
    await expect(inline.getByText("No recent replies", { exact: true })).toHaveCount(0);
    live.state.preparingViews.delete("channel-updates:33");
    live.event({ type: "channel.view.ready", schemaVersion: 1, revision: 1, sequence: 0,
      generation: 1, conversationId: "channel-updates", parentId: "33" });
    await expect(inline.getByText("No recent replies", { exact: true })).toBeVisible();
    await expect(inline.getByText("Loading replies...", { exact: true })).toHaveCount(0);
    expect(live.state.sendRequests).toHaveLength(0);
  });

  test(`failed Teams reply reads retain the post, expose Retry and never send a message at ${width}px`, async ({ page, live }) => {
    await page.setViewportSize({ width, height: 900 });
    live.state.failedViews.add("channel-updates:33");
    await page.goto("/");
    if (width <= 700) await page.getByRole("button", { name: "Close chat search" }).click();
    await page.getByRole("button", { name: "Channels", exact: true }).click();
    await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
    const inline = page.locator('[data-message-id="33"]').getByRole("region", { name: "Replies to Sam Rivera" });
    await expect(inline.getByRole("heading", { name: "Could not load replies from Microsoft Teams" })).toBeVisible();
    await expect(page.getByText("Plan for the next shift.", { exact: true })).toBeVisible();
    await expect(inline.getByText("No recent replies", { exact: true })).toHaveCount(0);
    live.state.failedViews.delete("channel-updates:33");
    await inline.getByRole("button", { name: "Retry", exact: true }).click();
    await expect(inline.getByText("Existing thread reply.", { exact: true })).toBeVisible();
    await expect(page.getByRole("complementary")).toHaveCount(0);
    expect(live.state.sendRequests).toHaveLength(0);
  });
}

test("fresh channel access can disable posting without hiding authorized posts", async ({ page, live }) => {
  live.state.canSend = false;
  await page.goto("/");
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
  await expect(page.getByText("Plan for the next shift.", { exact: true })).toBeVisible();
  await expect(page.getByText(/Read-only\./)).toBeVisible();
  await expect(page.getByRole("button", { name: "New post", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Cache diagnostics" })).toHaveCount(0);
});

test("only nearby posts read previews, and offscreen threads catch up when brought back", async ({ page, live }) => {
  await page.setViewportSize({ width: 1280, height: 700 });
  live.state.messages.set("channel-updates", Array.from({ length: 20 }, (_, index) =>
    message(`root-${index}`, `Discussion ${index}.`)));
  await page.goto("/");
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
  const first = page.locator('[data-message-id="root-0"]');
  const last = page.locator('[data-message-id="root-19"]');
  await expect(first.getByText("No recent replies", { exact: true })).toBeVisible();
  const reads = (parent: string) => live.state.contentReads.filter(path =>
    new URL(path, "https://local").searchParams.get("parent") === parent).length;
  expect(reads("root-19")).toBe(0);
  await last.scrollIntoViewIfNeeded();
  await expect(last.getByText("No recent replies", { exact: true })).toBeVisible();
  const beforeFirst = reads("root-0"), beforeLast = reads("root-19");
  live.update("channel-updates", [...live.state.messages.get("channel-updates")!,
    message("200", "Catch up on returning to this post.", "root-0")]);
  await expect.poll(() => reads("root-19")).toBeGreaterThan(beforeLast);
  expect(reads("root-0")).toBe(beforeFirst);
  await first.scrollIntoViewIfNeeded();
  await expect(first.getByText("Catch up on returning to this post.", { exact: true })).toBeVisible();
  expect(live.state.sendRequests).toHaveLength(0);
});

test("replies keep their own author styling beneath a post written by the signed-in user", async ({ page, live }) => {
  const root = live.state.messages.get("channel-updates")!.find(item => item.id === "33")!;
  root.actorId = live.state.session!.id;
  root.author = live.state.session!.name;
  await page.goto("/");
  await page.getByRole("button", { name: "Channels", exact: true }).click();
  await page.getByRole("button", { name: "Open channel: Northwind / General" }).click();
  const post = page.locator('[data-message-id="33"]');
  const reply = post.locator('[data-message-id="34"] .message-text');
  await expect(reply).toBeVisible();
  expect(await reply.evaluate(element => getComputedStyle(element).backgroundColor)).toBe("rgba(0, 0, 0, 0)");
  expect(await post.locator(":scope > .message-content > .message-body .message-text")
    .evaluate(element => getComputedStyle(element).backgroundColor)).not.toBe("rgba(0, 0, 0, 0)");
});

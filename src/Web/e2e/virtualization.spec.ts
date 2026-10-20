import { expect, test } from "./live-fixture";

for (const width of [1280, 390]) {
  test(`large loaded history is virtualized and safe rich content remains readable (${width}px, controlled API)`, async ({ page }, testInfo) => {
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("console", event => { if (event.type() === "error") errors.push(event.text()); });
    await page.route("**/api/**", async route => {
      const url = new URL(route.request().url());
      if (url.pathname === "/api/session") return route.fulfill({ json: {
        id: "alex", name: "Alex", csrf: "synthetic", environment: "Development", source: "Microsoft Teams",
        scopedValidation: true, canStartDirectChats: false, canCreateDirectChats: false,
      } });
      if (url.pathname === "/api/realtime/negotiate") return route.fulfill({ json: {
        transport: "webpubsub", url: "wss://realtime.example.test/client/hubs/test",
      } });
      if (url.pathname === "/api/conversations") return route.fulfill({ json: [
        { id: "large-chat", title: "Large history", kind: "chat", team: null, revision: 1 },
      ] });
      if (url.pathname.endsWith("/messages")) {
        const end = Number(url.searchParams.get("cursor") ?? 1000);
        const messages = Array.from({ length: 20 }, (_, index) => {
          const id = end - 19 + index;
          const at = new Date(Date.UTC(2026, 9, 19, 10) + id * 1000).toISOString();
          return {
            id: String(id), actorId: "sam", author: "Sam", body: `Rendered message ${id}`,
            htmlBody: `<p>Rendered message <strong>${id}</strong></p>` +
              (id % 3 === 0 ? `<p>${"Long multilingual context 日本語 ".repeat(12)}</p>` : "") +
              `<img src=x onerror="window.unsafe=true"><script>window.unsafe=true</script>`,
            createdAt: at, modifiedAt: at, parentId: null, deleted: false,
          };
        });
        return route.fulfill({ json: {
          messages, cursor: end > 20 ? String(end - 20) : null, revision: 1, window: "synthetic", parentMessage: null,
        } });
      }
      return route.fulfill({ status: 404, json: { code: "outside_fixture", message: "Outside controlled fixture." } });
    });
    await page.setViewportSize({ width, height: 844 });
    await page.goto("/");
    await page.getByRole("button", { name: "Open chat: Large history", exact: true }).click();
    const feed = page.locator(".messages");
    for (let index = 0; index < 3; index++) {
      await feed.evaluate(element => { element.scrollTop = 0; });
      await page.getByRole("button", { name: "Load earlier messages" }).click();
      await expect(page.getByRole("button", { name: "Load earlier messages" })).toBeEnabled();
    }
    await expect(page.getByRole("feed", { name: "Loaded messages" })).toBeAttached();
    await expect.poll(() => page.locator(".message[data-message-id]").count()).toBeLessThan(60);
    await feed.evaluate(element => { element.scrollTop = 0; });
    await expect(page.locator('[data-message-id="921"]')).toBeInViewport();
    await feed.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expect(page.locator('[data-message-id="1000"]')).toBeInViewport();
    expect(await page.locator(".message-text img, .message-text script").count()).toBe(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`virtual-history-${width}.png`) });
    expect(errors).toEqual([]);
  });
}

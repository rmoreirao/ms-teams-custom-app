import { afterEach, describe, expect, it, vi } from "vitest";
import { sendMessage } from "./api";

const command = { operationId: "original", window: "window", body: "Test message - 1", parentId: null };
afterEach(() => { vi.unstubAllGlobals(); });

describe("shared send outcome handling", () => {
  it.each([
    [201, { id: "other", state: "confirmed", messageId: "message", error: null }, "unknown"],
    [201, { id: "original", state: "confirmed", messageId: null, error: null }, "unknown"],
    [202, { id: "original", state: "invalid", messageId: null, error: null }, "unknown"],
    [422, { id: "original", state: "rejected", messageId: null, error: "Not sent." }, "rejected"],
    [429, { code: "throttled", message: "Wait before a new attempt." }, "rejected"],
    [502, { code: "unavailable", message: "Teams did not return an outcome." }, "unknown"],
  ])("maps status %i and body %j to %s without retry", async (status, body, state) => {
    const fetch = vi.fn(async () => new Response(JSON.stringify(body), { status }));
    vi.stubGlobal("fetch", fetch);
    expect(await sendMessage("direct", command, "csrf")).toMatchObject({ id: "original", state });
    expect(fetch).toHaveBeenCalledOnce();
  });

  it.each([[403, "rejected"], [502, "unknown"]])("keeps unreadable status %i explicit", async (status, state) => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("not json", { status })));
    expect(await sendMessage("direct", command, "csrf")).toMatchObject({ id: "original", state, error: expect.any(String) });
  });

  it("retains original-operation uncertainty after a network failure", async () => {
    const fetch = vi.fn(async () => { throw new TypeError("Connection lost."); });
    vi.stubGlobal("fetch", fetch);
    expect(await sendMessage("direct", command, "csrf")).toMatchObject({
      id: "original", state: "unknown", error: "Connection lost.",
    });
    expect(fetch).toHaveBeenCalledOnce();
  });
});

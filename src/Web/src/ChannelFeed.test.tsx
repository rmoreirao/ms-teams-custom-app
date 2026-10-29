// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { useMessageFeed } from "./useMessageFeed";
import type { Message, ReadAccess } from "./api";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const message = (id: string, parentId: string | null = null, age = 1000): Message => ({
  id, parentId, actorId: "sam", author: "Sam", body: id, deleted: false,
  createdAt: new Date(Date.now() - age).toISOString(), modifiedAt: new Date(Date.now() - age).toISOString(),
});
const access = (overrides: Partial<ReadAccess> = {}): ReadAccess => ({
  epoch: 1, canSend: true, historyFrom: new Date(Date.now() - 86400_000).toISOString(),
  validUntil: new Date(Date.now() + 300_000).toISOString(), ...overrides,
});
const page = (messages: Message[], cursor: string | null = null, proof = access(), parentMessage: Message | null = null) => ({
  messages, cursor, revision: 1, window: "window", access: proof, parentMessage,
});
const input = () => ({
  path: "/api/conversations/channel/messages", changesPath: "/api/conversations/channel/changes",
  accessPath: "/api/conversations/channel/access", eventDriven: true,
  parent: null as string | null, refresh: 0, minimumRevision: 0, unavailable: vi.fn(), expired: vi.fn(), syncParent: vi.fn(),
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); vi.restoreAllMocks(); });

describe("event-driven channel reads", () => {
  it("does not timer-poll a pending known-unknown root and completes a known-empty view on an event refresh", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn().mockResolvedValueOnce(json({ state: "pending", retryAfterMs: 1000, access: null }, 202))
      .mockImplementationOnce(async () => json(page([])));
    vi.stubGlobal("fetch", fetch);
    const initial = input();
    const { result, rerender } = renderHook(value => useMessageFeed(value), { initialProps: initial });
    await act(async () => {});
    expect(result.current.preparing).toBe(true);
    expect(result.current.page).toBeNull();
    expect(result.current.loading).toBe(false);
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000); });
    expect(fetch).toHaveBeenCalledTimes(1);
    rerender({ ...initial, refresh: 1 });
    await act(async () => {});
    expect(result.current.page?.messages).toEqual([]);
    expect(result.current.preparing).toBe(false);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("retains loaded history and retries the exact pending older page on readiness, without polling", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn().mockResolvedValueOnce(json(page([message("recent")], "opaque+older")))
      .mockResolvedValueOnce(json({ state: "pending", retryAfterMs: 1000, access: access() }, 202))
      .mockResolvedValueOnce(json(page([message("earlier", null, 2000)])));
    vi.stubGlobal("fetch", fetch);
    const initial = input();
    const { result, rerender } = renderHook(value => useMessageFeed(value), { initialProps: initial });
    await act(async () => {});
    await act(async () => { await result.current.older(); });
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["recent"]);
    expect(result.current.loadingMore).toBe(false);
    expect(result.current.preparing).toBe(true);
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
    expect(fetch).toHaveBeenCalledTimes(2);
    rerender({ ...initial, refresh: 1 });
    await act(async () => {});
    expect(String(fetch.mock.calls[2][0])).toContain("cursor=opaque%2Bolder");
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["earlier", "recent"]);
    expect(result.current.page?.cursor).toBeNull();
    expect(result.current.preparing).toBe(false);
  });

  it("only renews the access lease and applies canSend and tightened history without rereading content", async () => {
    vi.useFakeTimers();
    const original = access();
    const older = message("older", null, 5000);
    const recent = message("recent", null, 1000);
    const renewed = access({ canSend: false, historyFrom: recent.createdAt,
      validUntil: new Date(Date.now() + 600_000).toISOString() });
    const fetch = vi.fn(async (path: string) => path.endsWith("/access") ? json(renewed) :
      json(page([older, recent], null, original)));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed(input()));
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(270_000); });
    expect(fetch.mock.calls.map(([path]) => path)).toEqual([
      "/api/conversations/channel/messages", "/api/conversations/channel/access",
    ]);
    expect(result.current.page?.access?.canSend).toBe(false);
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["recent"]);
    await act(async () => { await vi.advanceTimersByTimeAsync(30_001); });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(result.current.page).not.toBeNull();
  });

  it("catches up all loaded posts after a newer revision completes the exact pending older page", async () => {
    const fetch = vi.fn().mockImplementationOnce(async () => json(page([message("recent")], "older")))
      .mockImplementationOnce(async () => json({ state: "pending", retryAfterMs: 1000, access: access() }, 202))
      .mockImplementationOnce(async () => json({ ...page([message("earlier", null, 2000)]), revision: 2 }))
      .mockImplementationOnce(async () => json({ changes: [], resetRequired: false, revision: 2,
        messages: [{ ...message("recent"), body: "Edited recent post" }], access: access() }));
    vi.stubGlobal("fetch", fetch);
    const initial = input();
    const { result, rerender } = renderHook(value => useMessageFeed(value), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    await act(async () => { await result.current.older(); });
    rerender({ ...initial, refresh: 1, minimumRevision: 2 });
    await waitFor(() => expect(result.current.page?.revision).toBe(2));
    expect(fetch.mock.calls[3][0]).toContain("after=1");
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["earlier", "recent"]);
    expect(result.current.page?.messages[1].body).toBe("Edited recent post");
  });

  it("registers an older-page demand before a readiness event races its pending response", async () => {
    let finish!: (value: Response) => void;
    const fetch = vi.fn().mockImplementationOnce(async () => json(page([message("recent")], "older")))
      .mockImplementationOnce(() => new Promise<Response>(resolve => { finish = resolve; }))
      .mockImplementationOnce(async () => json(page([message("earlier", null, 2000)])));
    vi.stubGlobal("fetch", fetch);
    const initial = input();
    const { result, rerender } = renderHook(value => useMessageFeed(value), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    let pending!: Promise<void>;
    act(() => { pending = result.current.older(); });
    rerender({ ...initial, refresh: 1 });
    await waitFor(() => expect(result.current.page?.messages.map(item => item.id)).toEqual(["earlier", "recent"]));
    await act(async () => { finish(json({ state: "pending", retryAfterMs: 1000, access: access() }, 202)); await pending; });
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["earlier", "recent"]);
    expect(fetch.mock.calls.slice(1).map(([path]) => String(path).includes("cursor=older"))).toEqual([true, true]);
  });

  it("restores authorized loaded pages in memory but never restores an expired lease", async () => {
    vi.useFakeTimers();
    const memory = { page: null, pages: 1 };
    const fetch = vi.fn(async () => json(page([message("recent")], "older",
      access({ validUntil: new Date(Date.now() + 5000).toISOString() }))));
    vi.stubGlobal("fetch", fetch);
    const first = renderHook(() => useMessageFeed({ ...input(), memory }));
    await act(async () => {});
    expect(memory.page).not.toBeNull();
    first.unmount();
    const second = renderHook(() => useMessageFeed({ ...input(), memory, paused: true }));
    expect(second.result.current.page?.messages[0].id).toBe("recent");
    second.unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(5001); });
    const expired = renderHook(() => useMessageFeed({ ...input(), memory, paused: true }));
    expect(expired.result.current.page).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it.each(["denied", "epoch", "expired"])("fails closed on %s authorization during a lease renewal", async outcome => {
    vi.useFakeTimers();
    const original = access({ validUntil: new Date(Date.now() + 5000).toISOString() });
    const fetch = vi.fn(async (path: string) => !path.endsWith("/access") ? json(page([message("private")], null, original)) :
      outcome === "denied" ? json({ code: "access_denied", message: "Membership revoked." }, 403) :
        outcome === "epoch" ? json(access({ epoch: 2 })) : json(original));
    vi.stubGlobal("fetch", fetch);
    const initial = input();
    const { result } = renderHook(() => useMessageFeed(initial));
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(5001); });
    expect(result.current.page).toBeNull();
    expect(fetch.mock.calls.map(([path]) => path)).toEqual([
      "/api/conversations/channel/messages", "/api/conversations/channel/access",
    ]);
    if (outcome === "denied") expect(initial.unavailable).toHaveBeenCalled();
  });

  it("binds thread access renewal to the parent and rejects an ineligible original parent", async () => {
    vi.useFakeTimers();
    const parent = message("parent", null, 5000);
    const proof = access({ validUntil: new Date(Date.now() + 5000).toISOString() });
    const fetch = vi.fn(async (path: string) => path.includes("/access") ? json(access({
      historyFrom: new Date(Date.now() - 1000).toISOString(),
    })) : json(page([message("reply", "parent")], null, proof, parent)));
    vi.stubGlobal("fetch", fetch);
    const initial = { ...input(), parent: "parent", path: "/api/conversations/channel/messages?parent=parent",
      accessPath: "/api/conversations/channel/access?parent=parent" };
    const { result } = renderHook(() => useMessageFeed(initial));
    await act(async () => {});
    expect(result.current.page?.parentMessage?.id).toBe("parent");
    await act(async () => { await vi.advanceTimersByTimeAsync(3750); });
    expect(fetch.mock.calls[1][0]).toBe("/api/conversations/channel/access?parent=parent");
    expect(result.current.page).toBeNull();
    expect(initial.unavailable).toHaveBeenCalled();
  });

  it("filters cross-thread messages and rejects a substituted parent", async () => {
    const parent = message("actual");
    const fetch = vi.fn(async () => json(page([message("reply", "actual"), message("other", "different")], null, access(), parent)));
    vi.stubGlobal("fetch", fetch);
    const initial = { ...input(), parent: "actual", path: "/api/conversations/channel/messages?parent=actual" };
    const { result, rerender } = renderHook(value => useMessageFeed(value), { initialProps: initial });
    await waitFor(() => expect(result.current.page?.messages.map(item => item.id)).toEqual(["reply"]));
    rerender({ ...initial, parent: "substituted", path: "/api/conversations/channel/messages?parent=substituted" });
    await waitFor(() => expect(result.current.error).toContain("no longer available"));
    expect(result.current.page).toBeNull();
  });

  it("uses a bounded demanded-read retry budget, not an indefinite channel discovery loop", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn(async () => json({ code: "projection_behind", message: "Synchronizing." }, 503));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed({ ...input(), minimumRevision: 4 }));
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000); });
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(result.current.page).toBeNull();
    expect(result.current.error).toBe("Synchronizing.");
  });
});

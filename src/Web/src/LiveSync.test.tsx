// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { parseHint, useRealtime } from "./useRealtime";
import { useMessageFeed } from "./useMessageFeed";
import type { Message, ReadAccess, Session } from "./api";

const sdk = vi.hoisted(() => ({
  handlers: new Map<string, (event?: unknown) => void>(), start: vi.fn(), stop: vi.fn(),
}));
vi.mock("@azure/web-pubsub-client", () => ({
  WebPubSubClient: class {
    private readonly options: { getClientAccessUrl: () => Promise<string> };
    constructor(options: { getClientAccessUrl: () => Promise<string> }) { this.options = options; }
    on(name: string, callback: (event?: unknown) => void) { sdk.handlers.set(name, callback); }
    async start() { await this.options.getClientAccessUrl(); return sdk.start(); }
    stop() { return sdk.stop(); }
  },
}));
const session: Session = { id: "alex", name: "Alex", environment: "Development", source: "Microsoft Teams", csrf: "csrf" };
const message = (id: string, body = id, parentId: string | null = null): Message => ({
  id, body, parentId, actorId: "sam", author: "Sam", createdAt: `2026-10-19T10:${id.padStart(2, "0")}:00Z`,
  modifiedAt: "2026-10-19T12:00:00Z", deleted: false,
});
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const page = (messages: Message[], cursor: string | null = null, revision = 1) => ({
  messages, cursor, revision, window: "window", parentMessage: null,
});
const handlers = () => ({ hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() });
afterEach(() => {
  cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); vi.restoreAllMocks(); sdk.handlers.clear(); sdk.start.mockReset(); sdk.stop.mockReset();
});

describe("live transport", () => {
  it.each([null, {}, { conversationId: "../secret", revision: 1 }, { conversationId: "chat", revision: -1 },
    { conversationId: "chat", revision: 1.5 }, { conversationId: "chat", revision: Number.MAX_SAFE_INTEGER + 1 }])(
    "rejects malformed hints without interpreting them as refresh-all", value => expect(() => parseHint(value)).toThrow());

  it("coalesces targeted hints and does not poll every 30 seconds in push mode", async () => {
    vi.useFakeTimers();
    vi.stubGlobal("fetch", vi.fn(async () => json({ transport: "webpubsub", url: "wss://realtime.example.test/client" })));
    const callbacks = handlers();
    const { result, unmount } = renderHook(() => useRealtime(session, callbacks));
    await act(async () => {});
    const emit = (data: unknown) => sdk.handlers.get("server-message")?.({ message: { data } });
    act(() => {
      sdk.handlers.get("connected")?.();
      emit({ conversationId: "chat-a", revision: 2 });
      emit({ conversationId: "chat-a", revision: 4 });
      emit({ conversationId: "chat-b", revision: 3 });
      vi.advanceTimersByTime(100);
    });
    expect(result.current.status).toBe("Live");
    expect(callbacks.hints).toHaveBeenCalledExactlyOnceWith([
      { conversationId: "chat-a", revision: 4 }, { conversationId: "chat-b", revision: 3 },
    ]);
    act(() => { vi.advanceTimersByTime(30_000); });
    expect(callbacks.reconcile).not.toHaveBeenCalled();
    act(() => { sdk.handlers.get("connected")?.(); });
    expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
    unmount();
    expect(sdk.stop).toHaveBeenCalledTimes(1);
    act(() => { vi.advanceTimersByTime(400_000); });
    expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
  });

  it.each(["poll", "sse"])("rejects retired %s transport without falling back to interval refresh", async transport => {
    vi.useFakeTimers();
    vi.stubGlobal("fetch", vi.fn(async () => json({ transport, intervalSeconds: 45, url: "/api/realtime/events" })));
    const callbacks = handlers();
    const { result } = renderHook(() => useRealtime(session, callbacks));
    await act(async () => {});
    expect(result.current.status).toBe("Synchronization unavailable");
    expect(callbacks.error).toHaveBeenCalledWith("The service returned an unsupported live transport.");
    act(() => { vi.advanceTimersByTime(30_000); });
    expect(callbacks.reconcile).not.toHaveBeenCalled();
    act(() => { vi.advanceTimersByTime(15_000); });
    expect(callbacks.reconcile).not.toHaveBeenCalled();
    expect(sdk.start).not.toHaveBeenCalled();
  });

  it("surfaces invalid transports and lets a manual reconnect retry negotiation", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => json({ transport: "unknown", url: "/invalid" })));
    const callbacks = handlers();
    const { result } = renderHook(() => useRealtime(session, callbacks));
    await waitFor(() => expect(result.current.status).toBe("Synchronization unavailable"));
    expect(callbacks.error).toHaveBeenCalledWith("The service returned an unsupported live transport.");
    act(() => { result.current.reconnect(); });
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
  });

  it("does not expose a source or process callbacks after unmount", async () => {
    let resolve!: (value: Response) => void;
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(finish => { resolve = finish; })));
    const callbacks = handlers();
    const { unmount } = renderHook(() => useRealtime(session, callbacks));
    unmount();
    await act(async () => { resolve(json({ transport: "webpubsub", url: "wss://realtime.example.test/client" })); });
    expect(sdk.start).not.toHaveBeenCalled();
    expect(callbacks.hints).not.toHaveBeenCalled();
  });
});

const feedInput = () => ({
  path: "/api/conversations/chat/messages", changesPath: "/api/conversations/chat/changes",
  parent: null as string | null, refresh: 0, minimumRevision: 0,
  unavailable: vi.fn(), expired: vi.fn(), syncParent: vi.fn(),
});
const access = (overrides: Partial<ReadAccess> = {}): ReadAccess => ({
  historyFrom: message("1").createdAt, validUntil: new Date(Date.now() + 300_000).toISOString(), epoch: 1, ...overrides,
});

describe("authorized message catch-up", () => {
  it.each(["dependency_unavailable", "read_refresh_unavailable", "graph_503"])(
    "retains authorized history, cursor and command window during %s without an error panel", async code => {
      vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
        json({ code, message: "Backend synchronization failed." }, 503) :
        json({ ...page([message("1")], "older", 42), access: access() })));
      const initial = feedInput();
      const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
      await waitFor(() => expect(result.current.page?.revision).toBe(42));
      rerender({ ...initial, refresh: 1 });
      await waitFor(() => expect(result.current.preparing).toBe(true));
      expect(result.current.page?.messages[0].id).toBe("1");
      expect(result.current.page?.cursor).toBe("older");
      expect(result.current.page?.window).toBe("window");
      expect(result.current.error).toBe("");
      expect(initial.unavailable).not.toHaveBeenCalled();
    });

  it("renews authority before its deadline even when no transport hint arrives", async () => {
    vi.useFakeTimers();
    const initialProof = access();
    const renewed = { ...initialProof, validUntil: new Date(Date.now() + 600_000).toISOString() };
    const fetch = vi.fn(async (path: string) => path.includes("/changes") ?
      json({ changes: [], messages: [], revision: 1, resetRequired: false, access: renewed }) :
      json({ ...page([message("1")]), access: initialProof }));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed(feedInput()));
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(269_999); });
    expect(fetch).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(result.current.page?.access?.validUntil).toBe(renewed.validUntil);
    await act(async () => { await vi.advanceTimersByTimeAsync(30_001); });
    expect(result.current.page).not.toBeNull();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("does not hot-poll when the authority source returns the same short lease", async () => {
    vi.useFakeTimers();
    const proof = access({ validUntil: new Date(Date.now() + 5000).toISOString() });
    const fetch = vi.fn(async (path: string) => path.includes("/changes") ?
      json({ changes: [], messages: [], revision: 1, resetRequired: false, access: proof }) :
      json({ ...page([message("1")]), access: proof }));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed(feedInput()));
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(4999); });
    expect(fetch).toHaveBeenCalledTimes(2);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(result.current.page).toBeNull();
  });

  it("automatically finishes an older pending page without replacing loaded history", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn().mockResolvedValueOnce(json({ ...page([message("2")], "older"), access: access() }))
      .mockResolvedValueOnce(json({ state: "pending", retryAfterMs: 1000, access: access() }, 202))
      .mockResolvedValueOnce(json({ ...page([message("1")]), access: access() }));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed(feedInput()));
    await act(async () => {});
    let loaded!: Promise<void>;
    await act(async () => { loaded = result.current.older(); });
    expect(result.current.loadingMore).toBe(true);
    expect(result.current.page?.messages.map(value => value.id)).toEqual(["2"]);
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); await loaded; });
    expect(result.current.page?.messages.map(value => value.id)).toEqual(["1", "2"]);
    expect(result.current.loadingMore).toBe(false);
  });

  it.each([{ state: "pending" }, "invalid", 42, null])("rejects a malformed 202 contract instead of rendering it as empty history: %j", async value => {
    vi.stubGlobal("fetch", vi.fn(async () => json(value, 202)));
    const { result } = renderHook(() => useMessageFeed(feedInput()));
    await waitFor(() => expect(result.current.error).toContain("invalid"));
    expect(result.current.page).toBeNull();
    expect(result.current.loading).toBe(false);
  });

  it("recovers a preparing projection with a bounded targeted read and retains the announced barrier", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn().mockResolvedValueOnce(json({ state: "pending", retryAfterMs: 1000, access: null }, 202))
      .mockResolvedValueOnce(json(page([message("2")], null, 2)));
    vi.stubGlobal("fetch", fetch);
    const initial = { ...feedInput(), minimumRevision: 2 };
    const { result } = renderHook(() => useMessageFeed(initial));
    await act(async () => {});
    expect(result.current.preparing).toBe(true);
    expect(result.current.page).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(999); });
    expect(fetch).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(result.current.page?.revision).toBe(2);
    expect(result.current.preparing).toBe(false);
    expect(result.current.error).toBe("");
    expect(fetch.mock.calls.every(([path]) => String(path).includes("minimumRevision=2"))).toBe(true);
    expect(initial.unavailable).not.toHaveBeenCalled();
  });

  it("recovers when a targeted repair takes longer than a minute without another catalog request", async () => {
    vi.useFakeTimers();
    let ready = false;
    const fetch = vi.fn(async (_path: string) => ready ? json(page([message("2")], null, 2)) :
      json({ state: "pending", retryAfterMs: 1000, access: null }, 202));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed({ ...feedInput(), minimumRevision: 2 }));
    await act(async () => {});
    for (const delay of [1000, 2000, 4000, 8000, 15000, 30000])
      await act(async () => { await vi.advanceTimersByTimeAsync(delay); });
    expect(result.current.preparing).toBe(true);
    ready = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(result.current.page?.revision).toBe(2);
    expect(result.current.error).toBe("");
    expect(fetch.mock.calls.every(([path]) => String(path).includes("/messages?minimumRevision=2"))).toBe(true);
    const reads = fetch.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(300_000); });
    expect(fetch).toHaveBeenCalledTimes(reads);
  });

  it("stops preparation retries after two minutes instead of introducing another polling transport", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn(async () => json({ state: "pending", retryAfterMs: 1000, access: null }, 202));
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useMessageFeed(feedInput()));
    await act(async () => {});
    for (const delay of [1000, 2000, 4000, 8000, 15000, 30000, 30000, 30000])
      await act(async () => { await vi.advanceTimersByTimeAsync(delay); });
    expect(fetch).toHaveBeenCalledTimes(9);
    await act(async () => { await vi.advanceTimersByTimeAsync(300_000); });
    expect(fetch).toHaveBeenCalledTimes(9);
    expect(result.current.preparing).toBe(false);
    expect(result.current.error).toBe("History is not available yet. Try again.");
  });

  it("cancels delayed preparation reads when the feed is unmounted", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn(async () => json({ code: "authorization_stale", message: "Revalidating." }, 503));
    vi.stubGlobal("fetch", fetch);
    const { unmount } = renderHook(() => useMessageFeed(feedInput()));
    await act(async () => {});
    unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(120_000); });
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("merges edits, deletions and new messages without losing loaded earlier pages", async () => {
    const fetch = vi.fn(async (path: string) => path.includes("/changes") ? json({
      changes: [], revision: 4, resetRequired: false, window: "renewed",
      messages: [message("1", "edited"), { ...message("3"), deleted: true, body: "" }, message("5"), message("6", "reply", "parent")],
    }) : path.includes("cursor=") ? json(page([message("1"), message("2")])) :
      json(page([message("3"), message("4")], "older")));
    vi.stubGlobal("fetch", fetch);
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.loading).toBe(false));
    await act(async () => { await result.current.older(); });
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["1", "2", "3", "4"]);
    rerender({ ...initial, minimumRevision: 4 });
    await waitFor(() => expect(result.current.page?.revision).toBe(4));
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["1", "2", "3", "4", "5"]);
    expect(result.current.page?.messages[0].body).toBe("edited");
    expect(result.current.page?.messages[2].deleted).toBe(true);
    expect(result.current.page?.window).toBe("renewed");
    expect(fetch.mock.calls.filter(([path]) => path.endsWith("/messages"))).toHaveLength(1);
  });

  it("refreshes every loaded page after an expired journal, rather than merging stale snapshots", async () => {
    let reset = false;
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ changes: [], revision: 2, resetRequired: true }) :
      path.includes("cursor=") ? json(page(reset ? [message("2", "fresh older")] : [message("1", "old hidden")], null, 2)) :
      json(page([message("3")], "opaque+cursor", reset ? 2 : 1))));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.loading).toBe(false));
    await act(async () => { await result.current.older(); });
    reset = true;
    rerender({ ...initial, refresh: 1, minimumRevision: 2 });
    await waitFor(() => expect(result.current.page?.revision).toBe(2));
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["2", "3"]);
    expect(fetch).toHaveBeenCalledWith(expect.stringContaining("minimumRevision=2"), expect.any(Object));
    expect(fetch).toHaveBeenCalledWith(expect.stringContaining("cursor=opaque%2Bcursor"), expect.any(Object));
  });

  it("applies tightened history even when no message revision changed", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ changes: [], messages: [], revision: 1, resetRequired: false, historyFrom: message("2").createdAt }) :
      json(page([message("1"), message("2")]))));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    rerender({ ...initial, refresh: 1 });
    await waitFor(() => expect(result.current.page?.messages).toHaveLength(1));
    expect(result.current.page?.messages[0].id).toBe("2");
  });

  it("clears content and expires the session on a catch-up denial", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ code: "authentication_required", message: "Sign in again." }, 401) : json(page([message("1")]))));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    rerender({ ...initial, refresh: 1 });
    await waitFor(() => expect(initial.expired).toHaveBeenCalledTimes(1));
    expect(result.current.page).toBeNull();
  });

  it("does not claim an announced revision was loaded from an older response", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ changes: [], messages: [], revision: 1, resetRequired: false }) : json(page([message("1")]))));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    rerender({ ...initial, minimumRevision: 2 });
    await waitFor(() => expect(result.current.preparing).toBe(true));
    expect(result.current.page).toBeNull();
    expect(result.current.error).toContain("still being synchronized");
  });

  it("updates an open thread's parent and closes it when the parent is deleted", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ? json({
      changes: [], messages: [{ ...message("1"), deleted: true }], revision: 2, resetRequired: false,
    }) : json({ ...page([message("2", "reply", "1")]), parentMessage: message("1") })));
    const initial = { ...feedInput(), parent: "1", path: "/api/conversations/chat/messages?parent=1" };
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(initial.syncParent).toHaveBeenCalled());
    rerender({ ...initial, refresh: 1 });
    await waitFor(() => expect(initial.unavailable).toHaveBeenCalledTimes(1));
    expect(result.current.page).toBeNull();
  });

  it("retains revision 40 through an older response at 41 and converges to the announced 42", async () => {
    vi.useFakeTimers();
    let reads = 0;
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ changes: [], messages: [message("1", ++reads === 1 ? "not yet current" : "current")],
        revision: reads === 1 ? 41 : 42, resetRequired: false, access: access() }) :
      json({ ...page([message("1", "loaded")], "older", 40), access: access() })));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await act(async () => {});
    rerender({ ...initial, minimumRevision: 42 });
    await act(async () => {});
    expect(result.current.page?.revision).toBe(40);
    expect(result.current.page?.messages[0].body).toBe("loaded");
    expect(result.current.page?.cursor).toBe("older");
    expect(result.current.preparing).toBe(true);
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(result.current.page?.revision).toBe(42);
    expect(result.current.page?.messages[0].body).toBe("current");
    expect(result.current.error).toBe("");
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(initial.unavailable).not.toHaveBeenCalled();
  });

  it("applies a fresh tightened history boundary even when the API is behind", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ code: "projection_behind", message: "Synchronizing.", access: access({ historyFrom: message("2").createdAt }) }, 503) :
      json({ ...page([message("1"), message("2")], null, 42), access: access() })));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    rerender({ ...initial, minimumRevision: 43 });
    await waitFor(() => expect(result.current.preparing).toBe(true));
    expect(result.current.page?.revision).toBe(42);
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["2"]);
  });

  it.each(["authorization_stale", "primary_read_unavailable", "primary_read_invalid", "resource_mapping_changed",
    "writer_fenced"])("clears retained content on %s rather than treating it as replica lag", async code => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ code, message: "Unverified.", access: access() }, 503) :
      json({ ...page([message("1")]), access: access() })));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    rerender({ ...initial, minimumRevision: 2 });
    await waitFor(() => expect(result.current.error).toBe("Unverified."));
    expect(result.current.page).toBeNull();
  });

  it("expires retained content at the verified deadline even while synchronization is retrying", async () => {
    vi.useFakeTimers();
    const proof = access({ validUntil: new Date(Date.now() + 1100).toISOString() });
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ code: "projection_behind", message: "Synchronizing.", access: proof }, 503) :
      json({ ...page([message("1")]), access: proof })));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await act(async () => {});
    rerender({ ...initial, minimumRevision: 2 });
    await act(async () => {});
    expect(result.current.page).not.toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(1100); });
    expect(result.current.page).toBeNull();
    expect(result.current.error).toContain("verified");
  });

  it("does not keep content across a changed epoch during lag", async () => {
    vi.stubGlobal("fetch", vi.fn(async (path: string) => path.includes("/changes") ?
      json({ code: "projection_behind", message: "Synchronizing.", access: access({ epoch: 2 }) }, 503) :
      json({ ...page([message("1")]), access: access() })));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page).not.toBeNull());
    rerender({ ...initial, minimumRevision: 2 });
    await waitFor(() => expect(result.current.page).toBeNull());
    expect(result.current.error).toContain("verified");
  });

  it("keeps the observed revision floor after an error clears the loaded page", async () => {
    let snapshots = 0;
    const fetch = vi.fn(async (path: string) => path.includes("/changes") ?
      json({ code: "authorization_stale", message: "Authority unavailable." }, 503) :
      json({ ...page([message("1")], null, ++snapshots === 1 ? 42 : 41), access: access() }));
    vi.stubGlobal("fetch", fetch);
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await waitFor(() => expect(result.current.page?.revision).toBe(42));
    rerender({ ...initial, refresh: 1 });
    await waitFor(() => expect(result.current.page).toBeNull());
    act(() => { result.current.retry(); });
    await waitFor(() => expect(snapshots).toBe(2));
    expect(result.current.page).toBeNull();
    expect(fetch.mock.calls.at(-1)?.[0]).toContain("minimumRevision=42");
  });

  it("filters messages at their seven-day retention boundary even without another hint", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-21T10:00:00Z"));
    const boundary = Date.now() - 7 * 86400_000;
    const expiring = { ...message("1"), createdAt: new Date(boundary + 1000).toISOString() };
    const proof = access({ historyFrom: new Date(boundary).toISOString() });
    vi.stubGlobal("fetch", vi.fn(async () => json({ ...page([expiring, message("2")]), access: proof })));
    const { result } = renderHook(() => useMessageFeed(feedInput()));
    await act(async () => {});
    expect(result.current.page?.messages).toHaveLength(2);
    await act(async () => { await vi.advanceTimersByTimeAsync(1001); });
    expect(result.current.page?.messages.map(item => item.id)).toEqual(["2"]);
    expect(result.current.error).toBe("");
  });

  it("coalesces hints arriving during an in-flight read without aborting or starting another read", async () => {
    let finish!: (value: Response) => void;
    const fetch = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; }));
    vi.stubGlobal("fetch", fetch);
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    for (let revision = 1; revision <= 20; revision++) rerender({ ...initial, minimumRevision: revision });
    expect(fetch).toHaveBeenCalledTimes(1);
    await act(async () => { finish(json({ ...page([message("1")], null, 20), access: access() })); });
    expect(result.current.page?.revision).toBe(20);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("does not restart an exhausted recovery budget when more hints arrive; manual retry can recover", async () => {
    vi.useFakeTimers();
    let ready = false;
    const fetch = vi.fn(async () => ready ? json({ ...page([message("1")], null, 100), access: access() }) :
      json({ code: "projection_behind", message: "Synchronizing.", access: access() }, 503));
    vi.stubGlobal("fetch", fetch);
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(120_000); });
    const reads = fetch.mock.calls.length;
    expect(reads).toBeLessThanOrEqual(9);
    rerender({ ...initial, minimumRevision: 90 });
    await act(async () => { await vi.advanceTimersByTimeAsync(300_000); });
    expect(fetch).toHaveBeenCalledTimes(reads);
    ready = true;
    await act(async () => { result.current.retry(); });
    expect(result.current.page?.revision).toBe(100);
    expect(result.current.error).toBe("");
  });

  it("ignores a late response from a previous conversation", async () => {
    let finish!: (value: Response) => void;
    vi.stubGlobal("fetch", vi.fn((path: string) => path.includes("/other/") ?
      Promise.resolve(json({ ...page([message("2")]), access: access() })) :
      new Promise<Response>(resolve => { finish = resolve; })));
    const initial = feedInput();
    const { result, rerender } = renderHook(input => useMessageFeed(input), { initialProps: initial });
    rerender({ ...initial, path: "/api/conversations/other/messages", changesPath: "/api/conversations/other/changes" });
    await waitFor(() => expect(result.current.page?.messages[0].id).toBe("2"));
    await act(async () => { finish(json(page([message("1")], null, 99))); });
    expect(result.current.page?.messages[0].id).toBe("2");
    expect(result.current.page?.revision).toBe(1);
  });
});

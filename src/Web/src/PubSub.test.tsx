// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { useRealtime } from "./useRealtime";
import type { Session } from "./api";

const sdk = vi.hoisted(() => ({
  handlers: new Map<string, (event?: unknown) => void>(),
  accessors: [] as (() => Promise<string>)[],
  start: vi.fn(), stop: vi.fn(),
}));
vi.mock("@azure/web-pubsub-client", () => ({
  WebPubSubClient: class {
    private readonly options: { getClientAccessUrl: () => Promise<string> };
    constructor(options: { getClientAccessUrl: () => Promise<string> }) {
      this.options = options;
      sdk.accessors.push(options.getClientAccessUrl);
    }
    on(name: string, callback: (event?: unknown) => void) { sdk.handlers.set(name, callback); }
    async start() { await this.options.getClientAccessUrl(); return sdk.start(); }
    stop() { return sdk.stop(); }
  },
}));
const session: Session = { id: "alex", name: "Alex", csrf: "csrf", environment: "Development", source: "Microsoft Teams" };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
afterEach(() => {
  cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); vi.restoreAllMocks(); sdk.handlers.clear(); sdk.accessors.length = 0;
  sdk.start.mockReset(); sdk.stop.mockReset();
});

it("uses the Web PubSub SDK, re-negotiates credentials and consumes JSON server-message hints", async () => {
  const fetch = vi.fn(async () => json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" }));
  vi.stubGlobal("fetch", fetch);
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { result, unmount } = renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(1));
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(await sdk.accessors[0]()).toBe("wss://synthetic.webpubsub.azure.com/client");
  expect(fetch).toHaveBeenCalledTimes(2);
  act(() => { sdk.handlers.get("connected")?.(); });
  expect(result.current.status).toBe("Live");
  act(() => { sdk.handlers.get("server-message")?.({ message: { data: { conversationId: "approved-chat", revision: 5 } } }); });
  await waitFor(() => expect(callbacks.hints).toHaveBeenCalledExactlyOnceWith([{ conversationId: "approved-chat", revision: 5 }]));
  act(() => { sdk.handlers.get("connected")?.(); });
  expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
  unmount();
  expect(sdk.stop).toHaveBeenCalledTimes(1);
  act(() => { sdk.handlers.get("connected")?.(); });
  expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
  expect(callbacks.error).not.toHaveBeenCalled();
});

it("waits for network recovery before negotiating and never reports Live while offline", async () => {
  vi.useFakeTimers();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  const fetch = vi.fn(async () =>
    json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" }));
  vi.stubGlobal("fetch", fetch);
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { result } = renderHook(() => useRealtime(session, callbacks));
  await act(async () => {});
  expect(result.current.status).toBe("Offline");
  expect(fetch).not.toHaveBeenCalled();
  expect(sdk.start).not.toHaveBeenCalled();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  await act(async () => { window.dispatchEvent(new Event("online")); });
  expect(fetch).toHaveBeenCalledTimes(1);
  act(() => { sdk.handlers.get("connected")?.(); });
  expect(result.current.status).toBe("Live");
  expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
  act(() => {
    window.dispatchEvent(new Event("offline"));
    sdk.handlers.get("connected")?.();
    sdk.handlers.get("stopped")?.();
    vi.advanceTimersByTime(400_000);
  });
  expect(result.current.status).toBe("Offline");
  expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
  expect(callbacks.error).not.toHaveBeenCalled();
});

it("checks session authorization after a Web PubSub disconnect", async () => {
  vi.stubGlobal("fetch", vi.fn(async (path: string) => path === "/api/session" ?
    json({ code: "authentication_required", message: "Sign in again." }, 401) :
    json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" })));
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(1));
  act(() => { sdk.handlers.get("disconnected")?.(); });
  await waitFor(() => expect(callbacks.expired).toHaveBeenCalledTimes(1));
  expect(callbacks.hints).not.toHaveBeenCalled();
});

it("does not claim to be reconnecting after the SDK stops retrying", async () => {
  vi.stubGlobal("fetch", vi.fn(async (path: string) => path === "/api/session" ? json(session) :
    json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" })));
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { result } = renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(1));
  act(() => { sdk.handlers.get("connected")?.(); sdk.handlers.get("disconnected")?.(); });
  expect(result.current.status).toBe("Reconnecting");
  act(() => { sdk.handlers.get("stopped")?.(); });
  expect(result.current.status).toBe("Synchronization unavailable");
  expect(callbacks.error).toHaveBeenCalledWith("Live updates stopped reconnecting. Select Reconnect to try again.");
});

it("catches up missed changes after a successful manual reconnect", async () => {
  vi.stubGlobal("fetch", vi.fn(async () =>
    json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" })));
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { result } = renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(1));
  act(() => { sdk.handlers.get("connected")?.(); });
  expect(callbacks.reconcile).not.toHaveBeenCalled();
  act(() => { result.current.reconnect(); });
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(2));
  expect(callbacks.reconcile).not.toHaveBeenCalled();
  act(() => { sdk.handlers.get("connected")?.(); });
  expect(callbacks.reconcile).toHaveBeenCalledTimes(1);
});

it("surfaces SDK startup failure instead of silently changing to polling", async () => {
  sdk.start.mockRejectedValue(new Error("Synthetic WebSocket unavailable"));
  const fetch = vi.fn(async () => json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" }));
  vi.stubGlobal("fetch", fetch);
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { result } = renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(result.current.status).toBe("Synchronization unavailable"));
  expect(callbacks.error).toHaveBeenCalledWith("Synthetic WebSocket unavailable");
  expect(callbacks.reconcile).not.toHaveBeenCalled();
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("stops a client that finishes startup after the hook has been disposed", async () => {
  let finish!: () => void;
  sdk.start.mockImplementation(() => new Promise<void>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", vi.fn(async () =>
    json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" })));
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { unmount } = renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(1));
  unmount();
  expect(sdk.stop).toHaveBeenCalledTimes(1);
  await act(async () => { finish(); });
  expect(sdk.stop).toHaveBeenCalledTimes(2);
  act(() => { sdk.handlers.get("connected")?.(); sdk.handlers.get("stopped")?.(); });
  expect(callbacks.reconcile).not.toHaveBeenCalled();
  expect(callbacks.error).not.toHaveBeenCalled();
});

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
    constructor(options: { getClientAccessUrl: () => Promise<string> }) { sdk.accessors.push(options.getClientAccessUrl); }
    on(name: string, callback: (event?: unknown) => void) { sdk.handlers.set(name, callback); }
    start() { return sdk.start(); }
    stop() { return sdk.stop(); }
  },
}));
const session: Session = { id: "alex", name: "Alex", csrf: "csrf", environment: "Development", source: "Microsoft Teams" };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
afterEach(() => {
  cleanup(); vi.unstubAllGlobals(); sdk.handlers.clear(); sdk.accessors.length = 0;
  sdk.start.mockReset(); sdk.stop.mockReset();
});

it("uses the Web PubSub SDK, re-negotiates credentials and consumes JSON server-message hints", async () => {
  const fetch = vi.fn(async () => json({ transport: "webpubsub", url: "wss://synthetic.webpubsub.azure.com/client" }));
  vi.stubGlobal("fetch", fetch);
  const callbacks = { hints: vi.fn(), reconcile: vi.fn(), expired: vi.fn(), error: vi.fn() };
  const { result, unmount } = renderHook(() => useRealtime(session, callbacks));
  await waitFor(() => expect(sdk.start).toHaveBeenCalledTimes(1));
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

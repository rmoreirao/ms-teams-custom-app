// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";
import { useSendQueue } from "./useSendQueue";
import type { SendCommand, Session } from "./api";

const session: Session = { id: "alex", name: "Alex", csrf: "csrf", environment: "Development", source: "Microsoft Teams" };
const command = (id: string): SendCommand => ({ operationId: id, body: id, parentId: null, window: "window" });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it("dispatches sixty requests immediately without waiting for responses or timers", () => {
  const fetch = vi.fn(() => new Promise<Response>(() => {}));
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() => useSendQueue(session, true));
  act(() => {
    for (let i = 0; i < 60; i++) void result.current(i % 2 ? "first-chat" : "second-chat", command(String(i)));
  });
  expect(fetch).toHaveBeenCalledTimes(60);
});

it("rejects new requests on sign-out without losing an in-flight confirmation", async () => {
  let finish!: (response: Response) => void;
  const fetch = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(({ current }: { current: Session | null }) => useSendQueue(current, true),
    { initialProps: { current: session as Session | null } });
  const first = result.current("chat", command("first"));
  rerender({ current: null });
  expect(await result.current("chat", command("second"))).toMatchObject({ state: "rejected" });
  finish(new Response(JSON.stringify({ id: "first", state: "confirmed", messageId: "sent", error: null }), { status: 201 }));
  expect(await first).toMatchObject({ state: "confirmed" });
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("cancels only requests that have not been dispatched", async () => {
  const fetch = vi.fn(() => new Promise<Response>(() => {}));
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() => useSendQueue(session, true));
  const batch = new AbortController();
  void result.current("chat", command("first"), batch.signal);
  batch.abort();
  expect(await result.current("chat", command("second"), batch.signal)).toMatchObject({
    state: "rejected", error: "Cancelled before dispatch. No message was sent.",
  });
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("does not dispatch or automatically resume requests made while offline", async () => {
  const fetch = vi.fn();
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(({ online }: { online: boolean }) => useSendQueue(session, online),
    { initialProps: { online: false } });
  expect(await result.current("chat", command("offline"))).toMatchObject({ state: "rejected" });
  rerender({ online: true });
  expect(fetch).not.toHaveBeenCalled();
});

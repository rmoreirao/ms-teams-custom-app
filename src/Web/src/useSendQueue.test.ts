// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";
import { useSendQueue } from "./useSendQueue";
import type { SendCommand, Session } from "./api";

const session: Session = { id: "alex", name: "Alex", csrf: "csrf", environment: "Development", source: "Microsoft Teams" };
const command = (id: string): SendCommand => ({ operationId: id, body: id, parentId: null, window: "window" });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

it("paces requests across conversations without awaiting earlier responses", async () => {
  vi.useFakeTimers();
  const fetch = vi.fn(() => new Promise<Response>(() => {}));
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() => useSendQueue(session, true));
  act(() => { void result.current("first-chat", command("first")); void result.current("second-chat", command("second")); });
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(1500); });
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls[0]).toContain("/api/conversations/first-chat/messages");
  expect(fetch.mock.calls[1]).toContain("/api/conversations/second-chat/messages");
});

it("cancels undispatched requests on sign-out without losing an in-flight confirmation", async () => {
  vi.useFakeTimers();
  let finish!: (response: Response) => void;
  const fetch = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(({ current }: { current: Session | null }) => useSendQueue(current, true),
    { initialProps: { current: session as Session | null } });
  const first = result.current("chat", command("first"));
  const second = result.current("chat", command("second"));
  rerender({ current: null });
  expect(await second).toMatchObject({ id: "second", state: "rejected", error: expect.stringContaining("session closed") });
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(fetch).toHaveBeenCalledTimes(1);
  finish(new Response(JSON.stringify({ id: "first", state: "confirmed", messageId: "sent", error: null }), { status: 201 }));
  expect(await first).toMatchObject({ id: "first", state: "confirmed" });
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("stops queued batch requests without aborting an already-dispatched request", async () => {
  vi.useFakeTimers();
  let finish!: (response: Response) => void;
  const fetch = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; }));
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() => useSendQueue(session, true));
  const batch = new AbortController();
  const first = result.current("chat", command("first"), batch.signal);
  const second = result.current("chat", command("second"), batch.signal);
  batch.abort();
  await act(async () => { await vi.advanceTimersByTimeAsync(1500); });
  expect(await second).toMatchObject({ id: "second", state: "rejected", error: "Cancelled before dispatch. No message was sent." });
  expect(fetch).toHaveBeenCalledTimes(1);
  finish(new Response(JSON.stringify({ id: "first", state: "confirmed", messageId: "sent", error: null }), { status: 201 }));
  expect(await first).toMatchObject({ id: "first", state: "confirmed" });
});

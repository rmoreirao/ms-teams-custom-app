// @vitest-environment jsdom
import React from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App } from "./App";
import { CacheDiagnostics } from "./CacheDiagnostics";
import { useMessageFeed } from "./useMessageFeed";
import type { CacheCleanup, CacheSnapshot, MessagePage, ReadObservation } from "./api";

vi.mock("./useRealtime", () => ({
  useRealtime: () => ({ status: "Live", online: true, reconnect: () => {} }),
}));
const now = () => new Date().toISOString();
const message = { id: "one", actorId: "sam", author: "Sam", body: "Loaded context", createdAt: now(),
  modifiedAt: now(), parentId: null, deleted: false };
const evidence = (hit = false) => ({ kind: "page" as const, observedAt: now(), hydrationRequested: false,
  steps: [{ layer: "redis", outcome: hit ? "hit" : "miss", count: hit ? 1 : 0, milliseconds: 1 },
    ...(hit ? [] : [{ layer: "cosmos", outcome: "content-query", count: 1, milliseconds: 2 }])] });
const page = (hit = false): MessagePage => ({ messages: [message], cursor: null, parentMessage: null,
  revision: 1, window: "window", diagnostics: evidence(hit),
  access: { epoch: 1, historyFrom: new Date(Date.now() - 86400_000).toISOString(), validUntil: new Date(Date.now() + 300000).toISOString() } });
const snapshot = (): CacheSnapshot => ({ observedAt: now(), revision: 1, generation: 0, cosmosMessages: 1,
  cosmosCountComplete: true, coverageComplete: true, readViewComplete: false, cosmosError: null,
  redis: { status: "available", pages: 1, maximumTtlSeconds: 12, complete: true }, cleanup: null });
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
beforeEach(() => {
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.setAttribute("open", ""); } },
    close: { configurable: true, value: function (this: HTMLDialogElement) {
      this.removeAttribute("open"); this.dispatchEvent(new Event("close"));
    } },
  });
});
afterEach(() => {
  cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers();
  Reflect.deleteProperty(HTMLDialogElement.prototype, "showModal");
  Reflect.deleteProperty(HTMLDialogElement.prototype, "close");
});
function fixture(options: { unknown?: boolean; partial?: boolean; expired?: boolean; busy?: boolean } = {}) {
  const commands: { operationId: string; cosmos: boolean; redis: boolean }[] = [];
  const reads: string[] = [];
  let operation: CacheCleanup | null = null;
  let warm = false;
  const fetch = vi.fn(async (path: string, init?: RequestInit) => {
    if (path === "/api/session") return json({ id: "alex", name: "Alex", environment: "Development", source: "Microsoft Teams", csrf: "csrf" });
    if (path === "/api/conversations") return json([{ id: "group", title: "Project group", kind: "chat", team: null, revision: 1 }]);
    if (path.includes("/cache-diagnostics/cleanup") && init?.method === "POST") {
      expect(new Headers(init.headers).get("X-CSRF-TOKEN")).toBe("csrf");
      const command = JSON.parse(String(init.body));
      commands.push(command);
      if (options.busy && commands.length === 1) {
        operation = { id: crypto.randomUUID(), cosmos: true, redis: false, targetRevision: 1, startedAt: now(),
          cosmosState: "in-progress", redisState: "not-selected", cosmosError: null, redisError: null,
          removedMessages: 0, removedPages: 0, complete: false };
        return json({ code: "cache_cleanup_busy", message: "Another cleanup is active." }, 409);
      }
      operation = { id: command.operationId, cosmos: command.cosmos, redis: command.redis, targetRevision: 1, startedAt: now(),
        cosmosState: command.cosmos ? "completed" : "not-selected", redisState: command.redis ? options.partial && commands.length === 1 ? "failed" : "completed" : "not-selected",
        cosmosError: null, redisError: options.partial && commands.length === 1 ? "cache_eviction_unavailable" : null,
        removedMessages: command.cosmos ? 1 : 0, removedPages: command.redis ? 1 : 0,
        complete: !(options.partial && commands.length === 1) };
      warm = false;
      if (options.unknown) { options.unknown = false; throw new TypeError("Lost response"); }
      return json(operation, operation.complete ? 200 : 202);
    }
    if (path.includes("/cache-diagnostics/cleanup/")) {
      if (options.expired) {
        operation = null;
        return json({ code: "cache_operation_expired", message: "The original operation expired. Inspect storage before a new cleanup." }, 410);
      }
      return json(operation);
    }
    if (path.endsWith("/cache-diagnostics")) return json({ ...snapshot(), cleanup: operation });
    if (path.includes("/messages")) {
      reads.push(path); const value = page(warm); warm = true; return json(value);
    }
    if (path.includes("/changes?")) {
      reads.push(path); return json({ changes: [], messages: [], resetRequired: false, revision: 1, window: "window",
        diagnostics: { kind: "delta", observedAt: now(), hydrationRequested: false,
          steps: [{ layer: "cosmos", outcome: "change-query", count: 0, milliseconds: 1 }] } });
    }
    return json({ code: "unexpected", message: `Unexpected request ${path}` }, 500);
  });
  vi.stubGlobal("fetch", fetch);
  return { commands, reads, fetch };
}
async function openApp() {
  render(<App />);
  fireEvent.click(await screen.findByRole("button", { name: "Open chat: Project group" }, { timeout: 5000 }));
  await screen.findByText("Loaded context", { exact: true }, { timeout: 5000 });
  fireEvent.change(screen.getByRole("textbox", { name: "Write a message" }), { target: { value: "Keep my draft" } });
  fireEvent.click(screen.getByRole("button", { name: "Cache Diagnostics" }));
  const dialog = screen.getByRole("dialog", { name: "Cache Diagnostics" });
  await waitFor(() => expect(within(dialog).getByRole("button", { name: "Inspect storage" })).toBeEnabled());
  return within(dialog);
}
it("inspection does not read messages or clean storage, defaults to no selections, and preserves the draft", async () => {
  const state = fixture();
  const dialog = await openApp();
  expect(state.reads).toHaveLength(1);
  expect(dialog.getByRole("checkbox", { name: "Cosmos message copies" })).not.toBeChecked();
  expect(dialog.getByRole("checkbox", { name: "Redis recent pages" })).not.toBeChecked();
  expect(dialog.getByRole("button", { name: "Clean selected caches" })).toBeDisabled();
  fireEvent.click(dialog.getByRole("button", { name: "Inspect storage" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Inspect storage" })).toBeEnabled());
  expect(state.reads).toHaveLength(1);
  expect(state.commands).toHaveLength(0);
  fireEvent.click(dialog.getByRole("button", { name: "Close" }));
  await waitFor(() => expect(state.reads.some(path => path.includes("/changes?"))).toBe(true));
  expect(screen.getByRole("textbox", { name: "Write a message" })).toHaveValue("Keep my draft");
});
it("requires store selection and confirmation, then validates a fresh Redis miss followed by a hit", async () => {
  const state = fixture();
  const dialog = await openApp();
  fireEvent.click(dialog.getByRole("checkbox", { name: "Redis recent pages" }));
  fireEvent.click(dialog.getByRole("button", { name: "Clean selected caches" }));
  expect(state.commands).toHaveLength(0);
  fireEvent.click(dialog.getByRole("button", { name: "Cancel cleanup" }));
  expect(state.commands).toHaveLength(0);
  fireEvent.click(dialog.getByRole("button", { name: "Clean selected caches" }));
  fireEvent.click(dialog.getByRole("button", { name: "Confirm cleanup" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeEnabled());
  expect(state.commands).toEqual([{ operationId: expect.any(String), cosmos: false, redis: true }]);
  expect(state.reads).toHaveLength(1);
  fireEvent.click(dialog.getByRole("button", { name: "Reload and validate" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeEnabled());
  expect(state.reads).toHaveLength(2);
  expect(state.reads.every(path => !path.includes("changes"))).toBe(true);
  expect(dialog.getAllByText("miss").length).toBeGreaterThan(0);
  fireEvent.click(dialog.getByRole("button", { name: "Reload and validate" }));
  await waitFor(() => expect(state.reads).toHaveLength(3));
  expect(dialog.getAllByText("hit").length).toBeGreaterThan(0);
  expect(screen.getByRole("textbox", { name: "Write a message" })).toHaveValue("Keep my draft");
});
it.each([{ unknown: true }, { partial: true }])("checks and resumes only the original operation after %j", async options => {
  const state = fixture(options);
  const dialog = await openApp();
  fireEvent.click(dialog.getByRole("checkbox", { name: "Cosmos message copies" }));
  fireEvent.click(dialog.getByRole("checkbox", { name: "Redis recent pages" }));
  fireEvent.click(dialog.getByRole("button", { name: "Clean selected caches" }));
  fireEvent.click(dialog.getByRole("button", { name: "Confirm cleanup" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Check original operation" })).toBeEnabled());
  expect(dialog.getByRole("button", { name: "Clean selected caches" })).toBeDisabled();
  expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeDisabled();
  fireEvent.click(dialog.getByRole("button", { name: "Check original operation" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Inspect storage" })).toBeEnabled());
  expect(state.commands).toHaveLength(1);
  if (options.partial) {
    fireEvent.click(dialog.getByRole("button", { name: "Resume original cleanup" }));
    await waitFor(() => expect(state.commands).toHaveLength(2));
    expect(state.commands[1]).toEqual(state.commands[0]);
  } else expect(dialog.queryByRole("button", { name: "Resume original cleanup" })).not.toBeInTheDocument();
});
it("reports pending hydration without inventing emptiness and can cancel further validation reads on close", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(snapshot())));
  const validate = vi.fn(async () => ({ state: "pending" as const, access: null, retryAfterMs: 1000, diagnostics: evidence() }));
  const openChanged = vi.fn();
  render(<CacheDiagnostics conversationId="chat" title="Chat" csrf="csrf" online retainedMessages={0}
    latestRead={null} openChanged={openChanged} validate={validate} authenticationRequired={vi.fn()} />);
  fireEvent.click(screen.getByRole("button", { name: "Cache Diagnostics" }));
  const dialog = within(screen.getByRole("dialog"));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Reload and validate" })).toBeEnabled());
  fireEvent.click(dialog.getByRole("button", { name: "Reload and validate" }));
  await screen.findByText("History is pending background hydration. No empty-chat conclusion is drawn.");
  fireEvent.click(dialog.getByRole("button", { name: "Close" }));
  expect(validate).toHaveBeenCalledTimes(1);
  expect(openChanged).toHaveBeenLastCalledWith(false);
});
it("allows a newly confirmed cleanup after the server definitively reports the original operation expired", async () => {
  const state = fixture({ partial: true, expired: true });
  const dialog = await openApp();
  fireEvent.click(dialog.getByRole("checkbox", { name: "Cosmos message copies" }));
  fireEvent.click(dialog.getByRole("checkbox", { name: "Redis recent pages" }));
  fireEvent.click(dialog.getByRole("button", { name: "Clean selected caches" }));
  fireEvent.click(dialog.getByRole("button", { name: "Confirm cleanup" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Check original operation" })).toBeEnabled());
  fireEvent.click(dialog.getByRole("button", { name: "Check original operation" }));
  await dialog.findByText("The original operation expired. Inspect storage before a new cleanup.");
  fireEvent.click(dialog.getByRole("button", { name: "Inspect storage" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Clean selected caches" })).toBeEnabled());
  expect(state.commands).toHaveLength(1);
  fireEvent.click(dialog.getByRole("button", { name: "Clean selected caches" }));
  expect(state.commands).toHaveLength(1);
  fireEvent.click(dialog.getByRole("button", { name: "Confirm cleanup" }));
  await waitFor(() => expect(state.commands).toHaveLength(2));
  expect(state.commands[1].operationId).not.toBe(state.commands[0].operationId);
});
it("can discover and resume the winning operation after a concurrent cleanup rejects this attempt", async () => {
  const state = fixture({ busy: true });
  const dialog = await openApp();
  fireEvent.click(dialog.getByRole("checkbox", { name: "Cosmos message copies" }));
  fireEvent.click(dialog.getByRole("button", { name: "Clean selected caches" }));
  fireEvent.click(dialog.getByRole("button", { name: "Confirm cleanup" }));
  await dialog.findByText(/Another cleanup is active/);
  fireEvent.click(dialog.getByRole("button", { name: "Inspect storage" }));
  await waitFor(() => expect(dialog.getByRole("button", { name: "Resume original cleanup" })).toBeEnabled());
  expect(state.commands).toHaveLength(1);
  fireEvent.click(dialog.getByRole("button", { name: "Resume original cleanup" }));
  await waitFor(() => expect(state.commands).toHaveLength(2));
  expect(state.commands[1].operationId).not.toBe(state.commands[0].operationId);
});
it("pauses automatic content reads and clears old history on a cache-reset delta", async () => {
  const observed = vi.fn<(value: ReadObservation) => void>();
  let requests = 0;
  const fetch = vi.fn(async (path: string) => {
    requests++;
    return path.includes("/changes?") ? json({ changes: [], messages: [], revision: 3, resetRequired: true, cacheReset: true })
      : requests === 1 ? json({ ...page(), cursor: "older" }) : json({ state: "pending", retryAfterMs: 30000, access: page().access }, 202);
  });
  vi.stubGlobal("fetch", fetch);
  const input = { path: "/api/conversations/chat/messages", changesPath: "/api/conversations/chat/changes",
    parent: null, refresh: 0, minimumRevision: 0, paused: false, observed, unavailable: vi.fn(), expired: vi.fn(), syncParent: vi.fn() };
  const { result, rerender } = renderHook(value => useMessageFeed(value), { initialProps: input });
  await waitFor(() => expect(result.current.page?.messages).toHaveLength(1));
  rerender({ ...input, paused: true, refresh: 1 });
  await act(async () => { await result.current.older(); });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(result.current.page?.messages).toHaveLength(1);
  rerender({ ...input, refresh: 2 });
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(3));
  expect(result.current.page).toBeNull();
  expect(result.current.loading).toBe(true);
  expect(observed).toHaveBeenCalled();
});
it("still expires loaded content authority while automatic content reads are paused", async () => {
  vi.useFakeTimers();
  const value = page();
  value.access!.validUntil = new Date(Date.now() + 1000).toISOString();
  const fetch = vi.fn(async () => json(value));
  vi.stubGlobal("fetch", fetch);
  const input = { path: "/api/conversations/chat/messages", changesPath: "/api/conversations/chat/changes",
    parent: null, refresh: 0, minimumRevision: 0, paused: false, unavailable: vi.fn(), expired: vi.fn(), syncParent: vi.fn() };
  const { result, rerender } = renderHook(properties => useMessageFeed(properties), { initialProps: input });
  await act(async () => {});
  expect(result.current.page?.messages).toHaveLength(1);
  rerender({ ...input, paused: true });
  await act(async () => { await vi.advanceTimersByTimeAsync(1001); });
  expect(result.current.page).toBeNull();
  expect(result.current.error).toContain("Current access must be verified");
  expect(fetch).toHaveBeenCalledTimes(1);
});

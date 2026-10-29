// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App } from "./App";
import { ChannelBrowser, useChannelBrowser } from "./ChannelBrowser";
import type { ChannelOperation, Conversation, Message, RealtimeEvent, Session } from "./api";

const realtime = vi.hoisted(() => ({
  status: "Live", online: true, reconnect: vi.fn(),
  callbacks: null as null | { events?: (items: RealtimeEvent[]) => void; reconcile: () => void },
}));
vi.mock("./useRealtime", () => ({
  useRealtime: (_session: Session | null, callbacks: typeof realtime.callbacks) => {
    realtime.callbacks = callbacks;
    return { status: realtime.status, online: realtime.online, reconnect: realtime.reconnect };
  },
}));
const session: Session = { id: "alex", name: "Alex", csrf: "csrf", environment: "Development",
  source: "Microsoft Teams", canBrowseChannels: true, channelRegistry: true };
const channel: Conversation = { id: "channel", title: "General", kind: "channel", team: "Product",
  teamId: "team", revision: 1, canPost: true, canReply: true };
const chat: Conversation = { id: "chat", title: "Sam", kind: "chat", team: null, revision: 1 };
const discovered = { id: "source-channel", teamId: "team", name: "General", teamName: "Product", selection: "opaque-token" };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const operation = (id: string, kind: ChannelOperation["kind"], overrides: Partial<ChannelOperation> = {}): ChannelOperation => ({
  id, kind, state: "ready", revision: 1, error: null, cursor: null, teams: null, channels: null, conversation: null, ...overrides,
});
const event = (id: string, revision = 2): RealtimeEvent => ({
  type: "channel.operation.changed", schemaVersion: 1, sequence: revision, operationId: id, revision,
});
const message = (id: string, parentId: string | null = null): Message => ({
  id, parentId, actorId: "sam", author: "Sam", body: `Message ${id}`, deleted: false,
  createdAt: new Date(Date.now() - 3_600_000).toISOString(), modifiedAt: new Date(Date.now() - 3_600_000).toISOString(),
});
const proof = (canSend = true) => ({ epoch: 1, canSend,
  historyFrom: new Date(Date.now() - 86400_000).toISOString(), validUntil: new Date(Date.now() + 300_000).toISOString() });
const page = (messages: Message[] = [], parentMessage: Message | null = null, canSend = true) => ({
  messages, parentMessage, revision: 1, window: "window", cursor: null, access: proof(canSend),
});
function appApi(input: { canSend?: boolean; catalog?: Conversation[]; pending?: boolean } = {}) {
  const root = { ...message("root"), htmlBody: "<p><strong>Original discussion</strong><img src=x onerror=alert(1)><script>bad()</script></p>" };
  const fetch = vi.fn(async (path: string, init?: RequestInit): Promise<Response> => {
    if (path === "/api/session") return json(session);
    if (path === "/api/conversations") return json(input.catalog ?? [chat, channel]);
    if (path.startsWith("/api/conversations/") && path.includes("/messages") && !init?.method) {
      if (input.pending) return json({ state: "pending", retryAfterMs: 1000, access: proof() }, 202);
      const body = path.includes("parent=root") ? page([message("reply", "root")], root, input.canSend) :
        page(path.includes("/channel/") ? [root] : [], null, input.canSend);
      if ("canSend" in input && input.canSend === undefined) Reflect.deleteProperty(body.access, "canSend");
      return json(body);
    }
    if (path.includes("/changes")) return json({ revision: 1, resetRequired: false, messages: [], changes: [], access: proof(input.canSend) });
    if (path.includes("/messages") && init?.method === "POST") {
      const body = JSON.parse(String(init.body));
      return json({ id: body.operationId, state: "confirmed", messageId: `sent-${body.operationId}`, error: null }, 201);
    }
    if (path.startsWith("/api/teams?")) {
      const id = new URL(path, "https://local").searchParams.get("requestId")!;
      return json(operation(id, "teams", { teams: [{ id: "team", name: "Product" }] }));
    }
    if (path.startsWith("/api/teams/team/channels?")) {
      const id = new URL(path, "https://local").searchParams.get("requestId")!;
      return json(operation(id, "channels", { channels: [discovered] }));
    }
    if (path === "/api/channels/open") {
      const body = JSON.parse(String(init?.body));
      return json(operation(body.operationId, "open", { state: "preparing" }), 202);
    }
    return json({ code: "unexpected", message: `Unexpected ${path}` }, 500);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}
async function openChannel() {
  fireEvent.click(await screen.findByRole("button", { name: "Channels" }));
  fireEvent.click(await screen.findByRole("button", { name: "Open channel: Product / General" }));
  await screen.findByText("Original discussion");
}
afterEach(() => {
  cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); vi.restoreAllMocks();
  realtime.status = "Live"; realtime.online = true; realtime.callbacks = null; realtime.reconnect.mockReset();
});

describe("channel operation registry", () => {
  it("registers selection before dispatch and preserves early ready evidence over a late pending response", async () => {
    let finish!: (value: Response) => void;
    const fetch = vi.fn((path: string, init?: RequestInit) => path === "/api/channels/open" ?
      new Promise<Response>(resolve => { finish = resolve; }) :
      Promise.resolve(json(operation(path.split("/").at(-1)!, "open", { revision: 3, conversation: channel }))));
    vi.stubGlobal("fetch", fetch);
    const opened = vi.fn();
    const initial = { session, live: true, events: [] as RealtimeEvent[], reconcile: 0, opened, expired: vi.fn() };
    const { result, rerender } = renderHook(input => useChannelBrowser(input), { initialProps: initial });
    act(() => result.current.select(discovered));
    const id = result.current.openJob!.snapshot.id;
    expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({ operationId: id, selection: discovered.selection });
    expect(fetch.mock.calls[0][1]?.headers).toMatchObject({ "X-CSRF-TOKEN": session.csrf });
    rerender({ ...initial, events: [event(id, 3)] });
    await waitFor(() => expect(opened).toHaveBeenCalledExactlyOnceWith(channel));
    await act(async () => { finish(json(operation(id, "open", { revision: 1, state: "pending" }), 202)); });
    expect(result.current.openJob?.snapshot.state).toBe("ready");
    expect(result.current.openJob?.snapshot.revision).toBe(3);
    expect(opened).toHaveBeenCalledTimes(1);
  });

  it("never polls pending metadata or onboarding and checks only on event, reconnect, or explicit request", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn(async (path: string) => {
      const id = path.includes("?") ? new URL(path, "https://local").searchParams.get("requestId")! : path.split("/").at(-1)!;
      return json(operation(id, "teams", { state: "pending" }), 202);
    });
    vi.stubGlobal("fetch", fetch);
    const initial = { session, live: true, events: [] as RealtimeEvent[], reconcile: 0, opened: vi.fn(), expired: vi.fn() };
    const { result, rerender } = renderHook(input => useChannelBrowser(input), { initialProps: initial });
    await act(async () => { result.current.loadTeams(); });
    const id = result.current.teamJobs[0].snapshot.id;
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000); });
    expect(fetch).toHaveBeenCalledTimes(1);
    rerender({ ...initial, events: [event(id)] });
    await act(async () => {});
    expect(fetch).toHaveBeenCalledTimes(2);
    rerender({ ...initial, reconcile: 1 });
    await act(async () => {});
    expect(fetch).toHaveBeenCalledTimes(3);
    await act(async () => { result.current.check(id); });
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it("does not submit any browse or selection requests without Live", async () => {
    const fetch = vi.fn();
    vi.stubGlobal("fetch", fetch);
    const { result } = renderHook(() => useChannelBrowser({
      session, live: false, events: [], reconcile: 0, opened: vi.fn(), expired: vi.fn(),
    }));
    act(() => { result.current.loadTeams(); result.current.loadChannels("team"); result.current.select(discovered); });
    render(<ChannelBrowser model={result.current} live={false} reconnect={realtime.reconnect} close={vi.fn()} />);
    expect(screen.getByText("Live connection required")).toBeVisible();
    expect(screen.getByRole("button", { name: "Load teams" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Connect live updates" }));
    expect(realtime.reconnect).toHaveBeenCalledOnce();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("uses opaque pagination without loading channel contents and exposes denied, capacity and empty outcomes", async () => {
    let request = 0;
    vi.stubGlobal("fetch", vi.fn(async (path: string) => {
      const url = new URL(path, "https://local");
      const id = url.searchParams.get("requestId")!;
      if (url.pathname === "/api/teams") return json(operation(id, "teams", {
        teams: [{ id: request++ ? "other" : "team", name: request === 1 ? "Product" : "Other" }], cursor: request === 1 ? "opaque+page" : null,
      }));
      return json(operation(id, "channels", { state: "denied", error: "Access changed." }));
    }));
    const { result } = renderHook(() => useChannelBrowser({
      session, live: true, events: [], reconcile: 0, opened: vi.fn(), expired: vi.fn(),
    }));
    await act(async () => { result.current.loadTeams(); });
    await act(async () => { result.current.loadTeams("opaque+page"); });
    expect(fetch).toHaveBeenCalledWith(expect.stringContaining("cursor=opaque%2Bpage"), expect.any(Object));
    expect(result.current.teams.map(team => team.id)).toEqual(["team", "other"]);
    await act(async () => { result.current.toggle("team"); });
    render(<ChannelBrowser model={result.current} live={true} reconnect={vi.fn()} close={vi.fn()} />);
    expect(screen.getByText("Access changed.")).toBeVisible();
    expect(screen.getByText(/selection may have expired/)).toBeVisible();
    expect(vi.mocked(fetch).mock.calls.every(([path]) => !String(path).includes("/messages"))).toBe(true);
  });
});

describe("separate channel destination", () => {
  it("reads two previews inline, expands and shares the reply draft with the optional panel", async () => {
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    const replies = Array.from({ length: 4 }, (_, index) => ({ ...message(`reply-${index}`, "root"),
      createdAt: new Date(Date.now() - 600_000 + index * 1000).toISOString() }));
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      const response = await original(path, init);
      if (path.includes("/messages?parent=root") && !init?.method) {
        const preview = new URL(path, "https://local").searchParams.has("preview");
        return json({ ...await response.json(), messages: preview ? replies.slice(-2) : replies,
          cursor: preview ? "before-two" : null, replyCount: 4, preview });
      }
      return response;
    });
    render(<App />);
    await openChannel();
    const inline = within(screen.getByRole("region", { name: "Replies to Sam" }));
    await inline.findByText("Message reply-3");
    expect(inline.getByText("4 recent replies")).toBeVisible();
    expect(inline.queryByText("Message reply-0")).not.toBeInTheDocument();
    expect(screen.queryByRole("complementary")).not.toBeInTheDocument();
    fireEvent.click(inline.getByRole("button", { name: "View all replies" }));
    await inline.findByText("Message reply-0");
    fireEvent.click(inline.getByRole("button", { name: "Reply to Sam" }));
    const composer = await inline.findByRole("textbox", { name: "Reply to Sam" });
    expect(composer).toHaveFocus();
    fireEvent.change(composer, { target: { value: "One shared draft" } });
    fireEvent.click(inline.getByRole("button", { name: "Open in panel" }));
    const panel = within(screen.getByRole("complementary", { name: "Channel thread" }));
    expect(await panel.findByRole("textbox", { name: "Write a reply" })).toHaveValue("One shared draft");
    expect(screen.getAllByRole("textbox")).toHaveLength(1);
    fireEvent.click(panel.getByRole("button", { name: "Close thread" }));
    expect(await inline.findByRole("textbox", { name: "Reply to Sam" })).toHaveValue("One shared draft");
    expect(fetch.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
    expect(fetch.mock.calls.some(([path]) => path.includes("parent=root&preview=true"))).toBe(true);
    expect(fetch.mock.calls.some(([path]) => path.startsWith("/api/conversations/channel/messages?parent=root") && !path.includes("preview"))).toBe(true);
  });

  it("automatically finishes cold replies on a same-revision event without polling or a false empty state", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let ready = false;
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path.includes("/messages?parent=root") && !ready)
        return json({ state: "pending", retryAfterMs: 1000, access: proof() }, 202);
      return original(path, init);
    });
    render(<App />);
    await openChannel();
    const inline = within(screen.getByRole("region", { name: "Replies to Sam" }));
    await inline.findByText("Loading replies...");
    expect(inline.queryByText("No recent replies yet")).not.toBeInTheDocument();
    expect(inline.queryByText("0 recent replies")).not.toBeInTheDocument();
    const reads = () => fetch.mock.calls.filter(([path]) => path.includes("/messages?parent=root")).length;
    expect(reads()).toBe(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });
    expect(reads()).toBe(1);
    vi.useRealTimers();
    ready = true;
    act(() => realtime.callbacks?.events?.([{ type: "channel.view.ready", schemaVersion: 1, sequence: 0,
      revision: 1, generation: 1, conversationId: "channel", parentId: "root" }]));
    await inline.findByText("Message reply");
    expect(inline.queryByText("Loading replies...")).not.toBeInTheDocument();
    expect(reads()).toBe(2);
    expect(fetch.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  });

  it("bounds a cold Teams read and offers Retry without automatically issuing more reads", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    fetch.mockImplementation(async (path: string, init?: RequestInit) =>
      path.includes("/messages?parent=root") ? json({ state: "pending", retryAfterMs: 1000, access: proof() }, 202) :
        original(path, init));
    render(<App />);
    await openChannel();
    const inline = within(screen.getByRole("region", { name: "Replies to Sam" }));
    await inline.findByText("Loading replies...");
    await act(async () => {});
    await act(async () => { await vi.advanceTimersByTimeAsync(120_001); });
    expect(await inline.findByRole("heading", { name: "Could not load replies from Microsoft Teams" })).toBeVisible();
    expect(inline.getByRole("button", { name: "Retry" })).toBeEnabled();
    expect(inline.queryByText("No recent replies yet")).not.toBeInTheDocument();
    expect(fetch.mock.calls.filter(([path]) => path.includes("/messages?parent=root"))).toHaveLength(1);
  });

  it("keeps authorized replies on Teams failure and recovers in place on Retry", async () => {
    let failed = false;
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path.includes("/changes")) {
        if (failed && path.includes("parent=root")) return json({ code: "read_refresh_unavailable", message: "Teams is unavailable." }, 503);
        return json({ revision: 2, resetRequired: false, messages: [message("reply", "root")], changes: [],
          access: proof(), replyCount: 1 });
      }
      return original(path, init);
    });
    render(<App />);
    await openChannel();
    const inline = within(screen.getByRole("region", { name: "Replies to Sam" }));
    await inline.findByText("Message reply");
    failed = true;
    act(() => realtime.callbacks?.reconcile());
    await inline.findByRole("heading", { name: "Could not load replies from Microsoft Teams" });
    expect(inline.getByText("Message reply")).toBeVisible();
    expect(screen.queryByRole("complementary")).not.toBeInTheDocument();
    failed = false;
    fireEvent.click(inline.getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(inline.queryByRole("alert")).not.toBeInTheDocument());
    expect(inline.getByText("1 recent reply")).toBeVisible();
  });

  it("expires a hidden reply count without losing visible replies or polling", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      const response = await original(path, init);
      return path.includes("/messages?parent=root") ? json({ ...await response.json(), cursor: "older",
        replyCount: 5, preview: true, replyCountExpiresAt: new Date(Date.now() + 1000).toISOString() }) : response;
    });
    render(<App />);
    await openChannel();
    const inline = within(screen.getByRole("region", { name: "Replies to Sam" }));
    await inline.findByText("5 recent replies");
    const reads = fetch.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(1001); });
    expect(inline.queryByText("5 recent replies")).not.toBeInTheDocument();
    expect(inline.getByText("Recent replies")).toBeVisible();
    expect(inline.getByText("Message reply")).toBeVisible();
    expect(fetch.mock.calls).toHaveLength(reads);
  });

  it("also bounds a pending older page while keeping the authorized thread readable", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path.includes("parent=root") && path.includes("cursor="))
        return json({ state: "pending", retryAfterMs: 1000, access: proof() }, 202);
      const response = await original(path, init);
      return path.includes("/messages?parent=root") ? json({ ...await response.json(), cursor: "older",
        preview: path.includes("preview=true") }) : response;
    });
    render(<App />);
    await openChannel();
    const inline = within(screen.getByRole("region", { name: "Replies to Sam" }));
    await inline.findByText("Message reply");
    fireEvent.click(inline.getByRole("button", { name: "View all replies" }));
    fireEvent.click(await inline.findByRole("button", { name: "Load earlier messages" }));
    await inline.findByText("Loading replies...");
    expect(inline.getByRole("button", { name: "Load earlier messages" })).toBeDisabled();
    await act(async () => { await vi.advanceTimersByTimeAsync(120_001); });
    expect(inline.getByRole("heading", { name: "Could not load replies from Microsoft Teams" })).toBeVisible();
    expect(inline.getByText("Message reply")).toBeVisible();
    expect(inline.getByRole("button", { name: "Retry" })).toBeEnabled();
    expect(fetch.mock.calls.filter(([path]) => path.includes("cursor="))).toHaveLength(1);
  });

  it.each(["deferred", "failed"] as const)("shows %s discovery honestly and reloads a terminal failure without sending", async state => {
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    let attempts = 0;
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path.startsWith("/api/teams/team/channels?") && attempts++ === 0) {
        const id = new URL(path, "https://local").searchParams.get("requestId")!;
        return json(operation(id, "channels", { state, error: "Channel discovery could not complete (graph_400)." }));
      }
      return original(path, init);
    });
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Channels" }));
    fireEvent.click(screen.getByRole("button", { name: "Browse channels" }));
    const browser = within(await screen.findByRole("region", { name: "Browse channels" }));
    fireEvent.click(await browser.findByRole("button", { name: "Product" }));
    await browser.findByText(state === "failed" ? "Channel request unavailable" : "Channel loading delayed");
    expect(browser.queryByText("Loading channels")).not.toBeInTheDocument();
    const reload = browser.getByRole("button", { name: "Reload channels" });
    if (state === "failed") {
      expect(reload).toBeEnabled();
      expect(browser.queryByRole("button", { name: "Check request" })).not.toBeInTheDocument();
      fireEvent.click(reload);
      await browser.findByRole("button", { name: "Open channel: Product / General" });
    } else {
      expect(reload).toBeDisabled();
      expect(browser.getByRole("button", { name: "Check request" })).toBeEnabled();
    }
    expect(fetch.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
  });

  it("keeps scoped chat search and a remembered selection and draft for each destination", async () => {
    appApi();
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Open chat: Sam" }));
    fireEvent.change(await screen.findByRole("textbox", { name: "Write a message" }), { target: { value: "Chat draft" } });
    fireEvent.change(screen.getByRole("searchbox", { name: "Search conversations" }), { target: { value: "Sam" } });
    await openChannel();
    expect(screen.queryByRole("button", { name: "New chat" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "All" })).not.toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Search channels" })).toHaveValue("");
    fireEvent.click(screen.getByRole("button", { name: "New post" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Start a discussion in #General" }), { target: { value: "Discussion draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Chats" }));
    expect(await screen.findByRole("textbox", { name: "Write a message" })).toHaveValue("Chat draft");
    expect(screen.getByRole("searchbox", { name: "Search conversations" })).toHaveValue("Sam");
    fireEvent.click(screen.getByRole("button", { name: "Channels" }));
    expect(await screen.findByRole("textbox", { name: "Start a discussion in #General" })).toHaveValue("Discussion draft");
  });

  it("preserves Cancel drafts, sanitizes the original thread, and separates Post from Reply sends", async () => {
    const fetch = appApi();
    render(<App />);
    await openChannel();
    expect(screen.queryByRole("textbox", { name: "Write a message" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "New post" }));
    const composer = screen.getByRole("textbox", { name: "Start a discussion in #General" });
    fireEvent.change(composer, { target: { value: "Root draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.getByText(/Unsent discussion draft saved/)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "New post" }));
    expect(screen.getByRole("textbox", { name: "Start a discussion in #General" })).toHaveValue("Root draft");
    fireEvent.click(screen.getByRole("button", { name: "Open in panel" }));
    const thread = within(screen.getByRole("complementary", { name: "Channel thread" }));
    expect(thread.getByText("Original discussion").tagName).toBe("STRONG");
    expect(screen.getByRole("complementary").querySelector("img, script")).toBeNull();
    expect(thread.getByText("Product / #General")).toBeVisible();
    fireEvent.change(await thread.findByRole("textbox", { name: "Write a reply" }), { target: { value: "Thread draft" } });
    fireEvent.click(thread.getByRole("button", { name: "Reply" }));
    await waitFor(() => expect(fetch.mock.calls.some(([, init]) => init?.method === "POST")).toBe(true));
    const reply = fetch.mock.calls.find(([, init]) => init?.method === "POST")!;
    expect(JSON.parse(String(reply[1]?.body))).toMatchObject({ body: "Thread draft", parentId: "root", window: "window" });
    fireEvent.click(screen.getByRole("button", { name: "Close thread" }));
    expect(screen.getByRole("textbox", { name: "Start a discussion in #General" })).toHaveValue("Root draft");
    fireEvent.click(screen.getByRole("button", { name: "Post" }));
    await waitFor(() => expect(fetch.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(2));
    const root = fetch.mock.calls.filter(([, init]) => init?.method === "POST")[1];
    expect(JSON.parse(String(root[1]?.body))).toMatchObject({ body: "Root draft", parentId: null });
  });

  it("groups stable team identities independently, including legacy fixtures", async () => {
    appApi({ catalog: [channel, { ...channel, id: "other", title: "Design", teamId: "other-team" },
      { ...channel, id: "legacy", title: "Announcements", teamId: undefined, team: "Legacy team" }] });
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Channels" }));
    const nav = within(screen.getByRole("navigation"));
    const teams = nav.getAllByRole("region", { name: "Product" });
    expect(teams).toHaveLength(2);
    fireEvent.click(within(teams[0]).getByRole("button", { name: "Product" }));
    expect(nav.queryByRole("button", { name: "Open channel: Product / General" })).not.toBeInTheDocument();
    expect(nav.getByRole("button", { name: "Open channel: Product / Design" })).toBeVisible();
    expect(nav.getByRole("button", { name: "Open channel: Legacy team / Announcements" })).toBeVisible();
  });

  it("browses teams on demand and selecting a channel immediately prepares without sending or confirmation", async () => {
    const fetch = appApi();
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Channels" }));
    expect(fetch.mock.calls.some(([path]) => path.startsWith("/api/teams"))).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Browse channels" }));
    const browser = within(await screen.findByRole("region", { name: "Browse channels" }));
    fireEvent.click(await browser.findByRole("button", { name: "Product" }));
    fireEvent.click(await browser.findByRole("button", { name: "Open channel: Product / General" }));
    expect(await browser.findByText(/Preparing channel: Product/)).toBeVisible();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const sends = fetch.mock.calls.filter(([, init]) => init?.method === "POST");
    expect(sends.map(([path]) => path)).toEqual(["/api/channels/open"]);
    expect(JSON.parse(String(sends[0][1]?.body))).toMatchObject({ selection: "opaque-token" });
  });

  it("delivers consecutive event batches to every accepted browse operation before React renders again", async () => {
    const fetch = appApi();
    const original = fetch.getMockImplementation()!;
    let teamRequests = 0;
    const accepted = new Map<string, ChannelOperation["kind"]>();
    fetch.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path.startsWith("/api/teams?") && ++teamRequests > 1 || path.startsWith("/api/teams/team/channels?")) {
        const id = new URL(path, "https://local").searchParams.get("requestId")!;
        const kind = path.includes("/channels?") ? "channels" : "teams";
        accepted.set(id, kind);
        return json(operation(id, kind, { state: "pending" }), 202);
      }
      if (path.startsWith("/api/channel-operations/")) {
        const id = path.split("/").at(-1)!;
        const kind = accepted.get(id)!;
        return json(operation(id, kind, { revision: 2,
          teams: kind === "teams" ? [{ id: "team", name: "Product" }] : null,
          channels: kind === "channels" ? [discovered] : null }));
      }
      return original(path, init);
    });
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Channels" }));
    fireEvent.click(screen.getByRole("button", { name: "Browse channels" }));
    const browser = within(await screen.findByRole("region", { name: "Browse channels" }));
    fireEvent.click(await browser.findByRole("button", { name: "Product" }));
    await browser.findByText("Loading channels");
    fireEvent.click(browser.getByRole("button", { name: "Reload teams" }));
    await browser.findByText("Loading teams");
    act(() => {
      for (const id of accepted.keys()) realtime.callbacks?.events?.([event(id)]);
    });
    await browser.findByRole("button", { name: "Open channel: Product / General" });
    const checked = fetch.mock.calls.filter(([path]) => path.startsWith("/api/channel-operations/"));
    expect(new Set(checked.map(([path]) => path.split("/").at(-1)))).toEqual(new Set(accepted.keys()));
  });

  it.each([false, undefined])("requires a fresh affirmative send capability (%s is read-only)", async canSend => {
    appApi({ canSend, catalog: [{ ...channel, canPost: canSend }] });
    render(<App />);
    await openChannel();
    expect(screen.getByText(/Read-only\./)).toBeVisible();
    expect(screen.getByRole("button", { name: "New post" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Cache diagnostics" })).not.toBeInTheDocument();
  });

  it("waits for targeted root readiness even at the same revision and never labels preparing as empty", async () => {
    let ready = false;
    const fetch = appApi({ pending: true });
    fetch.mockImplementation(async (path: string) => {
      if (path === "/api/session") return json(session);
      if (path === "/api/conversations") return json([channel]);
      return ready ? json(page()) : json({ state: "pending", retryAfterMs: 1000, access: null }, 202);
    });
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Channels" }));
    fireEvent.click(await screen.findByRole("button", { name: "Open channel: Product / General" }));
    await screen.findByText(/Preparing channel posts/);
    expect(screen.queryByRole("heading", { name: "No posts yet" })).not.toBeInTheDocument();
    ready = true;
    act(() => realtime.callbacks?.events?.([{ type: "channel.view.ready", schemaVersion: 1, sequence: 0,
      revision: 1, generation: 1, conversationId: channel.id, parentId: null }]));
    expect(await screen.findByRole("heading", { name: "No posts yet" })).toBeVisible();
    expect(fetch.mock.calls.filter(([path]) => path.includes("/messages"))).toHaveLength(2);
  });
});

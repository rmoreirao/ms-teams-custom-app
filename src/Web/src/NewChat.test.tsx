// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App } from "./App";
import type { Conversation, Session } from "./api";

const session: Session = {
  id: "alex", name: "Alex", environment: "Development", source: "Microsoft Teams",
  scopedValidation: true, canStartDirectChats: false, canCreateDirectChats: false, csrf: "test-csrf",
};
const chats: Conversation[] = [
  { id: "graph-pilot-chat", title: "Dedicated test chat", kind: "chat", team: null, revision: 1 },
  { id: "graph-pilot-channel", title: "General", kind: "channel", team: "Test team", revision: 1 },
];
const json = (body: unknown, status = 200, headers?: HeadersInit) =>
  new Response(JSON.stringify(body), { status, headers });
function mockApi(catalog: (path: string) => Response | Promise<Response> = () => json(chats)) {
  const fetch = vi.fn(async (path: string, init?: RequestInit) => {
    if (path === "/api/session") return json(session);
    if (path.startsWith("/api/conversations") && !path.includes("/messages")) return catalog(path);
    if (path === "/api/realtime/negotiate") return json({ code: "realtime_preparing", message: "Live updates are preparing." }, 503);
    if (path.endsWith("/messages") && !init?.method) return json({
      messages: [], cursor: null, revision: 1, window: "authorized-window", parentMessage: null,
    });
    return json({ code: "unexpected_request", message: "Unexpected request in test." }, 500);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}
const discovery = () => within(screen.getByRole("region", { name: "Find a chat" }));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("capability-aware new chat", () => {
  it("keeps the other person's catalog name in chat rows, search, and the header across refreshes", async () => {
    vi.useFakeTimers();
    const title = "Reference Test jordan";
    mockApi(() => json([
      { id: "direct-chat", title, kind: "chat", team: null, revision: 1 },
      { id: "group-chat", title: "Project group", kind: "chat", team: null, revision: 1 },
    ]));
    await act(async () => { render(<App />); });
    const navigation = within(screen.getByRole("navigation", { name: "Conversations" }));
    fireEvent.click(navigation.getByRole("button", { name: `${title}Chat` }));
    expect(screen.getByRole("heading", { name: title })).toBeVisible();
    expect(screen.queryByRole("heading", { name: /Alex/ })).not.toBeInTheDocument();
    expect(navigation.getByText(session.name)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Chats" }));
    fireEvent.change(screen.getByRole("searchbox", { name: "Search conversations" }), { target: { value: "jordan" } });
    expect(navigation.getByRole("button", { name: `${title}Chat` })).toBeVisible();
    expect(navigation.queryByRole("button", { name: "Project groupChat" })).not.toBeInTheDocument();
    await act(async () => { vi.advanceTimersByTime(30_000); });
    expect(screen.getByRole("heading", { name: title })).toBeVisible();
    expect(navigation.getByRole("button", { name: `${title}Chat` })).toBeVisible();
  });

  it("opens the reference search surface but only offers authorized chats, not fictional people or channels", async () => {
    const fetch = mockApi();
    render(<App />);
    const input = await screen.findByRole("searchbox", { name: "To" });
    expect(input).toHaveFocus();
    expect(await discovery().findByRole("button", { name: "Open chat: Dedicated test chat" })).toBeVisible();
    expect(discovery().queryByRole("button", { name: /General/ })).not.toBeInTheDocument();
    expect(discovery().getByText(/Creating a chat and looking up people by email are not available/)).toBeVisible();
    expect(screen.queryByRole("button", { name: "Test scenarios" })).not.toBeInTheDocument();
    fireEvent.change(input, { target: { value: "  DEDICATED  " } });
    expect(discovery().getByRole("button", { name: "Open chat: Dedicated test chat" })).toBeVisible();
    fireEvent.change(input, { target: { value: "unknown@example.com" } });
    expect(discovery().getByRole("heading", { name: "No matching chats" })).toBeVisible();
    fireEvent.click(discovery().getByRole("button", { name: "Clear chat search" }));
    expect(input).toHaveValue("");
    expect(input).toHaveFocus();
    expect(fetch.mock.calls.every(([path]) => !/people|users|directory/.test(path))).toBe(true);
  });

  it("reuses the existing conversation without a POST and restores its in-memory draft after discovery", async () => {
    const fetch = mockApi();
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Open chat: Dedicated test chat" }));
    const composer = await screen.findByRole("textbox", { name: "Write a message" });
    expect(composer).toHaveFocus();
    fireEvent.change(composer, { target: { value: "Unsent draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Change chat" }));
    fireEvent.click(await screen.findByRole("button", { name: "Open chat: Dedicated test chat" }));
    expect(screen.getByRole("textbox", { name: "Write a message" })).toHaveValue("Unsent draft");
    expect(fetch.mock.calls.filter(([, init]) => init?.method === "POST").map(([path]) => path))
      .toEqual(["/api/realtime/negotiate"]);
    expect(screen.getAllByRole("button", { name: /Dedicated test chat/ })).toHaveLength(1);
  });

  it("uses opaque catalog continuation and distinguishes loaded search results from the full catalog", async () => {
    const fetch = mockApi(path => path.includes("?cursor=") ? json([
      { id: "next", title: "Next authorized chat", kind: "chat", team: null, revision: 1 },
    ]) : json(chats, 200, { "X-Next-Cursor": "opaque+next" }));
    render(<App />);
    const more = await screen.findByRole("button", { name: "Load more available chats" });
    expect(discovery().getByText("1 loaded chat")).toBeVisible();
    fireEvent.change(screen.getByRole("searchbox", { name: "To" }), { target: { value: "Next" } });
    expect(discovery().getByText(/More conversations are available to load/)).toBeVisible();
    fireEvent.click(more);
    expect(await discovery().findByRole("button", { name: "Open chat: Next authorized chat" })).toBeVisible();
    expect(fetch).toHaveBeenCalledWith("/api/conversations?cursor=opaque%2Bnext", expect.any(Object));
    expect(discovery().queryByRole("button", { name: "Load more available chats" })).not.toBeInTheDocument();
  });

  it("does not misreport a catalog failure as an empty search and supports explicit recovery", async () => {
    let fail = true;
    mockApi(() => fail ? json({ code: "unavailable", message: "Chat access is temporarily unavailable." }, 503) : json(chats));
    render(<App />);
    expect(await screen.findByRole("heading", { name: "Chats are unavailable" })).toBeVisible();
    expect(discovery().queryByRole("heading", { name: "No chats available" })).not.toBeInTheDocument();
    fail = false;
    fireEvent.click(discovery().getByRole("button", { name: "Try chat search again" }));
    expect(await discovery().findByRole("button", { name: "Open chat: Dedicated test chat" })).toBeVisible();
    expect(discovery().queryByRole("alert")).not.toBeInTheDocument();
  });

  it("distinguishes loading from an account with no chats", async () => {
    let finish!: (response: Response) => void;
    mockApi(() => new Promise<Response>(resolve => { finish = resolve; }));
    render(<App />);
    expect(await screen.findByText("Loading chats...")).toBeVisible();
    expect(discovery().queryByRole("heading", { name: "No chats available" })).not.toBeInTheDocument();
    await act(async () => { finish(json([])); });
    expect(await discovery().findByRole("heading", { name: "No chats available" })).toBeVisible();
  });

  it("does not replace unavailable Live updates with a 30-second catalog poll", async () => {
    vi.useFakeTimers();
    const fetch = mockApi();
    await act(async () => { render(<App />); });
    expect(screen.getByText("Synchronization unavailable")).toBeVisible();
    await act(async () => { vi.advanceTimersByTime(30_000); });
    expect(fetch.mock.calls.filter(([path]) => path === "/api/conversations")).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "Test scenarios" })).not.toBeInTheDocument();
  });

  it("clears chat discovery and drafts when catalog authorization expires", async () => {
    let expired = false;
    mockApi(() => expired ? json({ code: "authentication_required", message: "Sign in again." }, 401) :
      json(chats, 200, { "X-Next-Cursor": "next" }));
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Open chat: Dedicated test chat" }));
    fireEvent.change(await screen.findByRole("textbox", { name: "Write a message" }), { target: { value: "Private draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Change chat" }));
    expired = true;
    fireEvent.click(screen.getByRole("button", { name: "Load more available chats" }));
    await screen.findByRole("link", { name: "Sign in with Microsoft" });
    expect(screen.queryByRole("region", { name: "Find a chat" })).not.toBeInTheDocument();
    expect(screen.queryByDisplayValue("Private draft")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText("Dedicated test chat")).not.toBeInTheDocument());
  });
});

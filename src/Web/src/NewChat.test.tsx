// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App } from "./App";
import type { Conversation, Session } from "./api";

const session: Session = {
  id: "alex", name: "Alex", simulated: false, localAuth: false, scenarioAdmin: false,
  basicValidation: true, csrf: "test-csrf",
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
    if (path === "/api/realtime/negotiate") return json({ transport: "poll", intervalSeconds: 30 });
    if (path.endsWith("/messages") && !init?.method) return json({
      messages: [], cursor: null, revision: 1, window: "authorized-window", simulated: false, parentMessage: null,
    });
    return json({ code: "unexpected_request", message: "Unexpected request in test." }, 500);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}
const discovery = () => within(screen.getByRole("region", { name: "Find a chat" }));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("capability-aware new chat", () => {
  it("opens the reference search surface but only offers authorized chats, not fictional people or channels", async () => {
    const fetch = mockApi();
    render(<App />);
    const input = await screen.findByRole("searchbox", { name: "To" });
    expect(input).toHaveFocus();
    expect(await discovery().findByRole("button", { name: "Open chat: Dedicated test chat" })).toBeVisible();
    expect(discovery().queryByRole("button", { name: /General/ })).not.toBeInTheDocument();
    expect(discovery().getByText(/New one-to-one chats and directory lookup are not enabled/)).toBeVisible();
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

  it("keeps a 30-second poll in GraphPilot without opening simulator controls", async () => {
    vi.useFakeTimers();
    const fetch = mockApi();
    await act(async () => { render(<App />); });
    expect(screen.getByText("Updates every 30s")).toBeVisible();
    await act(async () => { vi.advanceTimersByTime(30_000); });
    expect(fetch.mock.calls.filter(([path]) => path === "/api/conversations")).toHaveLength(2);
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

// @vitest-environment jsdom
import React from "react";
import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App } from "./App";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
function mockPeople(openStatus = 200, peopleStatus = 200) {
  const fetch = vi.fn(async (path: string, init?: RequestInit) => {
    if (path === "/api/session") return json({
      id: "jordan", name: "Jordan Lee", environment: "Development", source: "Microsoft Teams",
      scopedValidation: true, canStartDirectChats: true, canCreateDirectChats: false, csrf: "test-csrf",
    });
    if (path === "/api/conversations") return json([
      { id: "graph-pilot-chat", title: "Teams Reference Validation group", kind: "chat", team: null, revision: 1 },
    ]);
    if (path === "/api/people") return peopleStatus === 200 ? json([
      { id: "alex", name: "Alex Morgan", email: "alex@example.test" },
      { id: "sam", name: "Sam Rivera", email: "sam@example.test" },
    ]) : json({ code: "people_unavailable", message: "People could not be loaded." }, peopleStatus);
    if (path === "/api/chats/direct") return openStatus === 200 ? json({
      id: "graph-pilot-dm-test", title: "Alex Morgan", kind: "chat", team: null, revision: 1,
    }) : json({ code: "resource_preparing", message: "Your approved chat is being prepared. No message was sent. Try again." }, openStatus);
    if (path.endsWith("/messages") && !init?.method) return json({
      messages: [], cursor: null, revision: 1, window: "authorized-window", parentMessage: null,
    });
    if (path === "/api/realtime/negotiate") return json({ code: "realtime_preparing", message: "Live updates are preparing." }, 503);
    return json({ code: "unexpected_request", message: "Unexpected test request." }, 500);
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
}
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it("lets Jordan find Alex independently of conversation titles, then enter a real composer contract without sending", async () => {
  const fetch = mockPeople();
  render(<App />);
  const input = await screen.findByRole("searchbox", { name: "To" });
  fireEvent.change(input, { target: { value: "alex" } });
  fireEvent.click(await screen.findByRole("button", { name: "Choose: Alex Morgan, alex@example.test" }));
  expect(await screen.findByRole("textbox", { name: "Write a message" })).toHaveFocus();
  expect(screen.getByText(/alex@example.test/)).toBeVisible();
  expect(fetch).toHaveBeenCalledWith("/api/chats/direct", expect.objectContaining({
    method: "POST", body: JSON.stringify({ personId: "alex" }),
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": "test-csrf" },
  }));
  expect(fetch.mock.calls.filter(([path, init]) => path.endsWith("/messages") && init?.method === "POST")).toHaveLength(0);
  fireEvent.change(screen.getByRole("textbox", { name: "Write a message" }), { target: { value: "Unsent draft for Alex" } });
  fireEvent.click(screen.getByRole("button", { name: "Change person" }));
  fireEvent.change(await screen.findByRole("searchbox", { name: "To" }), { target: { value: "alex@example.test" } });
  fireEvent.click(await screen.findByRole("button", { name: "Choose: Alex Morgan, alex@example.test" }));
  expect(await screen.findByRole("textbox", { name: "Write a message" })).toHaveValue("Unsent draft for Alex");
  expect(within(screen.getByRole("navigation", { name: "Conversations" })).getAllByRole("button", { name: /Alex Morgan/ })).toHaveLength(1);
});

it("shows an explicit preparation failure without automatic retry or a fake composer", async () => {
  const fetch = mockPeople(503);
  render(<App />);
  fireEvent.click(await screen.findByRole("button", { name: "Choose: Alex Morgan, alex@example.test" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("No message was sent");
  await waitFor(() => expect(screen.getByRole("button", { name: "Choose: Alex Morgan, alex@example.test" })).toBeEnabled());
  expect(fetch.mock.calls.filter(([path]) => path === "/api/chats/direct")).toHaveLength(1);
  expect(screen.queryByRole("textbox", { name: "Write a message" })).not.toBeInTheDocument();
});

it("does not turn a people-read error into an empty search or fictional recipients", async () => {
  mockPeople(200, 503);
  render(<App />);
  expect(await screen.findByRole("heading", { name: "People are unavailable" })).toBeVisible();
  expect(screen.queryByRole("heading", { name: "No other people available" })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Try people search again" })).toBeEnabled();
});

it("clears the discovery surface on authorization expiry", async () => {
  mockPeople(200, 401);
  render(<App />);
  expect(await screen.findByRole("link", { name: "Sign in with Microsoft" })).toBeVisible();
  expect(screen.queryByRole("region", { name: "Find a person" })).not.toBeInTheDocument();
});

it("keeps Live discovery honest about opening existing approved chats rather than creating new ones", async () => {
  const fetch = mockPeople();
  render(<App />);
  expect(await screen.findByText(/Creating new chats and organization-wide directory lookup are not enabled/)).toBeVisible();
  expect(screen.getByText(/Choose a person to open your approved chat/)).toBeVisible();
  expect(screen.queryByText(/Alex, Sam and Jordan only/)).not.toBeInTheDocument();
  fireEvent.click(await screen.findByRole("button", { name: "Choose: Alex Morgan, alex@example.test" }));
  expect(await screen.findByRole("textbox", { name: "Write a message" })).toBeVisible();
  expect(fetch.mock.calls.filter(([path, init]) => path.endsWith("/messages") && init?.method === "POST")).toHaveLength(0);
});

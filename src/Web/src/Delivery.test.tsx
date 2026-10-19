// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App, Delivery } from "./App";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
describe("delivery correctness", () => {
  it("offers an original-operation check, never a resend, for uncertain writes", () => {
    const check = vi.fn();
    render(<Delivery item={{ conversationId: "test", parentId: null, body: "test",
      operation: { id: "one", state: "unknown", messageId: null, error: null, simulated: true } }} onCheck={check} />);
    fireEvent.click(screen.getByRole("button", { name: "Check original send" }));
    expect(check).toHaveBeenCalledOnce();
    expect(screen.queryByRole("button", { name: /resend/i })).not.toBeInTheDocument();
  });

  describe("deployment-aware sign-in", () => {
    for (const localAuth of [false, true]) {
      it(`${localAuth ? "shows" : "hides"} local sign-in from the server capability`, async () => {
        const fetch = vi.fn(async (path: string) => path === "/auth/config"
          ? new Response(JSON.stringify({ localAuth, simulated: true }), { status: 200 })
          : new Response(JSON.stringify({ code: "authentication_required", message: "Sign in." }), { status: 401 }));
        vi.stubGlobal("fetch", fetch);
        render(<App />);
        await screen.findByRole("link", { name: "Sign in with Microsoft" });
        await waitFor(() => expect(fetch).toHaveBeenCalledWith("/auth/config", expect.any(Object)));
        if (localAuth) {
          expect(await screen.findByRole("button", { name: "Local development sign-in" })).toBeVisible();
        } else {
          expect(screen.queryByRole("button", { name: "Local development sign-in" })).not.toBeInTheDocument();
        }
      });
    }
  });
  it("never labels simulator evidence as Teams delivery", () => {
    render(<Delivery item={{ conversationId: "test", parentId: null, body: "test",
      operation: { id: "one", state: "confirmed", messageId: "sim-one", error: null, simulated: true } }} onCheck={() => {}} />);
    expect(screen.getByText("Confirmed by simulator - not Teams")).toBeVisible();
  });
  it("labels real Graph confirmation as Teams delivery", () => {
    render(<Delivery item={{ conversationId: "test", parentId: null, body: "test",
      operation: { id: "one", state: "confirmed", messageId: "graph-one", error: null, simulated: false } }} onCheck={() => {}} />);
    expect(screen.getByText("Confirmed by Teams")).toBeVisible();
    expect(screen.queryByText("Confirmed by simulator - not Teams")).not.toBeInTheDocument();
  });
  it("loads the next authorized catalog page and keeps live labels distinct", async () => {
    const fetch = vi.fn(async (path: string) => {
      if (path === "/api/session") return new Response(JSON.stringify({
        id: "alex", name: "Alex", simulated: false, localAuth: false, scenarioAdmin: false, csrf: "test",
      }), { status: 200 });
      if (path === "/api/conversations") return new Response(JSON.stringify([
        { id: "first", title: "First conversation", kind: "chat", team: null, revision: 1 },
      ]), { status: 200, headers: { "X-Next-Cursor": "opaque+next" } });
      if (path === "/api/conversations?cursor=opaque%2Bnext") return new Response(JSON.stringify([
        { id: "second", title: "Second conversation", kind: "chat", team: null, revision: 1 },
      ]), { status: 200 });
      return new Response(JSON.stringify({ code: "realtime_preparing", message: "Updates are preparing." }), { status: 503 });
    });
    vi.stubGlobal("fetch", fetch);
    render(<App />);
    fireEvent.click(await screen.findByRole("button", { name: "Load more conversations" }));
    expect(await within(screen.getByRole("navigation", { name: "Conversations" })).findByRole("button", { name: /Second conversation/ })).toBeVisible();
    expect(screen.getByText("Teams workspace")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Test scenarios" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Load more conversations" })).not.toBeInTheDocument();
    expect(fetch).toHaveBeenCalledWith("/api/conversations?cursor=opaque%2Bnext", expect.any(Object));
  });
});

// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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
});

// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { Delivery } from "./App";

afterEach(cleanup);
describe("delivery correctness", () => {
  it("offers an original-operation check, never a resend, for uncertain writes", () => {
    const check = vi.fn();
    render(<Delivery item={{ conversationId: "test", parentId: null, body: "test",
      operation: { id: "one", state: "unknown", messageId: null, error: null, simulated: true } }} onCheck={check} />);
    fireEvent.click(screen.getByRole("button", { name: "Check original send" }));
    expect(check).toHaveBeenCalledOnce();
    expect(screen.queryByRole("button", { name: /resend/i })).not.toBeInTheDocument();
  });
  it("never labels simulator evidence as Teams delivery", () => {
    render(<Delivery item={{ conversationId: "test", parentId: null, body: "test",
      operation: { id: "one", state: "confirmed", messageId: "sim-one", error: null, simulated: true } }} onCheck={() => {}} />);
    expect(screen.getByText("Confirmed by simulator - not Teams")).toBeVisible();
  });
});

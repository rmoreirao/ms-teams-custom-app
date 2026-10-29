// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { MessageList } from "./MessageList";
import type { Message } from "./api";

const reply = (id: string, body: string): Message => ({ id, body, parentId: "root", actorId: "sam", author: "Sam",
  createdAt: new Date().toISOString(), modifiedAt: new Date().toISOString(), deleted: false });
afterEach(cleanup);

describe("inline reply content", () => {
  it("keeps a short reply complete and expands long content through the existing sanitizer", () => {
    const body = "A readable long reply. ".repeat(20);
    const { container } = render(<MessageList messages={[reply("short", "Short reply, fully readable."),
      { ...reply("long", body), htmlBody: `<p><em>${body}</em><img src=x onerror=alert(1)><script>bad()</script></p>` }]}
      actorId="alex" scrollRef={React.createRef()} threaded={false} onThread={() => {}} compact />);
    expect(screen.getByText("Short reply, fully readable.")).toBeVisible();
    expect(container.querySelector("em")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Show more" }));
    expect(container.querySelector("em")?.textContent).toBe(body);
    expect(container.querySelector("img, script")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Show less" }));
    expect(container.querySelector("em")).toBeNull();
  });

  it("keeps a paginated inline thread in document flow instead of a nested virtual scroller", () => {
    render(<MessageList messages={Array.from({ length: 65 }, (_, index) => reply(String(index), `Reply ${index}`))}
      actorId="alex" scrollRef={React.createRef()} threaded={false} onThread={() => {}} inline />);
    expect(screen.getAllByRole("article")).toHaveLength(65);
    expect(screen.queryByRole("feed")).not.toBeInTheDocument();
  });
});

// @vitest-environment jsdom
import React from "react";
import { afterEach, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { MessageContent, sanitizeMessageHtml } from "./MessageContent";
import type { Message } from "./api";

const message: Message = {
  id: "one", actorId: "sam", author: "Sam", body: "Plain text fallback",
  createdAt: "2026-10-19T10:00:00Z", modifiedAt: "2026-10-19T10:00:00Z", parentId: null, deleted: false,
};
afterEach(cleanup);

it("preserves supported formatting but strips active content, styles, forms and event handlers", () => {
  const html = sanitizeMessageHtml(`<p style="position:fixed" onclick="alert(1)">Hello <strong>world</strong></p>
    <ul><li>One</li><li><code>two</code></li></ul><script>alert(1)</script>
    <svg onload="alert(1)"><circle /></svg><img src=x onerror=alert(1)><iframe src="https://evil.invalid"></iframe>
    <form><input name=token></form><span data-secret="private">Mention</span>`);
  const { container } = render(<MessageContent message={{ ...message, htmlBody: html }} />);
  expect(container.querySelector("strong")).toHaveTextContent("world");
  expect(container.querySelectorAll("li")).toHaveLength(2);
  expect(container.querySelector("script, svg, img, iframe, input, [onclick], [onload], [style], [data-secret]")).toBeNull();
});

it("allows only safe HTTPS links and prevents opener/referrer access", () => {
  const { container } = render(<MessageContent message={{ ...message,
    htmlBody: `<a href="https://example.test/path">Safe</a><a href="javascript:alert(1)">Unsafe</a>
      <a href="http://example.test">Insecure</a><a href="data:text/html,unsafe">Data</a>
      <a href="https://user:password@example.test">Credentials</a>`,
  }} />);
  const link = screen.getByRole("link", { name: "Safe" });
  expect(link).toHaveAttribute("href", "https://example.test/path");
  expect(link).toHaveAttribute("rel", "noopener noreferrer");
  expect(link).toHaveAttribute("target", "_blank");
  expect(container.querySelectorAll("a[href]")).toHaveLength(1);
});

it("keeps plain user-authored markup literal and exposes attachment notices without fetching files", () => {
  const { container } = render(<MessageContent message={{ ...message, body: "<img src=x onerror=alert(1)>",
    contentNotice: "Attachments are available in Teams.", teamsUrl: "https://teams.microsoft.com/l/message/approved",
  }} />);
  expect(screen.getByText("<img src=x onerror=alert(1)>")).toBeVisible();
  expect(container.querySelector("img")).toBeNull();
  expect(screen.getByText("Attachments are available in Teams.")).toBeVisible();
  expect(screen.getByRole("link", { name: "Open in Teams" })).toHaveAttribute("href", "https://teams.microsoft.com/l/message/approved");
});

it("never renders deleted HTML, attachment notices or source links", () => {
  const { container } = render(<MessageContent message={{ ...message, deleted: true,
    htmlBody: "<strong>Deleted secret</strong>", contentNotice: "Deleted attachment", teamsUrl: "https://teams.microsoft.com/l/message/deleted",
  }} />);
  expect(screen.getByText("This message was deleted.")).toBeVisible();
  expect(screen.queryByText("Deleted secret")).not.toBeInTheDocument();
  expect(container.querySelector("a, strong")).toBeNull();
});

it("rejects a source URL outside the approved Teams hosts even if supplied by a faulty API", () => {
  render(<MessageContent message={{ ...message, teamsUrl: "https://teams.microsoft.com.evil.invalid/" }} />);
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

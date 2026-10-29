// @vitest-environment jsdom
import React from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import "@testing-library/jest-dom/vitest";
import { App } from "./App";
import { TestMessages } from "./TestMessages";
import type { Conversation, Operation } from "./api";

const conversations: Conversation[] = [
  { id: "direct", title: "Sam Rivera", kind: "chat", team: null, revision: 1, isDirect: true },
  { id: "group", title: "Project group", kind: "chat", team: null, revision: 1 },
  { id: "channel", title: "General", kind: "channel", team: "Team", revision: 1 },
];
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
type Command = { operationId: string; window: string; body: string; parentId: string | null };
function mockApi(send: (command: Command, number: number) => Response | Promise<Response> = command => json({
  id: command.operationId, state: "confirmed", messageId: command.operationId, error: null,
}, 201)) {
  const requests: { path: string; command: Command; headers: HeadersInit | undefined; at: number }[] = [];
  const fetch = vi.fn(async (path: string, init?: RequestInit) => {
    if (path === "/api/session") return json({
      id: "alex", name: "Alex", environment: "Development", source: "Microsoft Teams", csrf: "csrf",
    });
    if (path === "/api/conversations") return json(conversations);
    if (path === "/api/realtime/negotiate") return json({ code: "realtime_preparing", message: "Updates unavailable." }, 503);
    if (path.endsWith("/messages") && init?.method === "POST") {
      const command: Command = JSON.parse(String(init.body));
      requests.push({ path, command, headers: init.headers, at: Date.now() });
      return send(command, requests.length);
    }
    if (path.endsWith("/messages") || path.includes("/changes?")) return json({
      messages: [], cursor: null, changes: [], resetRequired: false, revision: 1,
      window: "authorized-window", parentMessage: null,
    });
    return json({ code: "unexpected_request", message: "Unexpected test request." }, 500);
  });
  vi.stubGlobal("fetch", fetch);
  return requests;
}
async function openChat() {
  await act(async () => { render(<App />); });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Open chat: Sam Rivera" }));
  });
}
function openPopup(count?: string, message?: string, concurrency?: string) {
  fireEvent.click(screen.getByText("Send test messages", { selector: "button" }));
  if (count !== undefined) fireEvent.change(screen.getByLabelText("Number of messages"), { target: { value: count } });
  if (message !== undefined) fireEvent.change(screen.getByLabelText("Test Message"), { target: { value: message } });
  if (concurrency !== undefined) fireEvent.change(screen.getByLabelText("Concurrent messages"), { target: { value: concurrency } });
}
async function submit() {
  await act(async () => { fireEvent.click(within(document.querySelector("dialog")!).getByText("Send messages", { selector: "button" })); });
}

beforeEach(() => {
  vi.useFakeTimers();
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) {
      this.setAttribute("open", "");
      this.querySelector<HTMLInputElement>("input")?.focus();
    } },
    close: { configurable: true, value: function (this: HTMLDialogElement) { this.removeAttribute("open"); } },
  });
});
afterEach(() => {
  cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers();
  Reflect.deleteProperty(HTMLDialogElement.prototype, "showModal");
  Reflect.deleteProperty(HTMLDialogElement.prototype, "close");
});

describe("personal-chat test messages", () => {
  it("opens with 30 messages, concurrency 1 and editable default text, and cancellation sends nothing", async () => {
    const requests = mockApi();
    await openChat();
    const composer = screen.getByLabelText("Write a message");
    fireEvent.change(composer, { target: { value: "My unsent draft" } });
    openPopup();
    expect(screen.getByRole("dialog", { name: "Send test messages" })).toHaveTextContent("Sam Rivera");
    expect(screen.getByLabelText("Number of messages")).toHaveValue(30);
    expect(screen.getByLabelText("Concurrent messages")).toHaveValue(1);
    expect(screen.getByLabelText("Number of messages")).toHaveFocus();
    expect(screen.getByLabelText("Test Message")).toHaveValue("Test message");
    expect(screen.getByRole("button", { name: "Send test messages" }).closest("header")).toContainElement(
      screen.getByRole("button", { name: "Change chat" }));
    fireEvent.click(within(document.querySelector("dialog")!).getByText("Cancel", { selector: "button" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(requests).toHaveLength(0);
    expect(composer).toHaveValue("My unsent draft");
  });

  describe("parallel batch controls", () => {
    it("keeps default concurrency independent of the message count and resets it on reopening", () => {
      const send = vi.fn();
      render(<TestMessages recipient="Sam Rivera" available send={send} />);
      openPopup("5");
      expect(screen.getByLabelText("Concurrent messages")).toHaveValue(1);
      fireEvent.change(screen.getByLabelText("Concurrent messages"), { target: { value: "2" } });
      expect(screen.getByLabelText("Concurrent messages")).toHaveValue(2);
      fireEvent.click(within(document.querySelector("dialog")!).getByText("Cancel", { selector: "button" }));
      openPopup();
      expect(screen.getByLabelText("Number of messages")).toHaveValue(30);
      expect(screen.getByLabelText("Concurrent messages")).toHaveValue(1);
      expect(send).not.toHaveBeenCalled();
    });

    it("sends one message at a time by default", async () => {
      const completions: (() => void)[] = [];
      const send = vi.fn((body: string) => new Promise<Operation>(resolve => {
        completions.push(() => resolve({ id: body, state: "confirmed", messageId: body, error: null }));
      }));
      render(<TestMessages recipient="Sam Rivera" available send={send} />);
      openPopup("3");
      await submit();
      expect(send).toHaveBeenCalledTimes(1);
      for (let index = 0; index < 3; index++) {
        expect(send.mock.calls[index][0]).toBe(`Test message - ${index + 1}`);
        await act(async () => { completions[index](); });
        expect(send).toHaveBeenCalledTimes(Math.min(3, index + 2));
      }
      expect(within(document.querySelector("dialog")!).getByRole("status")).toHaveTextContent(
        "3 of 3 attempted. 3 confirmed, 0 not sent, 0 pending or uncertain.");
    });

    it.each(["", "0", "-1", "1.5", "31"])("rejects invalid concurrency %j without dispatch", async concurrency => {
      const send = vi.fn();
      render(<TestMessages recipient="Sam Rivera" available send={send} />);
      openPopup("30", undefined, concurrency);
      const form = document.querySelector("dialog")!;
      expect(within(form).getByText("Send messages", { selector: "button" })).toBeDisabled();
      await act(async () => { fireEvent.submit(form.querySelector("form")!); });
      expect(send).not.toHaveBeenCalled();
    });

    it("starts the requested concurrency immediately and retains initial throttles after successful retries", async () => {
      const completions: (() => void)[] = [];
      let inFlight = 0;
      let maximum = 0;
      const send = vi.fn((body: string) => {
        maximum = Math.max(maximum, ++inFlight);
        const throttled = completions.length < 2;
        return new Promise<Operation>(resolve => completions.push(() => {
          inFlight--;
          resolve({ id: body, state: "confirmed", messageId: body, error: null,
            attempts: throttled ? 2 : 1, firstStatus: throttled ? 429 : 201, throttles: throttled ? 1 : 0 });
        }));
      });
      render(<TestMessages recipient="Sam Rivera" available send={send} />);
      openPopup("5", undefined, "2");
      await submit();
      expect(send).toHaveBeenCalledTimes(2);
      for (let index = 0; index < 5; index++) {
        await act(async () => { completions[index](); });
        expect(send).toHaveBeenCalledTimes(Math.min(5, index + 3));
      }
      expect(maximum).toBe(2);
      const status = within(document.querySelector("dialog")!).getByRole("status");
      expect(status).toHaveTextContent("5 of 5 attempted. 5 confirmed");
      expect(status).toHaveTextContent("Reported initial Graph responses: 3 accepted, 2 throttled.");
      expect(status).toHaveTextContent("7 attempts including 2 retries; 2 throttle responses.");
    });
  });

  it("sends exactly 30 immediate, uniquely identified numbered messages when concurrency is set to 30", async () => {
    const requests = mockApi();
    await openChat();
    fireEvent.change(screen.getByRole("textbox", { name: "Write a message" }), { target: { value: "Keep this draft" } });
    openPopup(undefined, undefined, "30");
    await submit();
    expect(requests).toHaveLength(30);
    expect(requests.map(item => item.command.body)).toEqual(Array.from({ length: 30 }, (_, index) => `Test message - ${index + 1}`));
    expect(new Set(requests.map(item => item.command.operationId)).size).toBe(30);
    for (const [index, item] of requests.entries()) {
      expect(item.path).toBe("/api/conversations/direct/messages");
      expect(item.command).toMatchObject({ window: "authorized-window", parentId: null });
      expect(item.headers).toEqual({ "Content-Type": "application/json", "X-CSRF-TOKEN": "csrf" });
      if (index) expect(item.at).toBe(requests[0].at);
    }
    expect(within(screen.getByRole("dialog")).getByRole("status")).toHaveTextContent(
      "30 of 30 attempted. 30 confirmed, 0 not sent, 0 pending or uncertain.");
    expect(screen.getByRole("textbox", { name: "Write a message" })).toHaveValue("Keep this draft");
  });

  it("uses custom text and count, continues after rejected and uncertain sends, and never retries them", async () => {
    const outcomes = ["unknown", "rejected", "confirmed"] as const;
    const requests = mockApi((command, number) => {
      const state = outcomes[number - 1];
      const operation: Operation = { id: command.operationId, state, messageId: state === "confirmed" ? "sent" : null, error: null };
      return json(operation, state === "confirmed" ? 201 : state === "rejected" ? 422 : 202);
    });
    await openChat();
    openPopup("3", "My test");
    await submit();
    expect(screen.getByText("Send test messages", { selector: "button" })).toBeEnabled();
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000); });
    expect(requests.map(item => item.command.body)).toEqual(["My test - 1", "My test - 2", "My test - 3"]);
    expect(within(screen.getByRole("dialog")).getByRole("status")).toHaveTextContent(
      "3 of 3 attempted. 1 confirmed, 1 not sent, 1 pending or uncertain.");
    fireEvent.click(within(document.querySelector("dialog")!).getByText("Close", { selector: "button" }));
    expect(screen.getByRole("button", { name: "Check original send" })).toBeVisible();
    expect(screen.getByText("Send test messages", { selector: "button" })).toBeEnabled();
    fireEvent.change(screen.getByLabelText("Write a message"), { target: { value: "An independent new message" } });
    expect(screen.getByRole("button", { name: "Send" })).toBeEnabled();
    expect(requests).toHaveLength(3);
  });

  it.each(["", "0", "-1", "1.5", "9007199254740992"])("rejects invalid message count %j", async count => {
    const requests = mockApi();
    await openChat();
    openPopup(count);
    expect(screen.getByRole("button", { name: "Send messages" })).toBeDisabled();
    expect(screen.getByRole("alert")).toHaveTextContent("Enter a positive whole number.");
    await act(async () => { fireEvent.submit(screen.getByRole("dialog").querySelector("form")!); });
    expect(requests).toHaveLength(0);
  });

  it("rejects blank text and reserves space for every numbered suffix", async () => {
    const requests = mockApi();
    await openChat();
    openPopup("30", "   ");
    expect(screen.getByRole("button", { name: "Send messages" })).toBeDisabled();
    const input = screen.getByLabelText("Test Message");
    expect(input).toHaveAttribute("maxlength", "4091");
    fireEvent.change(input, { target: { value: "x".repeat(4092) } });
    expect(within(document.querySelector("dialog")!).getByText("Send messages", { selector: "button" })).toBeDisabled();
    fireEvent.change(input, { target: { value: "x".repeat(4091) } });
    expect(within(document.querySelector("dialog")!).getByText("Send messages", { selector: "button" })).toBeEnabled();
    expect(requests).toHaveLength(0);
  });

  it("stops remaining messages without abandoning the in-flight outcome or double-submitting", async () => {
    let finish!: () => void;
    const requests = mockApi(command => new Promise(resolve => {
      finish = () => resolve(json({ id: command.operationId, state: "confirmed", messageId: "sent", error: null }, 201));
    }));
    await openChat();
    openPopup("3", undefined, "1");
    const form = screen.getByRole("dialog").querySelector("form")!;
    await act(async () => { fireEvent.submit(form); fireEvent.submit(form); });
    expect(requests).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Stop sending" }));
    await act(async () => { finish(); await vi.advanceTimersByTimeAsync(10_000); });
    expect(requests).toHaveLength(1);
    expect(within(screen.getByRole("dialog")).getByRole("status")).toHaveTextContent(
      "Batch stopped.1 of 3 attempted. 1 confirmed, 0 not sent, 0 pending or uncertain.");
    expect(within(screen.getByRole("dialog")).getByRole("status")).toHaveTextContent("2 messages were not attempted.");
  });

  it("cancels queued sends when leaving the chat", async () => {
    const requests = mockApi(() => new Promise(() => {}));
    await openChat();
    openPopup("3", undefined, "1");
    await submit();
    await act(async () => {
      fireEvent.click(within(screen.getByRole("navigation")).getByRole("button", { name: "Project groupChat" }));
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(requests).toHaveLength(1);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send test messages" })).not.toBeInTheDocument();
  });

  it("stops on loss of connectivity and does not automatically resume", async () => {
    let finish!: () => void;
    const requests = mockApi(command => new Promise(resolve => {
      finish = () => resolve(json({ id: command.operationId, state: "confirmed", messageId: "sent", error: null }, 201));
    }));
    await openChat();
    openPopup("3", undefined, "1");
    await submit();
    await act(async () => {
      vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
      window.dispatchEvent(new Event("offline"));
    });
    await act(async () => { finish(); });
    expect(requests).toHaveLength(1);
    expect(within(screen.getByRole("dialog")).getByRole("status")).toHaveTextContent("Batch stopped.");
    await act(async () => {
      vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
      window.dispatchEvent(new Event("online"));
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(requests).toHaveLength(1);
  });

  it("never offers the button in a group chat or channel", async () => {
    mockApi();
    await openChat();
    const navigation = within(screen.getByRole("navigation"));
    await act(async () => { fireEvent.click(navigation.getByRole("button", { name: "Project groupChat" })); });
    expect(screen.queryByRole("button", { name: "Send test messages" })).not.toBeInTheDocument();
    await act(async () => { fireEvent.click(navigation.getByRole("button", { name: "Channels" })); });
    await act(async () => { fireEvent.click(navigation.getByRole("button", { name: "Open channel: Team / General" })); });
    expect(screen.queryByRole("button", { name: "Send test messages" })).not.toBeInTheDocument();
  });

  it("starts later test messages before earlier confirmations, and updates each icon independently", async () => {
    const finish: (() => void)[] = [];
    const requests = mockApi(command => new Promise(resolve => {
      finish.push(() => resolve(json({ id: command.operationId, state: "confirmed", messageId: command.operationId, error: null }, 201)));
    }));
    await openChat();
    openPopup("3", "Independent", "3");
    await submit();
    expect(requests.map(item => item.command.body)).toEqual(["Independent - 1", "Independent - 2", "Independent - 3"]);
    expect(within(screen.getByRole("dialog")).getByRole("status")).toHaveTextContent(
      "3 of 3 attempted. 0 confirmed, 0 not sent, 3 pending or uncertain.");
    expect(screen.getAllByRole("status", { name: "Sending" })).toHaveLength(3);
    fireEvent.click(within(document.querySelector("dialog")!).getByText("Close", { selector: "button" }));
    const composer = screen.getByLabelText("Write a message");
    fireEvent.change(composer, { target: { value: "A new manual message" } });
    await act(async () => { fireEvent.submit(composer.closest("form")!); });
    expect(composer).toHaveValue("");
    expect(screen.getByText("A new manual message")).toBeVisible();
    expect(requests).toHaveLength(4);
    await act(async () => { finish[1](); });
    expect(within(screen.getByText("Independent - 2").closest("article")!).getByRole("status", { name: "Sent" })).toBeVisible();
    expect(within(screen.getByText("Independent - 1").closest("article")!).getByRole("status", { name: "Sending" })).toBeVisible();
    await act(async () => { finish[0](); finish[2](); finish[3](); });
    expect(screen.getAllByRole("status", { name: "Sent" })).toHaveLength(4);
    expect(requests).toHaveLength(4);
  });

  it("accepts consecutive manual sends immediately without dispatching duplicate submissions", async () => {
    const requests = mockApi(() => new Promise(() => {}));
    await openChat();
    const composer = screen.getByLabelText("Write a message");
    fireEvent.change(composer, { target: { value: "First manual send" } });
    const form = composer.closest("form")!;
    await act(async () => { fireEvent.submit(form); fireEvent.submit(form); });
    expect(requests).toHaveLength(1);
    fireEvent.change(composer, { target: { value: "Second manual send" } });
    expect(within(form).getByText("Send", { selector: "button" })).toBeEnabled();
    await act(async () => { fireEvent.submit(form); });
    expect(screen.getByText("First manual send")).toBeVisible();
    expect(screen.getByText("Second manual send")).toBeVisible();
    expect(composer).toHaveValue("");
    expect(requests).toHaveLength(2);
    expect(requests.map(item => item.command.body)).toEqual(["First manual send", "Second manual send"]);
    expect(screen.getAllByRole("status", { name: "Sending" })).toHaveLength(2);
  });

  it("disables new manual sends while offline and never automatically submits their draft", async () => {
    const requests = mockApi();
    await openChat();
    const composer = screen.getByLabelText("Write a message");
    fireEvent.change(composer, { target: { value: "Already dispatched" } });
    await act(async () => { fireEvent.submit(composer.closest("form")!); });
    await act(async () => {
      vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
      window.dispatchEvent(new Event("offline"));
      await vi.advanceTimersByTimeAsync(2000);
    });
    fireEvent.change(composer, { target: { value: "Not dispatched" } });
    expect(requests).toHaveLength(1);
    expect(within(composer.closest("form")!).getByText("Send", { selector: "button" })).toBeDisabled();
    await act(async () => {
      vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
      window.dispatchEvent(new Event("online"));
      await vi.advanceTimersByTimeAsync(2000);
    });
    expect(requests).toHaveLength(1);
    expect(composer).toHaveValue("Not dispatched");
  });
});

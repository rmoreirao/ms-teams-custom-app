import { test as base, expect } from "@playwright/test";
import type { Page, WebSocketRoute } from "@playwright/test";
import type { ChannelOperation, Conversation, DeliveryState, Message, Operation, RealtimeEvent, Session } from "../src/api";

export const session: Session = {
  id: "alex", name: "Alex Morgan", csrf: "test-only-csrf", environment: "Development",
  source: "Microsoft Teams", scopedValidation: true, canStartDirectChats: false, canCreateDirectChats: false,
};
const fixtureOrigin = Date.now() - 86400_000;
export const message = (id: string, body: string, parentId: string | null = null): Message => ({
  id, body, parentId, actorId: "sam", author: "Sam Rivera", deleted: false,
  createdAt: new Date(fixtureOrigin + Number(id.replace(/\D/g, "") || 100) * 1000).toISOString(),
  modifiedAt: new Date(fixtureOrigin + 7200_000).toISOString(),
});
export const catalog: Conversation[] = [
  { id: "chat-handover", title: "Sam Rivera", kind: "chat", team: null, revision: 1, isDirect: true },
  { id: "chat-empty", title: "New conversation", kind: "chat", team: null, revision: 1 },
  { id: "chat-project", title: "Opening team", kind: "chat", team: null, revision: 1 },
  { id: "channel-updates", title: "General", kind: "channel", team: "Northwind", teamId: "northwind", revision: 1, canPost: true, canReply: true },
];

/** Browser-runner doubles only: the shipped shell and real Web PubSub SDK are unmodified. */
export async function installLiveFixture(page: Page) {
  await page.route("**/*", route => ["localhost", "127.0.0.1", "[::1]"].includes(new URL(route.request().url()).hostname) ?
    route.fallback() : route.abort("blockedbyclient"));
  const sockets = new Set<WebSocketRoute>();
  const state = {
    session: { ...session } as Session | null,
    conversations: structuredClone(catalog),
    messages: new Map<string, Message[]>([
      ["chat-handover", Array.from({ length: 30 }, (_, i) => message(String(i + 1), `Conversation context ${i + 1}.`))],
      ["chat-empty", []],
      ["chat-project", [message("31", "The team plan is ready for review.")]],
      ["channel-updates", [
        { ...message("32", "Text-safety fixture: <img src=x onerror=alert(1)>"),
          htmlBody: "<p>Text-safety fixture: <strong>safe content</strong><img src=x onerror=alert(1)><script>alert(1)</script></p>" },
        message("33", "Plan for the next shift."), message("34", "Existing thread reply.", "33"),
      ]],
    ]),
    operations: new Map<string, Operation>(),
    nextSend: "confirmed" as DeliveryState | "throttle" | "csrf" | "forbidden" | "gateway" | "unreadable",
    sendRequests: [] as { path: string; body: { operationId: string; body: string; parentId: string | null; window: string } }[],
    reads: [] as string[],
    denied: new Set<string>(),
    historyFrom: undefined as string | undefined,
    channelOperations: new Map<string, ChannelOperation>(),
    channelRequests: [] as string[],
    channelEvents: [] as RealtimeEvent[],
    preparingViews: new Set<string>(),
    failedViews: new Set<string>(),
    messageReads: [] as string[],
    contentReads: [] as string[],
    canSend: true,
  };
  await page.routeWebSocket("wss://realtime.example.test/**", socket => {
    sockets.add(socket);
    socket.send(JSON.stringify({ type: "system", event: "connected", userId: "alex", connectionId: "test-connection" }));
    socket.onMessage(data => {
      const value = JSON.parse(String(data));
      if (value.type === "ping") socket.send(JSON.stringify({ type: "pong" }));
    });
    socket.onClose(() => sockets.delete(socket));
  });
  const hint = (conversationId: string) => {
    const revision = state.conversations.find(item => item.id === conversationId)?.revision ?? 1;
    for (const socket of sockets) socket.send(JSON.stringify({
      type: "message", from: "server", dataType: "json", data: { conversationId, revision },
    }));
  };
  const update = (conversationId: string, messages: Message[]) => {
    state.messages.set(conversationId, messages);
    const conversation = state.conversations.find(item => item.id === conversationId);
    if (conversation) conversation.revision++;
    hint(conversationId);
  };
  const event = (value: Omit<RealtimeEvent, "sequence"> & { sequence?: number }) => {
    const data: RealtimeEvent = { ...value, sequence: value.sequence ?? state.channelEvents.length + 1 };
    if (data.sequence > 0) state.channelEvents.push(data);
    for (const socket of sockets) socket.send(JSON.stringify({
      type: "message", from: "server", dataType: "json", data,
    }));
  };
  const channelOperation = (id: string, kind: ChannelOperation["kind"], values: Partial<ChannelOperation>): ChannelOperation => ({
    id, kind, state: "ready", revision: 1, error: null, teams: null, channels: null, cursor: null, conversation: null, ...values,
  });
  await page.route("**/api/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const failure = (status: number, code: string, text: string) =>
      route.fulfill({ status, json: { code, message: text } });
    if (!state.session) return failure(401, "authentication_required", "Sign in again.");
    if (request.method() === "GET") state.reads.push(path);
    if (path === "/api/session") return route.fulfill({ json: state.session });
    if (request.method() === "POST" && request.headers()["x-csrf-token"] !== state.session.csrf)
      return failure(403, "csrf_invalid", "Your request token expired. Sign in again.");
    if (path === "/api/realtime/negotiate") return route.fulfill({
      json: { transport: "webpubsub", url: "wss://realtime.example.test/client/hubs/test" },
    });
    if (path === "/api/realtime/changes") {
      const after = Number(url.searchParams.get("after") ?? 0);
      return route.fulfill({ json: { events: state.channelEvents.filter(item => item.sequence > after),
        sequence: state.channelEvents.at(-1)?.sequence ?? 0, resetRequired: false, more: false } });
    }
    if (path === "/api/teams" || path === "/api/teams/northwind/channels" || path === "/api/channels/open") {
      state.channelRequests.push(request.url());
      const id = request.method() === "POST" ? request.postDataJSON().operationId : url.searchParams.get("requestId")!;
      const value = path === "/api/teams" ? channelOperation(id, "teams", { teams: [{ id: "northwind", name: "Northwind" }] }) :
        path.includes("/channels") && request.method() === "GET" ? channelOperation(id, "channels", { channels: [
          { id: "source-general", teamId: "northwind", name: "General", teamName: "Northwind", selection: "actor-bound-selection" },
        ] }) : channelOperation(id, "open", { state: "preparing" });
      state.channelOperations.set(id, value);
      return route.fulfill({ status: value.state === "ready" ? 200 : 202, json: value });
    }
    if (path.startsWith("/api/channel-operations/")) {
      const value = state.channelOperations.get(path.split("/").at(-1)!);
      return value ? route.fulfill({ json: value }) : failure(404, "not_found", "Operation not found.");
    }
    if (path === "/api/conversations") return route.fulfill({ json: state.conversations });
    const match = /^\/api\/conversations\/([^/]+)\/(messages|changes|operations|access)(?:\/([^/]+))?$/.exec(path);
    if (!match || state.denied.has(match[1]) || !state.conversations.some(item => item.id === match[1]))
      return failure(404, "access_denied", "This conversation is no longer available.");
    const [, id, action, operationId] = match;
    const revision = state.conversations.find(item => item.id === id)!.revision;
    const messages = state.messages.get(id) ?? [];
    const access = { historyFrom: state.historyFrom ?? new Date(Date.now() - 7 * 86400_000).toISOString(),
      validUntil: new Date(Date.now() + 300_000).toISOString(), epoch: 1, canSend: state.canSend };
    if (action === "access") {
      const parent = url.searchParams.get("parent");
      const root = parent ? messages.find(item => item.id === parent && item.parentId === null) : null;
      if (parent && (!root || root.deleted || root.createdAt < access.historyFrom))
        return failure(404, "access_denied", "This thread is no longer available.");
      return route.fulfill({ json: access });
    }
    if (action === "operations") {
      const operation = state.operations.get(operationId);
      return operation ? route.fulfill({ json: operation }) : failure(404, "not_found", "Operation not found.");
    }
    const parent = url.searchParams.get("parent");
    const matching = messages.filter(item => item.parentId === parent && item.createdAt >= access.historyFrom);
    if (request.method() === "GET" && (action === "messages" || action === "changes"))
      state.contentReads.push(path + url.search);
    if (request.method() === "GET" && (action === "messages" || action === "changes") &&
        state.failedViews.has(`${id}:${parent ?? ""}`))
      return failure(503, "read_refresh_unavailable", "Microsoft Teams could not be reached.");
    const replyCount = parent && matching.length < 21 ? matching.length : null;
    if (action === "changes") return route.fulfill({ json: {
      changes: [], revision, resetRequired: false, messages, historyFrom: state.historyFrom, window: "test-command-window", access,
      replyCount,
    } });
    if (request.method() === "GET") {
      state.messageReads.push(path + url.search);
      if (state.preparingViews.has(`${id}:${parent ?? ""}`))
        return route.fulfill({ status: 202, json: { state: "pending", retryAfterMs: 1000, access } });
      const end = Number(url.searchParams.get("cursor") ?? matching.length);
      const preview = url.searchParams.get("preview") === "true";
      const size = preview ? 2 : 20;
      return route.fulfill({ json: {
        messages: matching.slice(Math.max(0, end - size), end), cursor: end > size ? String(end - size) : null,
        revision, window: "test-command-window", parentMessage: parent ? messages.find(item => item.id === parent) : null, access,
        replyCount, preview,
      } });
    }
    const body = request.postDataJSON();
    state.sendRequests.push({ path, body });
    const outcome = state.nextSend;
    if (outcome === "csrf") return failure(403, "csrf_invalid", "Your request token expired. Sign in again.");
    if (outcome === "forbidden") return failure(404, "access_denied", "You no longer have access to this conversation.");
    if (outcome === "throttle") return failure(429, "throttled", "Teams is busy. Wait before a new attempt.");
    if (outcome === "gateway") return failure(502, "upstream_unavailable", "Teams did not return a definitive result.");
    if (outcome === "unreadable") return route.fulfill({ status: 502, body: "upstream unavailable" });
    const operation: Operation = { id: body.operationId, state: outcome, messageId: null,
      error: outcome === "rejected" ? "Teams rejected this message." : null };
    if (outcome === "confirmed") {
      operation.messageId = `sent-${100 + state.sendRequests.length}`;
      messages.push({ ...message(operation.messageId, body.body, body.parentId),
        actorId: state.session.id, author: state.session.name });
      update(id, messages);
    }
    state.operations.set(operation.id, operation);
    return route.fulfill({ status: outcome === "confirmed" ? 201 : outcome === "rejected" ? 422 : 202, json: operation });
  });
  await page.route("**/auth/signout", async route => {
    expect(route.request().headers()["x-csrf-token"]).toBe(state.session?.csrf);
    state.session = null;
    await route.fulfill({ status: 200, json: {} });
  });
  return { state, hint, update, event, disconnect: () => {
    for (const socket of sockets) socket.close({ code: 1000, reason: "test disconnect" });
    sockets.clear();
  } };
}
export type LiveFixture = Awaited<ReturnType<typeof installLiveFixture>>;
export const test = base.extend<{ live: LiveFixture }>({
  live: [async ({ page }, use) => { await use(await installLiveFixture(page)); }, { auto: true }],
});
export { expect };

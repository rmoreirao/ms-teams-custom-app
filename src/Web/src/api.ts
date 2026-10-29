export interface Session { id: string; name: string; environment: "Development"; source: "Microsoft Teams"; csrf: string; scopedValidation?: boolean; backgroundAuthentication?: "delegated-validation" | "application"; entitlementProvider?: "External" | "Graph"; canStartDirectChats?: boolean; canCreateDirectChats?: false; canBrowseChannels?: boolean; channelRegistry?: boolean }
export interface Person { id: string; name: string; email: string | null }
export interface Conversation { id: string; title: string; kind: "chat" | "channel"; team: string | null; revision: number; isDirect?: boolean; teamId?: string | null; canPost?: boolean; canReply?: boolean; capabilityReason?: string | null }
export interface Message { id: string; actorId: string; author: string; body: string; createdAt: string; modifiedAt: string; parentId: string | null; deleted: boolean; contentNotice?: string | null; teamsUrl?: string | null; htmlBody?: string | null }
export interface ReadAccess { historyFrom: string; validUntil: string; epoch: number; canSend?: boolean }
export interface CacheReadEvidence { kind: "page" | "delta"; observedAt: string; hydrationRequested: boolean;
  steps: { layer: string; outcome: string; count: number; milliseconds: number }[] }
export interface ReadObservation { observedAt: string; kind: "page" | "delta"; outcome: "ready" | "pending" | "failed";
  count: number | null; revision: number | null; diagnostics?: CacheReadEvidence; error?: string }
export interface CacheCleanup { id: string; cosmos: boolean; redis: boolean; targetRevision: number; startedAt: string;
  cosmosState: string; redisState: string; removedMessages: number; removedPages: number;
  cosmosError: string | null; redisError: string | null; complete: boolean }
export interface CacheSnapshot { observedAt: string; revision: number; generation: number; cosmosMessages: number | null;
  cosmosCountComplete: boolean; coverageComplete: boolean; readViewComplete: boolean; cosmosError: string | null;
  redis: { status: string; pages: number | null; maximumTtlSeconds: number | null; complete: boolean }; cleanup: CacheCleanup | null }
export interface MessagePage { messages: Message[]; cursor: string | null; revision: number; window: string; parentMessage: Message | null; access?: ReadAccess; diagnostics?: CacheReadEvidence; replyCount?: number | null; preview?: boolean; replyCountExpiresAt?: string | null }
export interface PendingRead { state: "pending"; retryAfterMs: number; access: ReadAccess | null; diagnostics?: CacheReadEvidence }
export const isPendingRead = (value: unknown): value is PendingRead =>
  typeof value === "object" && value !== null && "state" in value;
export interface ChangePage { changes: { revision: number; messageId: string; parentId: string | null }[]; revision: number; resetRequired: boolean; messages?: Message[]; historyFrom?: string; window?: string; access?: ReadAccess; cacheReset?: boolean; diagnostics?: CacheReadEvidence; replyCount?: number | null; replyCountExpiresAt?: string | null }
export interface RealtimeHint { conversationId: string; revision: number }
export interface RealtimeEvent { type: "channel.operation.changed" | "channel.catalog.changed" | "channel.view.ready";
  schemaVersion: 1; revision: number; sequence: number; operationId?: string | null; conversationId?: string | null;
  parentId?: string | null; generation?: number | null }
export interface ChannelTeam { id: string; name: string }
export interface DiscoveredChannel { id: string; teamId: string; name: string; teamName: string; selection: string }
export interface ChannelOperation { id: string; kind: "teams" | "channels" | "open";
  state: "pending" | "preparing" | "deferred" | "ready" | "denied" | "failed" | "capacity";
  revision: number; error: string | null; teams: ChannelTeam[] | null; channels: DiscoveredChannel[] | null;
  cursor: string | null; conversation: Conversation | null }
export type DeliveryState = "pending" | "dispatching" | "confirmed" | "rejected" | "unknown";
export interface Operation { id: string; state: DeliveryState; messageId: string | null; error: string | null;
  attempts?: number; throttles?: number; firstStatus?: number | null; retryAt?: string | null }
export interface SendCommand { operationId: string; window: string; body: string; parentId: string | null }
export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public access?: ReadAccess, public diagnostics?: CacheReadEvidence) { super(message); }
}
async function request<T>(path: string, init?: RequestInit): Promise<{ body: T; headers: Headers; status: number }> {
  const response = await fetch(path, { ...init, credentials: "same-origin", cache: "no-store" });
  const text = await response.text();
  let body: unknown;
  try { body = text ? JSON.parse(text) : undefined; }
  catch { throw new ApiError(response.status, "invalid_response", "The service returned an invalid response. Refresh the page."); }
  if (!response.ok) {
    const error = body as { code?: string; message?: string; access?: ReadAccess; diagnostics?: CacheReadEvidence } | undefined;
    throw new ApiError(response.status, error?.code ?? "request_failed",
      error?.message ?? (response.status === 401 ? "Sign in to continue." : "The request failed. Try again."), error?.access, error?.diagnostics);
  }
  return { body: body as T, headers: response.headers, status: response.status };
}
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  return (await request<T>(path, init)).body;
}
export async function readMessages(path: string, signal?: AbortSignal): Promise<MessagePage | PendingRead> {
  const read = (bounded = signal) => request<MessagePage | PendingRead>(path, { signal: bounded, headers: { "X-Cache-Diagnostics": "true" } });
  const response = new URL(path, "https://local").searchParams.has("parent") ? await threadRead(read, signal) : await read();
  const value = response.body;
  if (response.status === 202 && value && isPendingRead(value) && value.state === "pending" &&
      Number.isInteger(value.retryAfterMs) && value.retryAfterMs >= 100 && value.retryAfterMs <= 30000 &&
      "access" in value) return value;
  if (response.status === 200 && value && !isPendingRead(value) && Array.isArray(value.messages) &&
      Number.isSafeInteger(value.revision) && value.revision >= 0 && typeof value.window === "string") return value;
  throw new ApiError(response.status, "invalid_response", "History could not be loaded because the read response was invalid.");
}
export function readObservation(value: MessagePage | PendingRead | ChangePage, kind: "page" | "delta"): ReadObservation {
  const pending = isPendingRead(value);
  return { observedAt: value.diagnostics?.observedAt ?? new Date().toISOString(), kind,
    outcome: pending ? "pending" : "ready", count: pending ? null : value.messages?.length ?? 0,
    revision: pending ? null : value.revision, diagnostics: value.diagnostics };
}
export async function conversationPage(cursor?: string | null, signal?: AbortSignal): Promise<{ items: Conversation[]; cursor: string | null }> {
  const response = await request<Conversation[]>(`/api/conversations${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ""}`, { signal });
  return { items: response.body, cursor: response.headers.get("X-Next-Cursor") };
}
export function post<T>(path: string, body: unknown, csrf: string, signal?: AbortSignal): Promise<T> {
  return api<T>(path, { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf }, body: JSON.stringify(body), signal });
}

export async function sendMessage(conversationId: string, command: SendCommand, csrf: string): Promise<Operation> {
  const outcome = (state: DeliveryState, error: string): Operation =>
    ({ id: command.operationId, state, messageId: null, error });
  try {
    const response = await fetch(`/api/conversations/${conversationId}/messages`, {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf },
      body: JSON.stringify(command),
    });
    let result: unknown;
    try { result = await response.json(); }
    catch {
      return response.status >= 400 && response.status < 500
        ? outcome("rejected", "The gateway rejected this request. Review the message and sign-in state before a new attempt.")
        : outcome("unknown", "The send response could not be read. Check the original operation; do not resend.");
    }
    if ([201, 202, 422].includes(response.status)) {
      if (isOperation(result) && result.id === command.operationId) return result;
      return outcome("unknown", "The send response was invalid. Check the original operation; do not resend.");
    }
    const message = typeof result === "object" && result !== null && "message" in result &&
      typeof result.message === "string" ? result.message : "The service did not return a valid send outcome.";
    // A server-side pre-dispatch denial is definitive; generic 5xx is not.
    return outcome(response.status >= 400 && response.status < 500 ? "rejected" : "unknown", message);
  } catch (error) {
    return outcome("unknown", error instanceof Error ? error.message : "The send failed. Check the original operation; do not resend.");
  }
}

function isOperation(value: unknown): value is Operation {
  return typeof value === "object" && value !== null &&
    "id" in value && typeof value.id === "string" &&
    "state" in value && typeof value.state === "string" &&
    ["pending", "dispatching", "confirmed", "rejected", "unknown"].includes(value.state) &&
    "messageId" in value && (value.messageId === null || typeof value.messageId === "string") &&
    (value.state !== "confirmed" || typeof value.messageId === "string" && value.messageId.length > 0) &&
    "error" in value && (value.error === null || typeof value.error === "string") &&
    (!("attempts" in value) || Number.isSafeInteger(value.attempts) && Number(value.attempts) >= 0) &&
    (!("throttles" in value) || Number.isSafeInteger(value.throttles) && Number(value.throttles) >= 0) &&
    (!("firstStatus" in value) || value.firstStatus === null ||
      Number.isInteger(value.firstStatus) && Number(value.firstStatus) >= 100 && Number(value.firstStatus) <= 599) &&
    (!("retryAt" in value) || value.retryAt === null ||
      typeof value.retryAt === "string" && Number.isFinite(Date.parse(value.retryAt)));
}
import { threadRead } from "./threadReads";

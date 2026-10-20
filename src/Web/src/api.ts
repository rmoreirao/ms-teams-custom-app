export interface Session { id: string; name: string; environment: "Development"; source: "Microsoft Teams"; csrf: string; scopedValidation?: boolean; canStartDirectChats?: boolean; canCreateDirectChats?: false }
export interface Person { id: string; name: string; email: string | null }
export interface Conversation { id: string; title: string; kind: "chat" | "channel"; team: string | null; revision: number; isDirect?: boolean }
export interface Message { id: string; actorId: string; author: string; body: string; createdAt: string; modifiedAt: string; parentId: string | null; deleted: boolean; contentNotice?: string | null; teamsUrl?: string | null; htmlBody?: string | null }
export interface MessagePage { messages: Message[]; cursor: string | null; revision: number; window: string; parentMessage: Message | null }
export interface ChangePage { changes: { revision: number; messageId: string; parentId: string | null }[]; revision: number; resetRequired: boolean; messages?: Message[]; historyFrom?: string; window?: string }
export interface RealtimeHint { conversationId: string; revision: number }
export type DeliveryState = "pending" | "dispatching" | "confirmed" | "rejected" | "unknown";
export interface Operation { id: string; state: DeliveryState; messageId: string | null; error: string | null }
export interface SendCommand { operationId: string; window: string; body: string; parentId: string | null }
export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) { super(message); }
}
async function request<T>(path: string, init?: RequestInit): Promise<{ body: T; headers: Headers }> {
  const response = await fetch(path, { ...init, credentials: "same-origin", cache: "no-store" });
  const text = await response.text();
  let body: unknown;
  try { body = text ? JSON.parse(text) : undefined; }
  catch { throw new ApiError(response.status, "invalid_response", "The service returned an invalid response. Refresh the page."); }
  if (!response.ok) {
    const error = body as { code?: string; message?: string } | undefined;
    throw new ApiError(response.status, error?.code ?? "request_failed",
      error?.message ?? (response.status === 401 ? "Sign in to continue." : "The request failed. Try again."));
  }
  return { body: body as T, headers: response.headers };
}
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  return (await request<T>(path, init)).body;
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
    "error" in value && (value.error === null || typeof value.error === "string");
}

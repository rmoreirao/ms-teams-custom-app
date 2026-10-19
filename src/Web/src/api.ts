export interface Session { id: string; name: string; simulated: boolean; localAuth: boolean; scenarioAdmin: boolean; csrf: string; basicValidation?: boolean }
export interface Conversation { id: string; title: string; kind: "chat" | "channel"; team: string | null; revision: number }
export interface Message { id: string; actorId: string; author: string; body: string; createdAt: string; modifiedAt: string; parentId: string | null; deleted: boolean }
export interface MessagePage { messages: Message[]; cursor: string | null; revision: number; window: string; simulated: boolean; parentMessage: Message | null }
export type DeliveryState = "pending" | "dispatching" | "confirmed" | "rejected" | "unknown";
export interface Operation { id: string; state: DeliveryState; messageId: string | null; error: string | null; simulated: boolean }
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
export function post<T>(path: string, body: unknown, csrf: string): Promise<T> {
  return api<T>(path, { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf }, body: JSON.stringify(body) });
}

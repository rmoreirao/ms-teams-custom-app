import { useRef } from "react";
import { sendMessage } from "./api";
import type { Operation, SendCommand, Session } from "./api";

export function useSendQueue(session: Session | null, online: boolean) {
  const context = useRef({ session, online });
  context.current = { session, online };
  return (conversationId: string, command: SendCommand, signal?: AbortSignal): Promise<Operation> => {
    const { session: current, online: connected } = context.current;
    const error = signal?.aborted ? "Cancelled before dispatch. No message was sent."
      : !current ? "Sign in before sending."
      : !connected || !navigator.onLine ? "Not sent because you are offline. Review the message before a new attempt." : null;
    if (error || !current)
      return Promise.resolve({ id: command.operationId, state: "rejected", messageId: null, error });
    return sendMessage(conversationId, command, current.csrf);
  };
}

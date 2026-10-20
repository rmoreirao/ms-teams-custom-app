import { useEffect, useRef } from "react";
import { sendMessage } from "./api";
import type { Operation, SendCommand, Session } from "./api";

type Send = {
  conversationId: string; command: SendCommand; session: Session; signal?: AbortSignal;
  resolve: (operation: Operation) => void;
};

export function useSendQueue(session: Session | null, online: boolean) {
  const context = useRef({ session, online });
  context.current = { session, online };
  const queue = useRef<Send[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const lastDispatch = useRef(0);
  const reject = (job: Send, error: string) => job.resolve({
    id: job.command.operationId, state: "rejected", messageId: null, error,
  });
  const cancel = (error: string) => {
    clearTimeout(timer.current); timer.current = undefined;
    for (const job of queue.current.splice(0)) reject(job, error);
  };
  const pump = () => {
    if (timer.current !== undefined) return;
    while (queue.current.length) {
      const job = queue.current[0];
      if (job.signal?.aborted) {
        queue.current.shift();
        reject(job, "Cancelled before dispatch. No message was sent.");
        continue;
      }
      if (!context.current.online || !navigator.onLine || context.current.session?.id !== job.session.id) {
        cancel("Not sent because the connection or session changed before dispatch. Review the message before a new attempt.");
        return;
      }
      const delay = Math.max(0, lastDispatch.current + 1500 - Date.now());
      if (delay) {
        timer.current = setTimeout(() => { timer.current = undefined; pump(); }, delay);
        return;
      }
      queue.current.shift();
      lastDispatch.current = Date.now();
      // Pace starts, not confirmations: previous requests can still be in flight.
      void sendMessage(job.conversationId, job.command, job.session.csrf).then(job.resolve);
    }
  };
  useEffect(() => {
    if (!online) cancel("Not sent because you went offline before dispatch. Review the message before a new attempt.");
  }, [online]);
  useEffect(() => () => {
    cancel("Not sent because the session closed before dispatch.");
  }, [session?.id]);
  return (conversationId: string, command: SendCommand, signal?: AbortSignal): Promise<Operation> =>
    new Promise(resolve => {
      const current = context.current.session;
      if (!current) {
        resolve({ id: command.operationId, state: "rejected", messageId: null, error: "Sign in before sending." });
        return;
      }
      queue.current.push({ conversationId, command, session: current, signal, resolve });
      pump();
    });
}

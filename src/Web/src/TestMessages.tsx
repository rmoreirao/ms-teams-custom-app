import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Operation } from "./api";

type Progress = { total: number; attempted: number; confirmed: number; rejected: number; uncertain: number };

function pause(signal: AbortSignal) {
  return new Promise<void>(resolve => {
    const finish = () => { clearTimeout(timer); signal.removeEventListener("abort", finish); resolve(); };
    const timer = setTimeout(finish, 1500);
    signal.addEventListener("abort", finish, { once: true });
    if (signal.aborted) finish();
  });
}

export function TestMessages({ recipient, available, send }: {
  recipient: string; available: boolean;
  send: (body: string, signal?: AbortSignal) => Promise<Operation>;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const controller = useRef<AbortController | null>(null);
  const active = useRef(true);
  const latest = useRef({ available, send });
  latest.current = { available, send };
  const [count, setCount] = useState("30");
  const [message, setMessage] = useState("Test message");
  const [running, setRunning] = useState(false);
  const [scheduling, setScheduling] = useState(false);
  const [progress, setProgress] = useState<Progress | null>(null);
  const [error, setError] = useState("");
  const total = Number(count);
  const valid = Number.isSafeInteger(total) && total > 0;
  const messageLimit = 4096 - ` - ${valid ? total : 30}`.length;
  const validMessage = !!message.trim() && message.length <= messageLimit;
  useEffect(() => {
    active.current = true;
    return () => { active.current = false; controller.current?.abort(); };
  }, []);
  const open = () => {
    if (!controller.current) { setCount("30"); setMessage("Test message"); setProgress(null); setError(""); }
    dialog.current?.showModal();
  };
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (controller.current || !latest.current.available || !valid || !validMessage) return;
    const batch = new AbortController();
    controller.current = batch;
    setRunning(true); setScheduling(true); setError("");
    const result: Progress = { total, attempted: 0, confirmed: 0, rejected: 0, uncertain: 0 };
    setProgress({ ...result });
    const confirmations: Promise<void>[] = [];
    try {
      for (let number = 1; number <= total; number++) {
        if (batch.signal.aborted || !latest.current.available) break;
        result.attempted++;
        result.uncertain++;
        setProgress({ ...result });
        confirmations.push(latest.current.send(`${message} - ${number}`, batch.signal).then(operation => {
          if (operation.state === "confirmed") { result.confirmed++; result.uncertain--; }
          else if (operation.state === "rejected") { result.rejected++; result.uncertain--; }
          if (active.current) setProgress({ ...result });
        }, failure => {
          batch.abort();
          if (active.current) setError(failure instanceof Error ? failure.message : "A send could not be started. Check the message statuses before trying again.");
        }));
        if (number < total && !batch.signal.aborted) await pause(batch.signal);
      }
    } catch (failure) {
      if (active.current) setError(failure instanceof Error ? failure.message : "The batch stopped unexpectedly. Check the original sends before trying again.");
    } finally {
      if (active.current) setScheduling(false);
      await Promise.all(confirmations);
      controller.current = null;
      if (active.current) setRunning(false);
    }
  };
  return <>
    <button type="button" className="test-messages-button" disabled={!available} onClick={open}>
      Send test messages
    </button>
    <dialog ref={dialog} className="test-messages-dialog" aria-labelledby="test-messages-title"
      aria-describedby="test-messages-description" onCancel={event => {
        if (controller.current) { event.preventDefault(); controller.current.abort(); }
      }}>
      <form onSubmit={event => void submit(event)}>
        <h2 id="test-messages-title">Send test messages</h2>
        <p id="test-messages-description">Send numbered messages to {recipient} in this Teams chat.</p>
        {!progress ? <>
          <label htmlFor="test-message-count">Number of messages</label>
          <input id="test-message-count" type="number" min="1" step="1" required autoFocus value={count}
            aria-invalid={!valid} aria-describedby="test-message-preview test-message-count-error"
            onChange={event => setCount(event.target.value)} />
          {!valid && <p id="test-message-count-error" role="alert">Enter a positive whole number.</p>}
          <label htmlFor="test-message-text">Test Message</label>
          <input id="test-message-text" type="text" required maxLength={messageLimit} value={message}
            aria-invalid={!validMessage} aria-describedby="test-message-preview test-message-text-error"
            onChange={event => setMessage(event.target.value)} />
          {!validMessage && <p id="test-message-text-error" role="alert">Enter a test message of 1 to {messageLimit} characters, including some non-whitespace text.</p>}
          <p id="test-message-preview">Each message uses the format: <code>{message || "Test message"} - 1</code></p>
          <p>Messages start every 1.5 seconds without waiting for confirmations. Failed or uncertain sends are not retried.</p>
        </> : <div role="status" aria-live="polite" aria-atomic="true">
          <p><strong>{running ? scheduling ? "Sending test messages..." : "Waiting for confirmations..."
            : progress.attempted === progress.total ? "Batch finished." : "Batch stopped."}</strong></p>
          <p>{progress.attempted} of {progress.total} attempted. {progress.confirmed} confirmed, {progress.rejected} not sent, {progress.uncertain} pending or uncertain.</p>
          {!running && progress.attempted < progress.total && <p>{progress.total - progress.attempted} messages were not attempted.</p>}
          {!running && progress.uncertain > 0 && <p>Close this popup and use Check original send to review pending or uncertain outcomes. Do not resend them.</p>}
        </div>}
        {error && <p role="alert">{error}</p>}
        {!available && <p role="alert">Sending is unavailable. Check your connection and reload the conversation before starting another batch.</p>}
        <div className="test-messages-actions">
          <button type="button" onClick={() => dialog.current?.close()}>{progress ? "Close" : "Cancel"}</button>
          {scheduling && <button type="button" onClick={() => controller.current?.abort()}>Stop sending</button>}
          {!progress && <button className="primary" type="submit" disabled={!valid || !validMessage || !available}>Send messages</button>}
        </div>
      </form>
    </dialog>
  </>;
}

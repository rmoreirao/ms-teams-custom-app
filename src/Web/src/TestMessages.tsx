import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { Operation } from "./api";

type Progress = { total: number; attempted: number; confirmed: number; rejected: number; uncertain: number;
  initialAccepted: number; initialThrottled: number; graphAttempts: number; retries: number; throttles: number };

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
  const [concurrency, setConcurrency] = useState("1");
  const [message, setMessage] = useState("Test message");
  const [running, setRunning] = useState(false);
  const [scheduling, setScheduling] = useState(false);
  const [progress, setProgress] = useState<Progress | null>(null);
  const [error, setError] = useState("");
  const total = Number(count);
  const valid = Number.isSafeInteger(total) && total > 0;
  const parallel = Number(concurrency);
  const validConcurrency = Number.isSafeInteger(parallel) && parallel > 0 && parallel <= total;
  const messageLimit = 4096 - ` - ${valid ? total : 30}`.length;
  const validMessage = !!message.trim() && message.length <= messageLimit;
  useEffect(() => {
    active.current = true;
    return () => { active.current = false; controller.current?.abort(); };
  }, []);
  useEffect(() => {
    if (!available) { controller.current?.abort(); setScheduling(false); }
  }, [available]);
  const open = () => {
    if (!controller.current) { setCount("30"); setConcurrency("1"); setMessage("Test message"); setProgress(null); setError(""); }
    dialog.current?.showModal();
  };
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (controller.current || !latest.current.available || !valid || !validConcurrency || !validMessage) return;
    const batch = new AbortController();
    controller.current = batch;
    setRunning(true); setScheduling(true); setError("");
    const result: Progress = { total, attempted: 0, confirmed: 0, rejected: 0, uncertain: 0,
      initialAccepted: 0, initialThrottled: 0, graphAttempts: 0, retries: 0, throttles: 0 };
    setProgress({ ...result });
    let next = 1;
    const worker = async () => {
      while (next <= total && !batch.signal.aborted && latest.current.available) {
        const number = next++;
        result.attempted++;
        result.uncertain++;
        setProgress({ ...result });
        if (result.attempted === total) setScheduling(false);
        try {
          const operation = await latest.current.send(`${message} - ${number}`, batch.signal);
          if (operation.state === "confirmed") { result.confirmed++; result.uncertain--; }
          else if (operation.state === "rejected") { result.rejected++; result.uncertain--; }
          result.initialAccepted += operation.firstStatus === 201 ? 1 : 0;
          result.initialThrottled += operation.firstStatus === 429 ? 1 : 0;
          result.graphAttempts += operation.attempts ?? 0;
          result.retries += Math.max(0, (operation.attempts ?? 0) - 1);
          result.throttles += operation.throttles ?? 0;
          if (active.current) setProgress({ ...result });
        } catch (failure) {
          batch.abort();
          if (active.current) setError(failure instanceof Error ? failure.message : "A send could not be started. Check the message statuses before trying again.");
        }
      }
    };
    try {
      await Promise.all(Array.from({ length: parallel }, worker));
    } catch (failure) {
      if (active.current) setError(failure instanceof Error ? failure.message : "The batch stopped unexpectedly. Check the original sends before trying again.");
    } finally {
      if (active.current) setScheduling(false);
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
          <label htmlFor="test-message-concurrency">Concurrent messages</label>
          <input id="test-message-concurrency" type="number" min="1" max={valid ? total : undefined} step="1" required
            value={concurrency} aria-invalid={!validConcurrency} aria-describedby="test-message-concurrency-help"
            onChange={event => setConcurrency(event.target.value)} />
          <p id="test-message-concurrency-help">{validConcurrency
            ? "Starts this many messages immediately, with no pacing or ramp-up."
            : "Enter a positive whole number no larger than the number of messages."}</p>
          <label htmlFor="test-message-text">Test Message</label>
          <input id="test-message-text" type="text" required maxLength={messageLimit} value={message}
            aria-invalid={!validMessage} aria-describedby="test-message-preview test-message-text-error"
            onChange={event => setMessage(event.target.value)} />
          {!validMessage && <p id="test-message-text-error" role="alert">Enter a test message of 1 to {messageLimit} characters, including some non-whitespace text.</p>}
          <p id="test-message-preview">Each message uses the format: <code>{message || "Test message"} - 1</code></p>
          <p>Graph controls throttling. The server retries explicit throttles within its configured retry budget. Uncertain sends are never automatically resent.</p>
        </> : <div role="status" aria-live="polite" aria-atomic="true">
          <p><strong>{running ? scheduling ? "Sending test messages..." : "Waiting for confirmations..."
            : progress.attempted === progress.total ? "Batch finished." : "Batch stopped."}</strong></p>
          <p>{progress.attempted} of {progress.total} attempted. {progress.confirmed} confirmed, {progress.rejected} not sent, {progress.uncertain} pending or uncertain.</p>
          <p>Reported initial Graph responses: {progress.initialAccepted} accepted, {progress.initialThrottled} throttled.
            {" "}{progress.graphAttempts} attempts including {progress.retries} retries; {progress.throttles} throttle responses.</p>
          {running && <p>Graph results are reported as requests finish. Stopping cannot recall messages already dispatched.</p>}
          {!running && progress.attempted < progress.total && <p>{progress.total - progress.attempted} messages were not attempted.</p>}
          {!running && progress.uncertain > 0 && <p>Close this popup and use Check original send to review pending or uncertain outcomes. Do not resend them.</p>}
        </div>}
        {error && <p role="alert">{error}</p>}
        {!available && <p role="alert">Sending is unavailable. Check your connection and reload the conversation before starting another batch.</p>}
        <div className="test-messages-actions">
          <button type="button" onClick={() => dialog.current?.close()}>{progress ? "Close" : "Cancel"}</button>
          {scheduling && <button type="button" onClick={() => controller.current?.abort()}>Stop sending</button>}
          {!progress && <button className="primary" type="submit" disabled={!valid || !validConcurrency || !validMessage || !available}>Send messages</button>}
        </div>
      </form>
    </dialog>
  </>;
}

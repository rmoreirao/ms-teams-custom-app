import { useEffect, useRef, useState } from "react";
import { api, ApiError, isPendingRead, post, readObservation } from "./api";
import type { CacheCleanup, CacheSnapshot, MessagePage, PendingRead, ReadObservation } from "./api";

type Command = { operationId: string; cosmos: boolean; redis: boolean };
type Entry = { label: string; at: string; detail: string; read?: ReadObservation };
const describe = (error: unknown) => error instanceof Error ? error.message : "The diagnostic request failed. Try again.";
const time = (value: string) => new Date(value).toLocaleTimeString();
const cleanupSummary = (value: CacheCleanup) =>
  `Cosmos: ${value.cosmosState}, ${value.removedMessages} messages removed${value.cosmosError ? ` (${value.cosmosError})` : ""}. ` +
  `Redis: ${value.redisState}, ${value.removedPages} page${value.removedPages === 1 ? "" : "s"} removed${value.redisError ? ` (${value.redisError})` : ""}.`;
function ReadDetails({ value }: { value: ReadObservation }) {
  return <div className="cache-read">
    <p>{value.kind === "delta" ? "Delta read" : "Fresh page read"}: {value.outcome}.
      {value.count !== null && ` ${value.count} ${value.kind === "delta" ? "changed" : "returned"} messages.`}
      {value.revision !== null && ` Revision ${value.revision}.`}</p>
    {value.diagnostics ? <>
      {value.diagnostics.steps.length > 0 && <table><caption className="sr-only">Observed dependency work</caption>
        <thead><tr><th>Layer</th><th>Observed work</th><th>Items</th><th>Time</th></tr></thead>
        <tbody>{value.diagnostics.steps.map((step, index) => <tr key={index}>
          <th scope="row">{step.layer === "redis" ? "Redis" : step.layer === "cosmos" ? "Cosmos" : "Hydration"}</th>
          <td>{step.outcome}</td><td>{step.count}</td><td>{step.milliseconds} ms</td>
        </tr>)}</tbody></table>}
      {value.kind === "delta" && <p>Delta reads use Cosmos, not the Redis recent-page cache.</p>}
      <p>This table describes content reads and page-cache work. Authorization and conversation metadata still require Cosmos reads.</p>
      {value.diagnostics.hydrationRequested && <p>Background Teams hydration was accepted for processing, not confirmed complete.</p>}
    </> : <p>Dependency evidence was not provided for this read. Its source cannot be inferred from visible messages.</p>}
    {value.error && <p>{value.error}</p>}
  </div>;
}

export function CacheDiagnostics({ conversationId, title, csrf, online, retainedMessages, latestRead, openChanged, validate, authenticationRequired }: {
  conversationId: string; title: string; csrf: string; online: boolean; retainedMessages: number;
  latestRead: ReadObservation | null; openChanged: (open: boolean) => void;
  validate: (signal: AbortSignal) => Promise<MessagePage | PendingRead>; authenticationRequired: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const controller = useRef<AbortController | null>(null);
  const original = useRef<Command | null>(null);
  const mounted = useRef(true);
  const [snapshot, setSnapshot] = useState<CacheSnapshot | null>(null);
  const [operation, setOperation] = useState<CacheCleanup | null>(null);
  const [cosmos, setCosmos] = useState(false);
  const [redis, setRedis] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [entries, setEntries] = useState<Entry[]>([]);
  const path = `/api/conversations/${encodeURIComponent(conversationId)}/cache-diagnostics`;
  const unresolved = !!original.current && !operation?.complete;
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; controller.current?.abort(); };
  }, []);
  const record = (entry: Entry) => setEntries(previous => [entry, ...previous].slice(0, 12));
  const fail = (failure: unknown) => {
    if (!mounted.current) return;
    setError(describe(failure));
    if (failure instanceof ApiError && failure.status === 401) authenticationRequired();
  };
  const snapshotRead = async (label: string, signal?: AbortSignal) => {
    const value = await api<CacheSnapshot>(path, { signal });
    if (!mounted.current || signal?.aborted) return;
    setSnapshot(value);
    if (value.cleanup && (!original.current || original.current.operationId === value.cleanup.id)) {
      setOperation(value.cleanup);
      if (!value.cleanup.complete) {
        original.current = { operationId: value.cleanup.id, cosmos: value.cleanup.cosmos, redis: value.cleanup.redis };
        setCosmos(value.cleanup.cosmos); setRedis(value.cleanup.redis);
      }
    }
    record({ label, at: value.observedAt, detail: `Cosmos: ${value.cosmosMessages ?? "unavailable"}${value.cosmosCountComplete ? "" : " (incomplete)"} messages. ` +
      `Redis: ${value.redis.pages ?? value.redis.status} page${value.redis.pages === 1 ? "" : "s"}. Revision ${value.revision}, generation ${value.generation}.` });
  };
  const inspect = async () => {
    controller.current?.abort();
    const request = new AbortController(); controller.current = request;
    setBusy("Inspecting storage..."); setError("");
    try { await snapshotRead("Storage inspection", request.signal); }
    catch (failure) { if (!request.signal.aborted) fail(failure); }
    finally { if (mounted.current && !request.signal.aborted) setBusy(""); }
  };
  const close = () => {
    controller.current?.abort(); setConfirming(false);
    setBusy(previous => previous === "Cleaning selected caches..." ? previous : "");
    openChanged(false); dialog.current?.close();
  };
  const clean = async () => {
    const command = original.current && !operation?.complete ? original.current
      : { operationId: crypto.randomUUID(), cosmos, redis };
    original.current = command;
    setOperation(null); setConfirming(false); setBusy("Cleaning selected caches..."); setError("");
    let submitted = false;
    try {
      await snapshotRead("Before cleanup");
      if (!mounted.current) return;
      submitted = true;
      const result = await post<CacheCleanup>(`${path}/cleanup`, command, csrf);
      if (!mounted.current) return;
      setOperation(result);
      record({ label: result.complete ? "Cleanup complete" : "Cleanup needs attention", at: new Date().toISOString(), detail: cleanupSummary(result) });
      await snapshotRead("After cleanup");
    } catch (failure) {
      if (!mounted.current) return;
      if (failure instanceof ApiError && ["cache_cleanup_busy", "cache_operation_expired"].includes(failure.code)) {
        original.current = null; setOperation(null);
        setError(`${failure.message} Inspect storage to find the current operation before confirming another cleanup.`);
        return;
      }
      if (mounted.current) setError(`${submitted
        ? "Cleanup outcome could not be fully confirmed. Check the original operation; do not start a new cleanup."
        : "No cleanup request was sent in this attempt because storage inspection failed. Retry the original operation."} ${describe(failure)}`);
      if (failure instanceof ApiError && failure.status === 401) authenticationRequired();
    } finally { if (mounted.current) setBusy(""); }
  };
  const check = async () => {
    if (!original.current) return;
    setBusy("Checking original operation..."); setError("");
    try {
      const result = await api<CacheCleanup>(`${path}/cleanup/${original.current.operationId}`);
      if (!mounted.current) return;
      setOperation(result);
      record({ label: "Original operation status", at: new Date().toISOString(), detail: cleanupSummary(result) });
      await snapshotRead("Operation status inspection");
    } catch (failure) {
      if (!mounted.current) return;
      if (failure instanceof ApiError && failure.code === "cache_operation_expired") {
        original.current = null; setOperation(null);
      }
      fail(failure);
    }
    finally { if (mounted.current) setBusy(""); }
  };
  const reload = async () => {
    controller.current?.abort();
    const request = new AbortController(); controller.current = request;
    setBusy("Reloading and validating..."); setError("");
    const started = Date.now();
    try {
      for (let attempt = 0; attempt < 9; attempt++) {
        const value = await validate(request.signal);
        if (!mounted.current || request.signal.aborted) return;
        record({ label: "Validation read", at: new Date().toISOString(), detail: isPendingRead(value)
          ? "History is pending background hydration. No empty-chat conclusion is drawn."
          : `${value.messages.length} messages returned at revision ${value.revision}.`,
          read: readObservation(value, "page") });
        if (!isPendingRead(value)) { await snapshotRead("After validation", request.signal); return; }
        const delay = Math.max(value.retryAfterMs, Math.min(30000, 1000 * 2 ** attempt));
        if (Date.now() + delay - started >= 120000 || attempt === 8) {
          setError("Hydration is still pending. Use Reload and validate again to observe its progress.");
          return;
        }
        await new Promise<void>((resolve, reject) => {
          const abort = () => { clearTimeout(timer); reject(new DOMException("Read cancelled.", "AbortError")); };
          const timer = setTimeout(() => { request.signal.removeEventListener("abort", abort); resolve(); }, delay);
          request.signal.addEventListener("abort", abort, { once: true });
        });
      }
    } catch (failure) {
      if (request.signal.aborted) return;
      if (mounted.current) record({ label: "Validation failed", at: new Date().toISOString(), detail: describe(failure),
        read: { kind: "page", observedAt: new Date().toISOString(), outcome: "failed", count: null, revision: null,
          diagnostics: failure instanceof ApiError ? failure.diagnostics : undefined, error: describe(failure) } });
      fail(failure);
    } finally { if (mounted.current && !request.signal.aborted) setBusy(""); }
  };
  return <>
    <button type="button" className="cache-diagnostics-button" onClick={() => {
      openChanged(true); setConfirming(false); dialog.current?.showModal(); void inspect();
    }}>Cache Diagnostics</button>
    <dialog ref={dialog} className="cache-diagnostics-dialog" aria-labelledby="cache-diagnostics-title"
      aria-describedby="cache-diagnostics-description" onClose={() => {
        controller.current?.abort(); openChanged(false);
      }}>
      <div className="cache-dialog-heading"><h2 id="cache-diagnostics-title">Cache Diagnostics</h2>
        <button type="button" onClick={close}>Close</button></div>
      <p id="cache-diagnostics-description"><strong>{title}</strong> - shared app copies for this chat only.
        Microsoft Teams remains the source of record. Original Teams messages are never deleted here.</p>
      <section aria-labelledby="cache-layers-title">
        <h3 id="cache-layers-title">Understand the layers</h3>
        <dl className="cache-layers"><dt>Browser memory</dt><dd>{retainedMessages} loaded messages in this tab.
          Visible history can be retained without a fresh page read.</dd>
          <dt>Redis</dt><dd>Recent pages, up to 15 seconds. A miss falls back to Cosmos.</dd>
          <dt>Cosmos</dt><dd>Shared message projection with seven-day retention. Cold reads request bounded background Teams hydration.</dd></dl>
        <p>Automatic content reads in this tab are paused while this popup is open. Authorization still expires normally.
          Other tabs, users, sends, and workers can refill storage.</p>
        <details><summary>Last observed network read{latestRead ? ` at ${time(latestRead.observedAt)}` : ""}</summary>
          {latestRead ? <ReadDetails value={latestRead} /> : <p>No network-read evidence has been observed yet.</p>}</details>
      </section>
      <section aria-labelledby="cache-storage-title"><h3 id="cache-storage-title">Storage observation</h3>
        {snapshot ? <>
          <p>Observed at {time(snapshot.observedAt)}. Revision {snapshot.revision}; content generation {snapshot.generation}.
            Counts are sampled, not a globally isolated snapshot.</p>
          <table><caption className="sr-only">Selected chat cache snapshot</caption>
            <thead><tr><th>Store</th><th>Observed copies</th><th>State</th></tr></thead><tbody>
              <tr><th scope="row">Cosmos</th><td>{snapshot.cosmosMessages === null ? "Unavailable" :
                `${snapshot.cosmosCountComplete ? "" : "At least "}${snapshot.cosmosMessages} messages`}</td>
                <td>{snapshot.cosmosError ?? (snapshot.coverageComplete || snapshot.readViewComplete ? "Known complete" : "Hydration may be needed")}</td></tr>
              <tr><th scope="row">Redis</th><td>{snapshot.redis.pages === null ? "Unavailable" :
                `${snapshot.redis.complete ? "" : "At least "}${snapshot.redis.pages} page${snapshot.redis.pages === 1 ? "" : "s"}`}</td>
                <td>{snapshot.redis.status}{snapshot.redis.maximumTtlSeconds !== null && `; longest remaining TTL ${snapshot.redis.maximumTtlSeconds.toFixed(1)} s`}</td></tr>
            </tbody></table>
          <p>Counts cover shared chat copies, not just the messages in your current history window.
            Redis pages may belong to older, ineligible revisions.</p>
        </> : <p>No storage snapshot yet. Inspection does not warm caches or request Teams hydration.</p>}
        <div className="cache-actions"><button type="button" disabled={!!busy || !online} onClick={() => void inspect()}>Inspect storage</button>
          <button type="button" disabled={!!busy || !online} onClick={() => void reload()}>Reload and validate</button></div>
      </section>
      <section aria-labelledby="cache-cleanup-title"><h3 id="cache-cleanup-title">Clean shared app copies</h3>
        <p>Cleanup affects everyone using this chat in the app. Chat access, drafts, and original send-status records are preserved.
          Synchronization can repopulate the copies afterward.</p>
        <fieldset disabled={!!busy || unresolved}><legend>Select stores to clean</legend>
          <label><input type="checkbox" checked={cosmos} onChange={event => { setCosmos(event.target.checked); setConfirming(false); }} />
            Cosmos message copies</label>
          <label><input type="checkbox" checked={redis} onChange={event => { setRedis(event.target.checked); setConfirming(false); }} />
            Redis recent pages</label></fieldset>
        <p>Cosmos cleanup invalidates old page generations but does not physically delete Redis keys unless Redis is selected.
          Old opaque keys from the previous app version expire naturally within their existing 15-second TTL.</p>
        {confirming ? <div className="cache-confirmation">
          <p>Remove shared {cosmos && redis ? "Cosmos message copies and Redis recent pages" : cosmos ? "Cosmos message copies" : "Redis recent pages"} for {title}?</p>
          <div className="cache-actions"><button type="button" onClick={() => setConfirming(false)}>Cancel cleanup</button>
            <button type="button" className="cache-destructive" disabled={!!busy || !online} onClick={() => void clean()}>Confirm cleanup</button></div>
        </div> : <button type="button" disabled={!!busy || !online || unresolved || !cosmos && !redis}
          onClick={() => setConfirming(true)}>Clean selected caches</button>}
        {operation && <p role="status">{cleanupSummary(operation)}</p>}
        {original.current && <p className="cache-operation">Original operation: <code>{original.current.operationId}</code></p>}
        {unresolved && <div className="cache-actions">
          <button type="button" disabled={!!busy || !online} onClick={() => void check()}>Check original operation</button>
          <button type="button" disabled={!!busy || !online} onClick={() => void clean()}>Resume original cleanup</button>
        </div>}
      </section>
      {busy && <p role="status" aria-live="polite">{busy} Closing this popup does not undo a cleanup already requested.</p>}
      {error && <p role="alert">{error}</p>}
      {!online && <p role="alert">Diagnostics requires a connection. Reconnect before inspecting or cleaning shared storage.</p>}
      {entries.length > 0 && <section aria-labelledby="cache-history-title"><h3 id="cache-history-title">Observation history</h3>
        <p>Latest 12 observations, kept only in this page session. A refill is an observation, not proof of which worker caused it.</p>
        <ol className="cache-history">{entries.map((entry, index) => <li key={`${entry.at}-${index}`}>
          <strong>{entry.label}</strong> <time dateTime={entry.at}>{time(entry.at)}</time>
          <p>{entry.detail}</p>{entry.read && <ReadDetails value={entry.read} />}
        </li>)}</ol></section>}
    </dialog>
  </>;
}

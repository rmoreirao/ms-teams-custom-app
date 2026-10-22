import { useEffect, useRef, useState } from "react";
import { api, ApiError, isPendingRead, readMessages } from "./api";
import type { ChangePage, Message, MessagePage, PendingRead, ReadAccess } from "./api";

function mergeMessages(previous: Message[], incoming: Message[], parent: string | null, cutoff?: string) {
  const oldest = previous[0] ? Date.parse(previous[0].createdAt) : null;
  const items = new Map(previous.map(message => [message.id, message]));
  for (const message of incoming)
    if (message.parentId === parent && (items.has(message.id) || oldest === null || Date.parse(message.createdAt) >= oldest))
      items.set(message.id, message);
  return [...items.values()].filter(message => !cutoff || Date.parse(message.createdAt) >= Date.parse(cutoff))
    .sort(compareMessages);
}
const compareMessages = (a: Message, b: Message) =>
  Date.parse(a.createdAt) - Date.parse(b.createdAt) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
const accessFailure = (error: unknown) => error instanceof ApiError &&
  ([401, 403, 404].includes(error.status) ||
    ["authorization_stale", "primary_read_unavailable", "primary_read_invalid", "resource_mapping_changed",
      "writer_fenced", "access_denied", "admission_disabled"].includes(error.code));
const isPreparing = (error: unknown) => error instanceof TypeError ||
  error instanceof ApiError && [429, 503].includes(error.status);
const preparationRetryDelays = [1000, 2000, 4000, 8000, 15000, 30000, 30000, 30000];
const recoveryLimit = 120_000;
const validAccess = (access?: ReadAccess): access is ReadAccess => !!access &&
  Number.isSafeInteger(access.epoch) && access.epoch >= 0 && Number.isFinite(Date.parse(access.historyFrom)) &&
  Number.isFinite(Date.parse(access.validUntil)) && Date.parse(access.validUntil) > Date.now();
function restrict(value: MessagePage, access: ReadAccess): MessagePage {
  if (!validAccess(access) || value.access && value.access.epoch !== access.epoch)
    throw new ApiError(503, "authorization_stale", "Current access must be verified. Try again.");
  const cutoff = Math.max(Date.parse(access.historyFrom), Date.now() - 7 * 86400_000);
  if (value.parentMessage && (value.parentMessage.deleted || Date.parse(value.parentMessage.createdAt) < cutoff))
    throw new ApiError(404, "access_denied", "This thread is no longer available.");
  return { ...value, access, messages: value.messages.filter(message => Date.parse(message.createdAt) >= cutoff) };
}
const behind = (access?: ReadAccess) =>
  new ApiError(503, "projection_behind", "The announced update is still being synchronized. Try again.", access);

export function useMessageFeed(input: {
  path: string; changesPath: string; parent: string | null; refresh: number; minimumRevision: number;
  unavailable: () => void; expired: () => void; syncParent: (message: Message) => void;
}) {
  const callbacks = useRef(input);
  callbacks.current = input;
  const [page, setPage] = useState<MessagePage | null>(null);
  const [loading, setLoading] = useState(true);
  const [syncing, setSyncing] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState("");
  const [preparing, setPreparing] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const current = useRef<MessagePage | null>(null);
  const pages = useRef(1);
  const generation = useRef(0);
  const context = useRef(input.path);
  const target = useRef(0);
  const recovery = useRef({ started: 0, retries: 0, next: 0, exhausted: false });
  const requestSync = useRef<(() => void) | null>(null);
  const olderController = useRef<AbortController | null>(null);
  const renewedDeadline = useRef("");
  const commit = (value: MessagePage) => {
    target.current = Math.max(target.current, value.revision);
    current.current = value; setPage(value);
  };
  const fail = (error: unknown) => {
    if (error instanceof ApiError && error.access && !validAccess(error.access))
      error = new ApiError(503, "authorization_stale", "Current access must be verified. Try again.");
    let retained = false;
    if (!accessFailure(error) && current.current && validAccess(current.current.access)) {
      try {
        const proof = error instanceof ApiError && error.access ? error.access : current.current.access;
        commit(restrict(current.current, proof)); retained = true;
      }
      catch (restriction) { error = restriction; }
    }
    if (!retained) { current.current = null; setPage(null); }
    setError(retained ? "" : error instanceof Error ? error.message : "History could not be loaded. Try again.");
    setPreparing(isPreparing(error));
    if (error instanceof ApiError && [401, 403, 404].includes(error.status)) callbacks.current.unavailable();
    if (error instanceof ApiError && error.status === 401) callbacks.current.expired();
    if (!retained && callbacks.current.parent && isPreparing(error)) callbacks.current.unavailable();
  };
  useEffect(() => {
    if (context.current !== input.path) {
      context.current = input.path; current.current = null; setPage(null); pages.current = 1;
      target.current = 0; recovery.current = { started: 0, retries: 0, next: 0, exhausted: false }; renewedDeadline.current = "";
      setLoading(true);
    }
    target.current = Math.max(target.current, input.minimumRevision, current.current?.revision ?? 0);
    const controller = new AbortController();
    let active = true;
    let running = false;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    const version = ++generation.current;
    olderController.current?.abort();
    setLoadingMore(false);
    setSyncing(true);
    const read = async (): Promise<MessagePage | PendingRead> => {
      const previous = current.current;
      const minimum = target.current;
      if (previous) {
        const parameters = new URLSearchParams({ after: String(previous.revision), minimumRevision: String(minimum) });
        if (input.parent) parameters.set("parent", input.parent);
        const changes = await api<ChangePage>(`${input.changesPath}?${parameters}`, { signal: controller.signal });
        if (changes.revision < minimum) throw behind(changes.access);
        if (!changes.resetRequired && changes.messages) {
          let parentMessage = previous.parentMessage;
          if (input.parent) {
            parentMessage = changes.messages.find(message => message.id === input.parent && !message.parentId) ?? parentMessage;
            if (parentMessage && (parentMessage.deleted || changes.historyFrom &&
                Date.parse(parentMessage.createdAt) < Date.parse(changes.historyFrom)))
              throw new ApiError(404, "access_denied", "This thread is no longer available.");
          }
          const value = { ...previous, revision: changes.revision, window: changes.window ?? previous.window, parentMessage,
            messages: mergeMessages(previous.messages, changes.messages, input.parent, changes.historyFrom) };
          return changes.access ? restrict(value, changes.access) : value;
        }
      }
      let latest: MessagePage | null = null;
      let cursor: string | null = null;
      const messages: Message[] = [];
      for (let index = 0; index < pages.current; index++) {
        const parameters = new URLSearchParams(input.path.split("?")[1]);
        if (cursor) parameters.set("cursor", cursor);
        if (minimum) parameters.set("minimumRevision", String(minimum));
        const value = await readMessages(`${input.path.split("?")[0]}${parameters.size ? `?${parameters}` : ""}`,
          controller.signal);
        if (isPendingRead(value)) return value;
        if (value.revision < Math.max(minimum, latest?.revision ?? 0)) throw behind(value.access);
        if (!latest) latest = value;
        if (value.access) latest = restrict(latest, value.access);
        messages.push(...value.messages);
        cursor = value.cursor;
        if (!cursor) break;
      }
      if (!latest) throw new Error("Recent context could not be loaded.");
      const value = { ...latest, cursor, messages: [...new Map(messages.map(message => [message.id, message])).values()]
        .sort(compareMessages) };
      return value.access ? restrict(value, value.access) : value;
    };
    const synchronize = () => {
      if (!active || generation.current !== version || running) return;
      clearTimeout(retryTimer);
      if (recovery.current.exhausted) { setSyncing(false); return; }
      if (recovery.current.next > Date.now()) {
        retryTimer = setTimeout(synchronize, recovery.current.next - Date.now());
        return;
      }
      running = true;
      setSyncing(true);
      let waiting = false;
      const schedule = (retryAfterMs = 0, jitter = 1) => {
        const budget = recovery.current;
        if (!budget.started) budget.started = Date.now();
        const remaining = recoveryLimit - (Date.now() - budget.started);
        if (budget.retries < preparationRetryDelays.length && remaining > 0) {
          const delay = Math.min(remaining, Math.max(retryAfterMs, preparationRetryDelays[budget.retries++] * jitter));
          budget.next = Date.now() + delay;
          retryTimer = setTimeout(synchronize, delay);
          return true;
        }
        budget.exhausted = true;
        return false;
      };
      void read().then(value => {
        if (!active || generation.current !== version) return;
        if (isPendingRead(value)) {
          if (current.current && value.access) commit(restrict(current.current, value.access));
          const scheduled = schedule(value.retryAfterMs);
          waiting = !current.current && scheduled;
          setPreparing(scheduled);
          setError(scheduled || current.current ? "" : "History is not available yet. Try again.");
          return;
        }
        if (value.revision < target.current) throw behind(value.access);
        commit(value); setError(""); setPreparing(false);
        recovery.current = { started: 0, retries: 0, next: 0, exhausted: false };
        if (value.parentMessage) callbacks.current.syncParent(value.parentMessage);
      }).catch(error => {
        if (!active || generation.current !== version) return;
        fail(error);
        if (isPreparing(error)) schedule(0, error instanceof ApiError && error.code === "projection_behind"
          ? 0.9 + Math.random() * 0.1 : 1);
      }).finally(() => {
        running = false;
        if (active && generation.current === version) { setLoading(waiting); setSyncing(false); }
      });
    };
    requestSync.current = synchronize;
    synchronize();
    return () => { active = false; requestSync.current = null; clearTimeout(retryTimer); controller.abort(); };
  }, [input.path, input.changesPath, input.parent, input.refresh, attempt]);
  useEffect(() => {
    target.current = Math.max(target.current, input.minimumRevision);
    if (!current.current || target.current > current.current.revision) requestSync.current?.();
  }, [input.minimumRevision]);
  useEffect(() => {
    if (!page?.access) return;
    const expires = Math.min(Date.parse(page.access.validUntil),
      ...[...page.messages, ...(page.parentMessage ? [page.parentMessage] : [])]
        .map(message => Date.parse(message.createdAt) + 7 * 86400_000 + 1));
    const timer = setTimeout(() => {
      if (current.current?.access?.validUntil !== page.access?.validUntil) return;
      const loaded = current.current;
      if (loaded && validAccess(loaded.access)) {
        try { commit(restrict(loaded, loaded.access)); }
        catch (error) { fail(error); }
        return;
      }
      current.current = null; setPage(null);
      setPreparing(true); setError("Current access must be verified. Try again.");
      if (callbacks.current.parent) callbacks.current.unavailable();
    }, Math.max(0, expires - Date.now()));
    return () => clearTimeout(timer);
  }, [page]);
  useEffect(() => {
    const proof = page?.access;
    if (!proof || renewedDeadline.current === proof.validUntil) return;
    const remaining = Date.parse(proof.validUntil) - Date.now();
    const timer = setTimeout(() => {
      renewedDeadline.current = proof.validUntil;
      recovery.current = { started: 0, retries: 0, next: 0, exhausted: false };
      requestSync.current?.();
    }, Math.max(0, remaining - Math.min(30_000, Math.max(1000, remaining / 4))));
    return () => clearTimeout(timer);
  }, [page?.access?.validUntil, input.path]);
  useEffect(() => () => { olderController.current?.abort(); }, []);
  const older = async () => {
    const previous = current.current;
    if (!previous?.cursor || loadingMore || syncing || olderController.current) return;
    const controller = new AbortController();
    olderController.current = controller;
    const version = generation.current;
    setLoadingMore(true);
    try {
      const started = Date.now();
      let value: MessagePage | PendingRead;
      let retries = 0;
      do {
        value = await readMessages(`${input.path}${input.path.includes("?") ? "&" : "?"}cursor=${encodeURIComponent(previous.cursor)}&minimumRevision=${Math.max(previous.revision, target.current)}`,
          controller.signal);
        if (!isPendingRead(value)) break;
        if (current.current && value.access) commit(restrict(current.current, value.access));
        const remaining = recoveryLimit - (Date.now() - started);
        if (retries >= preparationRetryDelays.length || remaining <= 0)
          throw new ApiError(503, "read_refresh_unavailable", "Earlier history is not available yet. Try again.");
        const delay = Math.min(remaining, Math.max(value.retryAfterMs, preparationRetryDelays[retries++]));
        await new Promise<void>((resolve, reject) => {
          const abort = () => { clearTimeout(timer); reject(new DOMException("Read cancelled.", "AbortError")); };
          const timer = setTimeout(() => {
            controller.signal.removeEventListener("abort", abort); resolve();
          }, delay);
          controller.signal.addEventListener("abort", abort, { once: true });
        });
      } while (!controller.signal.aborted);
      if (isPendingRead(value)) return;
      if (controller.signal.aborted || version !== generation.current) return;
      if (value.revision < Math.max(previous.revision, target.current)) throw behind(value.access);
      pages.current++;
      const merged = { ...previous, cursor: value.cursor,
        messages: [...new Map([...value.messages, ...previous.messages].map(message => [message.id, message])).values()]
          .sort(compareMessages) };
      commit(value.access ? restrict(merged, value.access) : merged);
      setError("");
    } catch (error) {
      if (!controller.signal.aborted && version === generation.current) fail(error);
    } finally {
      if (olderController.current === controller) olderController.current = null;
      if (version === generation.current) setLoadingMore(false);
    }
  };
  return { page, loading, syncing, loadingMore, error, preparing, older, retry: () => {
    recovery.current = { started: 0, retries: 0, next: 0, exhausted: false };
    setLoading(!current.current); setAttempt(n => n + 1);
  } };
}

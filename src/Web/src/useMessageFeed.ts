import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "./api";
import type { ChangePage, Message, MessagePage } from "./api";

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
const isPreparing = (error: unknown) => error instanceof ApiError && error.status === 503 &&
  ["projection_preparing", "authorization_stale", "resource_preparing"].includes(error.code);
const preparationRetryDelays = [1000, 2000, 4000, 8000, 15000, 30000, 30000, 30000];

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
  const olderController = useRef<AbortController | null>(null);
  const commit = (value: MessagePage) => { current.current = value; setPage(value); };
  const fail = (error: unknown) => {
    current.current = null; setPage(null);
    setError(error instanceof Error ? error.message : "Recent context could not be synchronized. Try again.");
    setPreparing(isPreparing(error));
    if (error instanceof ApiError && [401, 403, 404].includes(error.status)) callbacks.current.unavailable();
    if (error instanceof ApiError && error.status === 401) callbacks.current.expired();
  };
  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    let retries = 0;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    const version = ++generation.current;
    olderController.current?.abort();
    setLoadingMore(false);
    setSyncing(true);
    const read = async () => {
      const previous = current.current;
      if (previous) {
        const changes = await api<ChangePage>(`${input.changesPath}?after=${previous.revision}`, { signal: controller.signal });
        if (changes.revision < input.minimumRevision)
          throw new ApiError(503, "projection_preparing", "The announced update is still being synchronized. Try again.");
        if (!changes.resetRequired && changes.messages) {
          let parentMessage = previous.parentMessage;
          if (input.parent) {
            parentMessage = changes.messages.find(message => message.id === input.parent && !message.parentId) ?? parentMessage;
            if (parentMessage && (parentMessage.deleted || changes.historyFrom &&
                Date.parse(parentMessage.createdAt) < Date.parse(changes.historyFrom)))
              throw new ApiError(404, "access_denied", "This thread is no longer available.");
          }
          return { ...previous, revision: changes.revision, window: changes.window ?? previous.window, parentMessage,
            messages: mergeMessages(previous.messages, changes.messages, input.parent, changes.historyFrom) };
        }
      }
      let latest: MessagePage | null = null;
      let cursor: string | null = null;
      const messages: Message[] = [];
      for (let index = 0; index < pages.current; index++) {
        const parameters = new URLSearchParams(input.path.split("?")[1]);
        if (cursor) parameters.set("cursor", cursor);
        if (input.minimumRevision) parameters.set("minimumRevision", String(input.minimumRevision));
        const value = await api<MessagePage>(`${input.path.split("?")[0]}${parameters.size ? `?${parameters}` : ""}`,
          { signal: controller.signal });
        if (!latest) latest = value;
        messages.push(...value.messages);
        cursor = value.cursor;
        if (!cursor) break;
      }
      if (!latest) throw new Error("Recent context could not be loaded.");
      return { ...latest, cursor, messages: [...new Map(messages.map(message => [message.id, message])).values()]
        .sort(compareMessages) };
    };
    const synchronize = () => {
      if (!active || generation.current !== version) return;
      setSyncing(true);
      void read().then(value => {
        if (!active || generation.current !== version) return;
        commit(value); setError(""); setPreparing(false);
        if (value.parentMessage) callbacks.current.syncParent(value.parentMessage);
      }).catch(error => {
        if (!active || generation.current !== version) return;
        fail(error);
        if (isPreparing(error) && retries < preparationRetryDelays.length)
          retryTimer = setTimeout(synchronize, preparationRetryDelays[retries++]);
      }).finally(() => { if (active && generation.current === version) { setLoading(false); setSyncing(false); } });
    };
    synchronize();
    return () => { active = false; clearTimeout(retryTimer); controller.abort(); };
  }, [input.path, input.changesPath, input.parent, input.refresh, input.minimumRevision, attempt]);
  useEffect(() => () => { olderController.current?.abort(); }, []);
  const older = async () => {
    const previous = current.current;
    if (!previous?.cursor || loadingMore || syncing || olderController.current) return;
    const controller = new AbortController();
    olderController.current = controller;
    const version = generation.current;
    setLoadingMore(true);
    try {
      const value = await api<MessagePage>(`${input.path}${input.path.includes("?") ? "&" : "?"}cursor=${encodeURIComponent(previous.cursor)}`,
        { signal: controller.signal });
      if (controller.signal.aborted || version !== generation.current) return;
      pages.current++;
      commit({ ...previous, cursor: value.cursor,
        messages: [...new Map([...value.messages, ...previous.messages].map(message => [message.id, message])).values()]
          .sort(compareMessages) });
      setError("");
    } catch (error) {
      if (!controller.signal.aborted && version === generation.current) fail(error);
    } finally {
      if (olderController.current === controller) olderController.current = null;
      if (version === generation.current) setLoadingMore(false);
    }
  };
  return { page, loading, syncing, loadingMore, error, preparing, older, retry: () => {
    setLoading(!current.current); setAttempt(n => n + 1);
  } };
}

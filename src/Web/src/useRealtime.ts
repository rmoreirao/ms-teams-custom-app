import { useEffect, useRef, useState } from "react";
import { WebPubSubClient } from "@azure/web-pubsub-client";
import { api, ApiError, post } from "./api";
import type { RealtimeEvent, RealtimeHint, Session } from "./api";

export function parseHint(data: unknown): RealtimeHint {
  const value: unknown = typeof data === "string" ? JSON.parse(data) : data;
  if (!value || typeof value !== "object" || !("conversationId" in value) || !("revision" in value) ||
      typeof value.conversationId !== "string" || !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(value.conversationId) ||
      typeof value.revision !== "number" || !Number.isSafeInteger(value.revision) || value.revision < 0)
    throw new Error("The live service returned an invalid update. Reconnect to synchronize your conversations.");
  return { conversationId: value.conversationId, revision: value.revision };
}

export function parseEvent(data: unknown): RealtimeEvent | RealtimeHint {
  const value: unknown = typeof data === "string" ? JSON.parse(data) : data;
  if (!value || typeof value !== "object" || !("type" in value)) return parseHint(value);
  const type = value.type;
  if (!("schemaVersion" in value) || value.schemaVersion !== 1 ||
      type !== "channel.operation.changed" && type !== "channel.catalog.changed" && type !== "channel.view.ready" ||
      !("revision" in value) || typeof value.revision !== "number" || !Number.isSafeInteger(value.revision) || value.revision < 0 ||
      !("sequence" in value) || typeof value.sequence !== "number" || !Number.isSafeInteger(value.sequence) || value.sequence < 0)
    throw new Error("The live service returned an unsupported channel event. Reconnect to synchronize.");
  const field = (key: string): unknown => Reflect.get(value, key);
  const optionalId = (key: string) => {
    const item = field(key);
    if (item == null) return null;
    if (typeof item !== "string" || item.length > 512)
      throw new Error("The live service returned an invalid channel context. Reconnect to synchronize.");
    return item;
  };
  const operationId = optionalId("operationId");
  const conversationId = optionalId("conversationId");
  const parentId = optionalId("parentId");
  const generation = field("generation");
  if (type === "channel.operation.changed" && (!operationId ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(operationId)) ||
      type !== "channel.operation.changed" &&
      (!conversationId || !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(conversationId)) ||
      generation != null && (typeof generation !== "number" || !Number.isSafeInteger(generation) || generation < 0))
    throw new Error("The live service returned an invalid channel context. Reconnect to synchronize.");
  return { type, schemaVersion: 1, revision: value.revision, sequence: value.sequence,
    operationId, conversationId, parentId, generation: typeof generation === "number" ? generation : null };
}

export function useRealtime(session: Session | null, handlers: {
  hints: (items: RealtimeHint[]) => void; reconcile: () => void; expired: () => void; error: (message: string) => void;
  events?: (items: RealtimeEvent[]) => void;
}) {
  const callbacks = useRef(handlers);
  callbacks.current = handlers;
  const [status, setStatus] = useState("Connecting");
  const [online, setOnline] = useState(navigator.onLine);
  const [attempt, setAttempt] = useState(0);
  const watermark = useRef(0);
  const principal = useRef<string | null>(null);
  useEffect(() => {
    if (!session) return;
    if (principal.current !== session.id) { principal.current = session.id; watermark.current = 0; }
    let stopped = false;
    let close: (() => void) | undefined;
    let batch: number | undefined;
    let opened = attempt > 0;
    let checking = false;
    let catchingUp = false;
    let catchUpRequested = false;
    const channelEvents = new Map<number, RealtimeEvent>();
    const pending = new Map<string, number>();
    const controller = new AbortController();
    const reconcile = () => { if (!stopped && navigator.onLine && !document.hidden) callbacks.current.reconcile(); };
    const checkSession = async () => {
      if (checking || !navigator.onLine || stopped) return;
      checking = true;
      try { await api<Session>("/api/session", { signal: controller.signal }); }
      catch (error) {
        if (!stopped) {
          if (error instanceof ApiError && error.status === 401) callbacks.current.expired();
          else callbacks.current.error(error instanceof Error ? error.message : "The session could not be verified. Reconnect to try again.");
        }
      } finally { checking = false; }
    };
    const catchUp = async () => {
      if (!session.channelRegistry || stopped) return;
      catchUpRequested = true;
      if (catchingUp) return;
      catchingUp = true;
      try {
        while (catchUpRequested && !stopped) {
          catchUpRequested = false;
          let more = true;
          for (let page = 0; page < 100 && more && !stopped; page++) {
            const result = await api<{ events: RealtimeEvent[]; sequence: number; resetRequired: boolean; more: boolean }>(
              `/api/realtime/changes?after=${watermark.current}`, { signal: controller.signal });
            if (!Number.isSafeInteger(result.sequence) || result.sequence < 0 || !Array.isArray(result.events))
              throw new Error("Channel event recovery returned invalid evidence.");
            if (result.resetRequired) {
              watermark.current = result.sequence;
              channelEvents.clear();
              reconcile();
              more = false;
              break;
            }
            const items: RealtimeEvent[] = [];
            for (const raw of result.events) {
              const event = parseEvent(raw);
              if (!("type" in event) || event.sequence !== watermark.current + 1)
                throw new Error("Channel event recovery did not cover the expected sequence.");
              watermark.current = event.sequence;
              channelEvents.delete(event.sequence);
              items.push(event);
            }
            if (items.length && !stopped) callbacks.current.events?.(items);
            more = result.more;
            if (more && items.length === 0) throw new Error("Channel event recovery made no progress.");
          }
          if (more) throw new Error("Channel event recovery exceeded its page budget. Reconnect to resume.");
        }
      } catch (error) {
        if (!stopped) {
          setStatus("Synchronization unavailable");
          if (error instanceof ApiError && error.status === 401) callbacks.current.expired();
          else callbacks.current.error(error instanceof Error ? error.message : "Channel event recovery failed. Reconnect to resume.");
        }
      } finally { catchingUp = false; }
    };
    const connected = () => {
      if (stopped) return;
      setStatus(navigator.onLine ? "Live" : "Offline");
      if (opened) reconcile();
      opened = true;
      void catchUp();
    };
    const hint = (data: unknown) => {
      if (stopped) return;
      try {
        const item = parseEvent(data);
        if ("type" in item) {
          if (!item.sequence) { callbacks.current.events?.([item]); return; }
          if (item.sequence <= watermark.current) return;
          channelEvents.set(item.sequence, item);
          if (catchingUp) { catchUpRequested = true; return; }
          const contiguous: RealtimeEvent[] = [];
          while (channelEvents.has(watermark.current + 1)) {
            const event = channelEvents.get(++watermark.current)!;
            contiguous.push(event); channelEvents.delete(watermark.current);
          }
          if (contiguous.length) callbacks.current.events?.(contiguous);
          if (channelEvents.size) void catchUp();
          return;
        }
        pending.set(item.conversationId, Math.max(pending.get(item.conversationId) ?? 0, item.revision));
        if (batch === undefined) batch = window.setTimeout(() => {
          batch = undefined;
          callbacks.current.hints([...pending].map(([conversationId, revision]) => ({ conversationId, revision })));
          pending.clear();
        }, 100);
      } catch (error) {
        setStatus("Synchronization unavailable");
        callbacks.current.error(error instanceof Error ? error.message : "The live update could not be read. Reconnect to try again.");
      }
    };
    const disconnected = () => {
      if (stopped) return;
      setStatus(navigator.onLine ? "Reconnecting" : "Offline");
      void checkSession();
    };
    const unavailable = () => {
      if (stopped) return;
      setStatus(navigator.onLine ? "Synchronization unavailable" : "Offline");
      if (navigator.onLine) callbacks.current.error("Live updates stopped reconnecting. Select Reconnect to try again.");
      void checkSession();
    };
    const start = async () => {
      const negotiate = () => post<{ transport: string; url?: string }>(
        "/api/realtime/negotiate", {}, session.csrf, controller.signal);
      try {
        const client = new WebPubSubClient({ getClientAccessUrl: async () => {
          const next = await negotiate();
          controller.signal.throwIfAborted();
          if (next.transport !== "webpubsub") throw new Error("The service returned an unsupported live transport.");
          if (!next.url) throw new Error("The live service did not provide a connection address.");
          return next.url;
        } });
        client.on("connected", connected);
        client.on("server-message", event => hint(event.message.data));
        client.on("disconnected", disconnected);
        client.on("stopped", unavailable);
        close = () => { void client.stop(); };
        await client.start({ abortSignal: controller.signal });
        if (stopped) { client.stop(); return; }
      } catch (error) {
        if (stopped) return;
        if (error instanceof ApiError && error.status === 401) callbacks.current.expired();
        else {
          setStatus(navigator.onLine ? "Synchronization unavailable" : "Offline");
          callbacks.current.error(error instanceof Error ? error.message : "Live updates could not connect. Reconnect to try again.");
        }
      }
    };
    const onOnline = () => { setOnline(true); setAttempt(n => n + 1); };
    const onOffline = () => { setOnline(false); setStatus("Offline"); };
    const onVisible = () => { if (!document.hidden) { reconcile(); void catchUp(); void checkSession(); } };
    setStatus(navigator.onLine ? "Connecting" : "Offline");
    setOnline(navigator.onLine);
    if (navigator.onLine) void start();
    window.addEventListener("online", onOnline);
    window.addEventListener("offline", onOffline);
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      stopped = true; controller.abort(); close?.(); clearTimeout(batch);
      window.removeEventListener("online", onOnline); window.removeEventListener("offline", onOffline);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [session, attempt]);
  return { status, online, reconnect: () => setAttempt(n => n + 1) };
}

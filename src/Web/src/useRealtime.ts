import { useEffect, useRef, useState } from "react";
import { WebPubSubClient } from "@azure/web-pubsub-client";
import { api, ApiError, post } from "./api";
import type { RealtimeHint, Session } from "./api";

export function parseHint(data: unknown): RealtimeHint {
  const value: unknown = typeof data === "string" ? JSON.parse(data) : data;
  if (!value || typeof value !== "object" || !("conversationId" in value) || !("revision" in value) ||
      typeof value.conversationId !== "string" || !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(value.conversationId) ||
      typeof value.revision !== "number" || !Number.isSafeInteger(value.revision) || value.revision < 0)
    throw new Error("The live service returned an invalid update. Reconnect to synchronize your conversations.");
  return { conversationId: value.conversationId, revision: value.revision };
}

export function useRealtime(session: Session | null, handlers: {
  hints: (items: RealtimeHint[]) => void; reconcile: () => void; expired: () => void; error: (message: string) => void;
}) {
  const callbacks = useRef(handlers);
  callbacks.current = handlers;
  const [status, setStatus] = useState("Connecting");
  const [online, setOnline] = useState(navigator.onLine);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!session) return;
    let stopped = false;
    let close: (() => void) | undefined;
    let timer: number | undefined;
    let batch: number | undefined;
    let opened = false;
    let checking = false;
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
    const connected = () => {
      if (stopped) return;
      setStatus("Live");
      if (opened) reconcile();
      opened = true;
    };
    const hint = (data: unknown) => {
      if (stopped) return;
      try {
        const item = parseHint(data);
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
    const start = async () => {
      const negotiate = () => post<{ transport: string; url?: string }>(
        "/api/realtime/negotiate", {}, session.csrf, controller.signal);
      try {
        const endpoint = await negotiate();
        if (stopped) return;
        if (endpoint.transport !== "webpubsub") throw new Error("The service returned an unsupported live transport.");
        if (!endpoint.url) throw new Error("The live service did not provide a connection address.");
        const client = new WebPubSubClient({ getClientAccessUrl: async () => {
          const next = await negotiate();
          if (next.transport !== "webpubsub" || !next.url) throw new Error("The live transport changed. Reconnect to try again.");
          return next.url;
        } });
        client.on("connected", connected);
        client.on("server-message", event => hint(event.message.data));
        client.on("disconnected", disconnected);
        client.on("stopped", disconnected);
        close = () => { void client.stop(); };
        await client.start();
        if (stopped) { await client.stop(); return; }
        // Reconcile missed hints without synchronizing every browser on the same timer.
        timer = window.setInterval(reconcile, 240_000 + Math.floor(Math.random() * 120_000));
      } catch (error) {
        if (stopped) return;
        if (error instanceof ApiError && error.status === 401) callbacks.current.expired();
        else {
          setStatus(navigator.onLine ? "Synchronization unavailable" : "Offline");
          callbacks.current.error(error instanceof Error ? error.message : "Live updates could not connect. Reconnect to try again.");
        }
      }
    };
    const onOnline = () => { setOnline(true); setAttempt(n => n + 1); reconcile(); };
    const onOffline = () => { setOnline(false); setStatus("Offline"); };
    const onVisible = () => { if (!document.hidden) { reconcile(); void checkSession(); } };
    setStatus(navigator.onLine ? "Connecting" : "Offline");
    setOnline(navigator.onLine);
    void start();
    window.addEventListener("online", onOnline);
    window.addEventListener("offline", onOffline);
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      stopped = true; controller.abort(); close?.(); clearInterval(timer); clearTimeout(batch);
      window.removeEventListener("online", onOnline); window.removeEventListener("offline", onOffline);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [session, attempt]);
  return { status, online, reconnect: () => setAttempt(n => n + 1) };
}

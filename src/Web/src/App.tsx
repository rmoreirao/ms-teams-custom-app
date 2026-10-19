import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { FormEvent, KeyboardEvent } from "react";
import { WebPubSubClient } from "@azure/web-pubsub-client";
import { api, ApiError, conversationPage, post } from "./api";
import type { Session, Conversation, Message, MessagePage, Operation } from "./api";

type Pending = { operation: Operation; body: string; parentId: string | null; conversationId: string };
const time = (value: string) => new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const initials = (name: string) => name.split(" ").map(n => n[0]).slice(0, 2).join("");
const problem = (error: unknown) => error instanceof Error ? error.message : "The operation failed. Try again.";

function Icon({ name }: { name: "plus" | "search" | "close" | "chat" | "send" }) {
  const paths = {
    plus: "M12 5v14M5 12h14",
    search: "m16 16 4 4M17 10.5a6.5 6.5 0 1 1-13 0 6.5 6.5 0 0 1 13 0",
    close: "m6 6 12 12M6 18 18 6",
    chat: "M5 4h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9l-6 4V6a2 2 0 0 1 2-2Z",
    send: "m21 3-7 18-4-7-7-4 18-7ZM10 14l5-5",
  };
  return <svg viewBox="0 0 24 24" aria-hidden="true"><path d={paths[name]} /></svg>;
}

export function Delivery({ item, onCheck }: { item: Pending; onCheck: () => void }) {
  const uncertain = item.operation.state === "unknown" || item.operation.state === "dispatching";
  return <div className="delivery" role="status">
    <span>{item.operation.state === "confirmed" ? item.operation.simulated ? "Confirmed by simulator - not Teams" : "Confirmed by Teams"
      : item.operation.state === "rejected" ? "Not sent"
      : uncertain ? "Outcome unknown - do not resend" : "Sending..."}</span>
    {item.operation.error && <span>{item.operation.error}</span>}
    {(uncertain || item.operation.state === "pending") && <button onClick={onCheck}>Check original send</button>}
  </div>;
}

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [booting, setBooting] = useState(true);
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [catalogCursor, setCatalogCursor] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [catalogLoading, setCatalogLoading] = useState(true);
  const [catalogError, setCatalogError] = useState("");
  const catalogPages = useRef(1);
  const catalogGeneration = useRef(0);
  const [selected, setSelected] = useState<Conversation | null>(null);
  const [parent, setParent] = useState<Message | null>(null);
  const [filter, setFilter] = useState("all");
  const [search, setSearch] = useState("");
  const [error, setError] = useState("");
  const [connected, setConnected] = useState("Connecting");
  const [refresh, setRefresh] = useState(0);
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [pending, setPending] = useState<Pending[]>([]);
  const [controls, setControls] = useState(false);
  const [discovering, setDiscovering] = useState(true);
  const [chatSearch, setChatSearch] = useState("");
  const [focusComposer, setFocusComposer] = useState(false);
  const newChatRef = useRef<HTMLButtonElement>(null);
  const discoveryOrigin = useRef("new-chat-button");
  const conversationTitle = useRef<HTMLHeadingElement>(null);
  const clearWorkspace = useCallback(() => {
    setSelected(null); setParent(null); setConversations([]); setPending([]); setDrafts({}); setControls(false);
    setCatalogCursor(null); setLoadingMore(false); catalogPages.current = 1; catalogGeneration.current++;
    setCatalogLoading(true); setCatalogError(""); setDiscovering(true); setChatSearch(""); setSearch(""); setFilter("all");
  }, []);
  const expireSession = useCallback(() => {
    clearWorkspace(); setSession(null); setError("Your session no longer has access. Sign in again.");
  }, [clearWorkspace]);
  const refreshRef = useRef(() => {});
  refreshRef.current = () => setRefresh(n => n + 1);

  const reloadSession = useCallback(async () => {
    try { setSession(await api<Session>("/api/session")); }
    catch (e) {
      if (!(e instanceof ApiError && e.status === 401)) setError(problem(e));
      clearWorkspace();
      setSession(null);
    } finally { setBooting(false); }
  }, [clearWorkspace]);
  useEffect(() => { void reloadSession(); }, [reloadSession]);
  useEffect(() => {
    if (!session) return;
    let active = true;
    const controller = new AbortController();
    const generation = ++catalogGeneration.current;
    setLoadingMore(false);
    setCatalogLoading(true);
    const load = async () => {
      const items: Conversation[] = [];
      let cursor: string | null = null;
      for (let index = 0; index < catalogPages.current; index++) {
        const page = await conversationPage(cursor, controller.signal);
        items.push(...page.items);
        cursor = page.cursor;
        if (!cursor) break;
      }
      return { items, cursor };
    };
    load().then(({ items, cursor }) => {
      if (!active) return;
      setConversations(items);
      setCatalogCursor(cursor);
      setCatalogError("");
      setSelected(previous => previous && (items.find(c => c.id === previous.id) || (cursor ? previous : null)));
    }).catch(e => {
      if (!active) return;
      setCatalogError(problem(e));
      if (e instanceof ApiError && e.status === 401) expireSession();
    }).finally(() => { if (active) setCatalogLoading(false); });
    return () => { active = false; controller.abort(); if (catalogGeneration.current === generation) catalogGeneration.current++; };
  }, [session, refresh]);
  const loadMoreConversations = async () => {
    if (!catalogCursor || loadingMore) return;
    const generation = catalogGeneration.current;
    setLoadingMore(true);
    try {
      const page = await conversationPage(catalogCursor);
      if (generation !== catalogGeneration.current) return;
      setConversations(previous => [...previous, ...page.items].filter((item, index, all) =>
        all.findIndex(other => other.id === item.id) === index));
      setCatalogCursor(page.cursor); catalogPages.current++;
    } catch (e) {
      if (generation === catalogGeneration.current) {
        setCatalogError(problem(e));
        if (e instanceof ApiError && e.status === 401) expireSession();
      }
    } finally { if (generation === catalogGeneration.current) setLoadingMore(false); }
  };
  useEffect(() => {
    if (!session) return;
    let stopped = false;
    let close: (() => void) | undefined;
    const refreshConnection = () => { if (!stopped) { setConnected("Live"); refreshRef.current(); } };
    const start = async () => {
      const negotiate = () => post<{ transport: string; url: string }>("/api/realtime/negotiate", {}, session.csrf);
      try {
        const endpoint = await negotiate();
        if (stopped) return;
        if (endpoint.transport === "poll") {
          setConnected("Updates every 30s");
          return;
        } else if (endpoint.transport === "sse") {
          const source = new EventSource(endpoint.url);
          source.onopen = refreshConnection;
          source.onmessage = () => refreshRef.current();
          source.onerror = () => {
            if (stopped) return;
            setConnected(navigator.onLine ? "Reconnecting" : "Offline");
            void api<Session>("/api/session").catch(e => { if (!stopped && e instanceof ApiError && e.status === 401) expireSession(); });
          };
          close = () => source.close();
        } else {
          const client = new WebPubSubClient({ getClientAccessUrl: async () => (await negotiate()).url });
          client.on("connected", refreshConnection);
          client.on("server-message", () => refreshRef.current());
          client.on("disconnected", () => {
            if (stopped) return;
            setConnected("Reconnecting");
            void api<Session>("/api/session").catch(e => { if (!stopped && e instanceof ApiError && e.status === 401) expireSession(); });
          });
          close = () => client.stop();
          await client.start();
          if (stopped) client.stop();
        }
      } catch (e) { if (!stopped) { setConnected("Unavailable"); setError(problem(e)); } }
    };
    void start();
    const online = () => { setConnected("Reconnecting"); refreshRef.current(); };
    const offline = () => setConnected("Offline");
    window.addEventListener("online", online);
    window.addEventListener("offline", offline);
    const catchup = window.setInterval(() => refreshRef.current(), 30_000);
    return () => { stopped = true; close?.(); clearInterval(catchup); window.removeEventListener("online", online); window.removeEventListener("offline", offline); };
  }, [session, expireSession]);

  const choose = (conversation: Conversation, compose = false) => {
    setSelected(conversation); setParent(null); setError(""); setDiscovering(false); setFocusComposer(compose);
    if (compose) { setSearch(""); setFilter("all"); }
  };
  const openDiscovery = (origin: HTMLButtonElement) => {
    discoveryOrigin.current = origin.id;
    setDiscovering(true); setParent(null); setControls(false);
  };
  const closeDiscovery = () => {
    setDiscovering(false);
    requestAnimationFrame(() => {
      const origin = document.getElementById(discoveryOrigin.current) ?? newChatRef.current;
      if (origin?.getClientRects().length) origin.focus();
      else conversationTitle.current?.focus();
    });
  };
  const closeThread = () => {
    const id = parent?.id;
    setParent(null);
    requestAnimationFrame(() => {
      if (id) document.querySelector<HTMLButtonElement>(`[data-message-id="${CSS.escape(id)}"] .reply-button`)?.focus();
    });
  };
  useEffect(() => {
    const escape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== "Escape") return;
      if (controls) setControls(false);
      else if (discovering) closeDiscovery();
      else if (parent) closeThread();
    };
    window.addEventListener("keydown", escape);
    return () => window.removeEventListener("keydown", escape);
  }, [controls, discovering, parent]);
  const scenario = async (action: string, next = false) => {
    if (!session || !selected && !next) return;
    try {
      await post(next ? "/api/simulator/next" : `/api/simulator/conversations/${selected!.id}`, { action }, session.csrf);
      setError(next ? `The next send will use the "${action}" simulator outcome.` : "");
      setRefresh(n => n + 1);
    } catch (e) { setError(problem(e)); }
  };
  const check = async (item: Pending) => {
    try {
      const operation = await api<Operation>(`/api/conversations/${item.conversationId}/operations/${item.operation.id}`);
      setPending(items => items.map(p => p.operation.id === operation.id ? { ...p, operation } : p));
      setRefresh(n => n + 1);
    } catch (e) {
      setError(problem(e));
      if (e instanceof ApiError && e.status === 401) expireSession();
    }
  };
  const logout = async () => {
    if (!session) return;
    try {
      await post("/auth/signout", {}, session.csrf);
      clearWorkspace(); setSession(null);
    } catch (e) { setError(problem(e)); }
  };
  const theme = () => {
    const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    const url = new URL(location.href); url.searchParams.set("clawpilotTheme", next); history.replaceState(null, "", url);
  };
  if (booting) return <main className="login"><p role="status">Opening your workspace...</p></main>;
  if (!session) return <Login onSignedIn={reloadSession} error={error} />;
  const visible = conversations.filter(c => (filter === "all" || c.kind === filter) &&
    `${c.title} ${c.team ?? ""}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const feedProps = {
    session, refresh, drafts, setDrafts, pending, setPending, check,
    changed: () => setRefresh(n => n + 1), online: connected !== "Offline",
    unavailable: () => setParent(null),
    syncParent: (message: Message) => setParent(message),
    authenticationRequired: expireSession,
  };
  return <div className={`shell ${selected || discovering ? "conversation-open" : ""} ${parent ? "thread-open" : ""}`}>
    <a href="#conversation-main" className="skip-link">Skip to conversation</a>
    <header className="app-header"><span className="brand">Conversations</span><span className="environment">{session.basicValidation ?
      "Basic Teams validation" : session.simulated ? "Dev workspace" : "Teams workspace"}</span>
      <div className="header-tools"><span className="connection" role="status">{connected}</span>
        <button onClick={theme} aria-label="Toggle light and dark theme">Theme</button><button onClick={logout}>Sign out</button></div></header>
    <div className="workspace">
      <nav className="navigation" aria-label="Conversations">
        <div className="nav-top"><div className="nav-heading"><h1>Messages</h1>
          <button id="new-chat-button" ref={newChatRef} className="primary reach-action" aria-expanded={discovering}
            aria-controls={discovering ? "chat-discovery" : undefined} onClick={e => openDiscovery(e.currentTarget)}>
            <Icon name="plus" />New chat</button></div>
          <label className="sr-only" htmlFor="search">Search conversations</label>
          <div className="search-wrap"><Icon name="search" /><input id="search" type="search" autoComplete="off"
            placeholder="Find a conversation" value={search} onChange={e => setSearch(e.target.value)} /></div>
          <div className="filter-row" role="group" aria-label="Conversation type">{[["all", "All"], ["chat", "Chats"], ["channel", "Channels"]].map(([value, label]) =>
            <button key={value} aria-pressed={filter === value} onClick={() => setFilter(value)}>{label}</button>)}</div>
        </div>
        <div className="nav-list"><h2 className="nav-section-title">Recent conversations</h2>
          {catalogLoading && !conversations.length && <p className="nav-state" role="status">Loading conversations...</p>}
          {catalogError && <div className="nav-state"><p role="alert">{catalogError}</p>
            <button onClick={() => setRefresh(n => n + 1)}>Reload conversations</button></div>}
          {visible.map(c => <button className="conversation-row" key={c.id}
          aria-current={selected?.id === c.id ? "true" : undefined} onClick={() => choose(c)}>
          <span className={`avatar ${c.kind === "channel" ? "channel" : ""}`} aria-hidden>{c.kind === "channel" ? "#" : initials(c.title)}</span>
          <span className="row-copy"><strong>{c.title}</strong><span>{c.team ?? (c.id === "chat-project" ? "Group chat" : "Chat")}</span></span>
        </button>)}
          {catalogCursor && <button className="older" disabled={loadingMore} onClick={() => void loadMoreConversations()}>
            {loadingMore ? "Loading conversations..." : "Load more conversations"}</button>}
          {!catalogLoading && !catalogError && !visible.length && <div className="state"><p>{search || filter !== "all" ? "No conversations match your search and filters." : "No conversations are available for this account."}</p>
            {(search || filter !== "all") && <button onClick={() => { setSearch(""); setFilter("all"); }}>Clear search</button>}</div>}
        </div>
        <div className="nav-bottom"><span className="avatar self" aria-hidden>{initials(session.name)}</span><span>{session.name}<small>{session.localAuth ? "Local test identity" : "Entra signed in"}</small></span></div>
      </nav>
      <main id="conversation-main" tabIndex={-1} className={`conversation ${discovering ? "discovery" : ""}`}>
        {error && <div className="notice" role="status">{error}<button onClick={() => setError("")} aria-label="Dismiss notice">Dismiss</button></div>}
        {discovering ? <ChatDiscovery conversations={conversations} query={chatSearch} setQuery={setChatSearch}
          loading={catalogLoading} error={catalogError} more={!!catalogCursor} loadingMore={loadingMore}
          loadMore={() => void loadMoreConversations()} retry={() => setRefresh(n => n + 1)}
          session={session} choose={c => choose(c, true)} close={closeDiscovery} /> : selected ? <>
          <header className="conversation-header"><button className="back" onClick={() => { setSelected(null); setParent(null); }}>Back</button>
            <span className={`avatar ${selected.kind === "channel" ? "channel" : ""}`} aria-hidden>{selected.kind === "channel" ? "#" : initials(selected.title)}</span>
            <div className="conversation-heading"><h1 ref={conversationTitle} tabIndex={-1}>{selected.title}</h1>
              <p>{selected.team ?? "Chat"} · {session.simulated ? "Synthetic content" : "Teams content"}</p></div>
            {selected.kind === "chat" && <button id="change-chat-button" className="recipient-action" onClick={e => openDiscovery(e.currentTarget)}>Change chat</button>}</header>
          <Feed key={selected.id} {...feedProps} conversation={selected} parent={null} onThread={setParent} focusComposer={focusComposer} />
        </> : <div className="state welcome"><h1>A little context goes a long way.</h1><p>Choose a conversation to read recent messages and keep the discussion moving.</p>
          <button id="welcome-new-chat" className="primary" onClick={e => openDiscovery(e.currentTarget)}><Icon name="plus" />New chat</button>
          <p className="muted">{session.simulated ? "This dev workspace uses generated data. It is not connected to Teams."
            : session.basicValidation ? "This pilot reads the dedicated Teams test resources. Updates refresh every 30 seconds."
            : "Recent Teams messages are synchronized into this workspace."}</p></div>}
      </main>
      {parent && selected && <aside className="thread-panel" aria-label="Channel thread">
        <header className="conversation-header"><div><h2>Thread</h2><p>{selected.title}</p></div>
          <button onClick={closeThread} aria-label="Close thread">Close</button></header>
        <div className="thread-parent"><strong>{parent.author}</strong><p>{parent.body}</p></div>
        <Feed key={`${selected.id}:${parent.id}`} {...feedProps} conversation={selected} parent={parent} onThread={() => {}} />
      </aside>}
    </div>
    <footer className="app-footer"><span>{session.basicValidation ? "Direct Teams reads · Dedicated test resources only · Updates every 30 seconds" :
      session.simulated ? "Synthetic test workspace · Not connected to Teams."
      : "Teams is the source of record. Updates may take time to synchronize."}</span>
      {session.simulated && session.scenarioAdmin && <button onClick={() => setControls(!controls)} aria-expanded={controls}>Test scenarios</button>}</footer>
    {controls && <section className="test-controls" aria-label="Simulator test controls"><h2>Test scenarios</h2><p>These affect only generated data.</p>
      <label htmlFor="next-outcome">Next send outcome</label><select id="next-outcome" defaultValue="confirm" onChange={e => void scenario(e.target.value, true)}>
        <option value="confirm">Confirm normally</option><option value="reject">Reject</option><option value="unknown">Lose confirmation</option><option value="throttle">Throttle</option><option value="delay">Delay</option></select>
      {["external", "edit", "delete", "revoke", "restore"].map(action => <button disabled={!selected} key={action} onClick={() => void scenario(action)}>{{
        external: "Receive external update", edit: "Edit a message", delete: "Delete a message", revoke: "Revoke Sam's access", restore: "Restore Sam's access",
      }[action]}</button>)}
      <button onClick={() => setControls(false)}>Close controls</button>
    </section>}
  </div>;
}

function ChatDiscovery({ conversations, query, setQuery, loading, error, more, loadingMore, loadMore, retry, session, choose, close }: {
  conversations: Conversation[]; query: string; setQuery: (value: string) => void; loading: boolean; error: string;
  more: boolean; loadingMore: boolean; loadMore: () => void; retry: () => void;
  session: Session; choose: (conversation: Conversation) => void; close: () => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => { input.current?.focus(); }, []);
  const search = query.trim().toLocaleLowerCase();
  const chats = conversations.filter(c => c.kind === "chat" && c.title.toLocaleLowerCase().includes(search));
  const waiting = loading && !conversations.length;
  return <section id="chat-discovery" aria-label="Find a chat">
    <header className="discovery-header">
      <div className="discovery-title"><h1>New chat</h1>
        <button className="icon-button" onClick={close} aria-label="Close chat search"><Icon name="close" /></button></div>
      <p>Who would you like to reach? Choose an available chat to start writing.</p>
      <div className="people-search"><label htmlFor="chat-search">To</label>
        <input ref={input} id="chat-search" type="search" autoComplete="off" placeholder="Type a name or chat title"
          value={query} onChange={e => setQuery(e.target.value)} aria-describedby="chat-search-hint chat-scope" aria-controls="chat-results" />
        <p className="search-hint" id="chat-search-hint">Search by the name shown in your conversation list. Nothing is sent when you select a chat.</p>
      </div>
    </header>
    <div className="people-results" id="chat-results" aria-busy={loading || loadingMore}>
      <div className="results-label"><h2>{search ? "Search results" : "Available chats"}</h2>
        <p role="status">{waiting ? "Loading chats..." : error ? "Chat search unavailable" :
          `${chats.length} ${more ? "loaded " : ""}${chats.length === 1 ? "chat" : "chats"}`}</p></div>
      <p className="capability-note" id="chat-scope">{session.basicValidation ?
        "This Teams pilot is limited to the dedicated test chat. New one-to-one chats and directory lookup are not enabled." :
        "Only existing chats you can access are shown. Creating a chat and looking up people by email are not available in this workspace."}</p>
      {error ? <div className="state" role="alert"><h2>Chats are unavailable</h2><p>{error}</p>
        <button onClick={retry} disabled={loading}>Try chat search again</button></div> :
        !waiting && <>
          {chats.map(chat => <button className="person-row" key={chat.id} aria-label={`Open chat: ${chat.title}`} onClick={() => choose(chat)}>
            <span className="avatar" aria-hidden>{initials(chat.title)}</span>
            <span className="person-copy"><strong>{chat.title}</strong><span>{session.simulated ? "Synthetic chat" : "Teams chat"}</span>
              <span className="person-detail">{session.basicValidation ? "Dedicated validation resource" : "Available to your signed-in account"}</span></span>
            <span className="person-affordance"><span>Existing chat</span><Icon name="chat" /></span>
          </button>)}
          {!chats.length && <div className="state"><Icon name="search" /><h2>{search ? "No matching chats" : "No chats available"}</h2>
            <p>{search ? "Try a different name or chat title." : "There are no chats in the conversations loaded for this account."}
              {more ? " More conversations are available to load." : " You can still open any available channel from the conversation list."}</p>
            {search && <button onClick={() => { setQuery(""); input.current?.focus(); }}>Clear chat search</button>}</div>}
          {more && <button className="load-chats" disabled={loadingMore || loading} onClick={loadMore}>
            {loadingMore ? "Loading more conversations..." : "Load more available chats"}</button>}
        </>}
      <p className="directory-note">Check the chat title before writing. A chat may include multiple people; this is not a people directory.
        {more && " Search covers loaded conversations only."}</p>
    </div>
  </section>;
}

type FeedProps = {
  session: Session; conversation: Conversation; parent: Message | null; refresh: number;
  drafts: Record<string, string>; setDrafts: React.Dispatch<React.SetStateAction<Record<string, string>>>;
  pending: Pending[]; setPending: React.Dispatch<React.SetStateAction<Pending[]>>;
  check: (item: Pending) => Promise<void>; changed: () => void; online: boolean; onThread: (message: Message) => void;
  unavailable: () => void;
  syncParent: (message: Message) => void;
  authenticationRequired: () => void;
  focusComposer?: boolean;
};
function Feed(props: FeedProps) {
  const { conversation, parent, session, refresh, drafts, setDrafts, pending, setPending, check, changed, online, onThread } = props;
  const [page, setPage] = useState<MessagePage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [sending, setSending] = useState(false);
  const [retry, setRetry] = useState(0);
  const busy = useRef(false);
  const titleRef = useRef<HTMLHeadingElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const initialScroll = useRef(true);
  const atBottom = useRef(true);
  const prepend = useRef<{ height: number; top: number } | null>(null);
  const key = `${conversation.id}:${parent?.id ?? ""}`;
  const current = pending.filter(p => p.conversationId === conversation.id && p.parentId === (parent?.id ?? null));
  const unresolved = current.some(p => ["pending", "dispatching", "unknown"].includes(p.operation.state));
  const path = `/api/conversations/${conversation.id}/messages${parent ? `?parent=${encodeURIComponent(parent.id)}` : ""}`;
  useEffect(() => {
    if (props.focusComposer) composerRef.current?.focus();
    else titleRef.current?.focus();
  }, []);
  useEffect(() => {
    let active = true;
    api<MessagePage>(path).then(value => { if (active) { setPage(value); setError(""); if (parent && value.parentMessage) props.syncParent(value.parentMessage); } })
      .catch(e => {
        if (active) {
          setPage(null); setError(problem(e)); props.unavailable();
          if (e instanceof ApiError && e.status === 401) props.authenticationRequired();
        }
      }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [path, refresh, retry]);
  useLayoutEffect(() => {
    const element = scrollRef.current;
    if (!element || !page) return;
    if (prepend.current) {
      element.scrollTop = prepend.current.top + element.scrollHeight - prepend.current.height;
      prepend.current = null;
    } else if ((initialScroll.current && (conversation.kind === "chat" || parent)) || (!initialScroll.current && atBottom.current)) {
      element.scrollTop = element.scrollHeight;
    }
    initialScroll.current = false;
  }, [page, pending, conversation.kind, parent]);
  const older = async () => {
    if (!page?.cursor) return;
    try {
      const value = await api<MessagePage>(`${path}${parent ? "&" : "?"}cursor=${encodeURIComponent(page.cursor)}`);
      if (scrollRef.current) prepend.current = { height: scrollRef.current.scrollHeight, top: scrollRef.current.scrollTop };
      setPage({ ...value, messages: [...value.messages, ...page.messages].filter((m, i, all) => all.findIndex(other => other.id === m.id) === i) });
    } catch (e) { setError(problem(e)); }
  };
  const send = async (event?: FormEvent) => {
    event?.preventDefault();
    if (busy.current || !page || !drafts[key]?.trim() || !online || unresolved) return;
    busy.current = true; setSending(true); setError("");
    const id = crypto.randomUUID();
    const body = drafts[key];
    const item: Pending = { conversationId: conversation.id, parentId: parent?.id ?? null, body,
      operation: { id, state: "pending", messageId: null, error: null, simulated: session.simulated } };
    setPending(items => [...items, item]);
    setDrafts(items => ({ ...items, [key]: "" }));
    try {
      const response = await fetch(path.split("?")[0], { method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": session.csrf },
        body: JSON.stringify({ operationId: id, window: page.window, body, parentId: parent?.id ?? null }) });
      let result: unknown;
      try { result = await response.json(); }
      catch {
        if (response.status >= 400 && response.status < 500) {
          setPending(items => items.map(p => p.operation.id === id ? { ...p, operation: {
            ...p.operation, state: "rejected", error: "The gateway rejected this request. Review the message and sign-in state before a new attempt.",
          } } : p));
          return;
        }
        throw new Error("The send response could not be read. Check the original operation; do not resend.");
      }
      if ([201, 202, 422].includes(response.status)) {
        const operation = result as Operation;
        setPending(items => items.map(p => p.operation.id === id ? { ...p, operation } : p));
      } else {
        const failure = result as { code: string; message: string };
        // A server-side pre-dispatch denial is definitive; generic 5xx is not.
        const state = response.status < 500 ? "rejected" : "unknown";
        setPending(items => items.map(p => p.operation.id === id ? { ...p, operation: { ...p.operation, state, error: failure.message } } : p));
      }
      changed();
    } catch (e) {
      setPending(items => items.map(p => p.operation.id === id ? { ...p, operation: { ...p.operation, state: "unknown", error: problem(e) } } : p));
    } finally { busy.current = false; setSending(false); }
  };
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send(); }
  };
  return <>
    <div className="messages" aria-busy={loading} ref={scrollRef} onScroll={event => {
      const element = event.currentTarget;
      atBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < 96;
    }}>
      <h2 ref={titleRef} tabIndex={-1} className="sr-only">{parent ? "Thread replies" : "Recent messages"}</h2>
      {error && <div className="state" role="alert"><h2>Content is unavailable</h2><p>{error}</p><button onClick={() => { setLoading(true); setRetry(n => n + 1); }}>Try again</button></div>}
      {loading && <p role="status">Loading recent context...</p>}
      {!loading && !error && page?.cursor && <button className="older" onClick={() => void older()}>Load earlier messages</button>}
      {!loading && !error && !page?.messages.length && <div className="state"><h2>{parent ? "Start the thread" : "No messages yet"}</h2><p>{parent ? "Add a reply to keep this discussion together."
        : session.simulated ? "Send the first message in this test conversation." : "No messages are available in the recent history you can access."}</p></div>}
      {!error && page?.messages.map(message => <article className={`message ${message.actorId === session.id ? "own" : ""}`} key={message.id} data-message-id={message.id}>
        <span className="avatar" aria-hidden>{initials(message.author)}</span><div className="message-content">
          <div className="message-meta"><strong>{message.author}</strong><time dateTime={message.createdAt}>{time(message.createdAt)}</time>
            {message.modifiedAt !== message.createdAt && !message.deleted && <span>Edited</span>}</div>
          <p className="message-text">{message.deleted ? "This message was deleted." : message.body}</p>
          {!parent && conversation.kind === "channel" && !message.deleted && <button className="reply-button" onClick={() => onThread(message)}>Open thread</button>}
        </div></article>)}
      {current.filter(item => item.operation.state !== "confirmed" || !page?.messages.some(m => m.id === item.operation.messageId)).map(item =>
        <article className="message own" key={item.operation.id}><span className="avatar self" aria-hidden>{initials(session.name)}</span>
          <div className="message-content"><strong>{session.name}</strong><p className="message-text">{item.body}</p><Delivery item={item} onCheck={() => void check(item)} />
            {item.operation.state === "rejected" && <button onClick={() => {
              setDrafts(items => ({ ...items, [key]: item.body }));
              setPending(items => items.filter(p => p.operation.id !== item.operation.id));
            }}>Edit before a new attempt</button>}
          </div></article>)}
    </div>
    <div className="composer-wrap">
      {!online && <p className="composer-warning" role="status">Offline. Sending is disabled; drafts stay in this page only.</p>}
      {unresolved && <p className="composer-warning" role="status">Resolve the original send before sending again.</p>}
      <form className="composer" onSubmit={e => void send(e)}>
        <label className="sr-only" htmlFor={`composer-${key}`}>{parent ? "Write a reply" : "Write a message"}</label>
        <textarea ref={composerRef} id={`composer-${key}`} placeholder={parent ? "Reply in this thread..." : "Write a message..."} maxLength={4096}
          aria-describedby={`composer-note-${key}`}
          value={drafts[key] ?? ""} onChange={e => setDrafts(items => ({ ...items, [key]: e.target.value }))} onKeyDown={onKey} />
        <button className="primary" type="submit" disabled={sending || loading || !!error || !page || !online || unresolved || !drafts[key]?.trim()}>
          <Icon name="send" />{parent ? "Reply" : "Send"}</button>
      </form><p className="composer-note" id={`composer-note-${key}`}>Enter to send · Shift+Enter for a new line · {session.simulated ?
        "Simulator confirmation is not Teams delivery" : "Sent only after Teams confirms creation"}</p>
    </div>
  </>;
}

function Login({ onSignedIn, error }: { onSignedIn: () => Promise<void>; error: string }) {
  const [local, setLocal] = useState(false);
  const [localAvailable, setLocalAvailable] = useState(false);
  const [simulated, setSimulated] = useState(false);
  const [password, setPassword] = useState("");
  const [actorId, setActorId] = useState("alex");
  const [message, setMessage] = useState(error);
  useEffect(() => {
    let active = true;
    api<{ localAuth: boolean; simulated: boolean }>("/auth/config")
      .then(config => { if (active) { setLocalAvailable(config.localAuth); setSimulated(config.simulated); } })
      .catch(error => { if (active) setMessage(problem(error)); });
    return () => { active = false; };
  }, []);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    try {
      const csrf = await api<{ token: string }>("/auth/csrf");
      await post("/auth/local", { actorId, password }, csrf.token);
      setPassword(""); await onSignedIn();
    } catch (e) { setMessage(problem(e)); }
  };
  return <main className="login"><div><span className="brand">Conversations</span><h1>Keep the discussion moving.</h1>
    <p>A focused workspace for chats, channel updates, and the context that matters.</p>
    <p className="notice">{simulated ? "Dev environment · Generated content only · Not connected to Teams" :
      "Microsoft sign-in · Access limited to authorized conversations"}</p>
    {message && <p role="alert">{message}</p>}
    <a className="primary sign-in" href="/auth/signin">Sign in with Microsoft</a>
    {localAvailable && <button className="local-toggle" onClick={() => setLocal(!local)} aria-expanded={local}>Local development sign-in</button>}
    {local && <form onSubmit={e => void submit(e)}><label htmlFor="actor">Test persona</label><select id="actor" value={actorId} onChange={e => setActorId(e.target.value)}>
      <option value="alex">Alex Morgan</option><option value="sam">Sam Rivera</option><option value="jordan">Jordan Lee (restricted / late joiner)</option>
    </select><label htmlFor="password">Development password</label><input id="password" autoComplete="current-password" type="password" required value={password} onChange={e => setPassword(e.target.value)} />
      <button className="primary">Open local workspace</button><p>Available only on a locally configured development host. There is no default password.</p></form>}
  </div></main>;
}

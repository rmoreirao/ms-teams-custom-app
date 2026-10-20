import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { FormEvent, KeyboardEvent, ReactNode } from "react";
import { api, ApiError, conversationPage, post } from "./api";
import { TestMessages } from "./TestMessages";
import { PeopleDiscovery } from "./PeopleDiscovery";
import { useRealtime } from "./useRealtime";
import { useMessageFeed } from "./useMessageFeed";
import { MessageList } from "./MessageList";
import { MessageStatus } from "./MessageStatus";
import { useSendQueue } from "./useSendQueue";
import type { Session, Conversation, Message, Operation, Person } from "./api";

type Pending = {
  operation: Operation; body: string; parentId: string | null; conversationId: string;
  createdAt: string; responded: boolean;
};
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
  return <div className="delivery">
    <MessageStatus state={item.operation.state} />
    {(item.operation.error || uncertain || item.responded && item.operation.state === "pending") && <div className="delivery-details">
      {item.operation.state === "unknown" && <span>Outcome unknown - do not resend</span>}
      {item.operation.error && <span>{item.operation.error}</span>}
      {(uncertain || item.responded && item.operation.state === "pending") && <button onClick={onCheck}>Check original send</button>}
    </div>}
  </div>;
}

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [booting, setBooting] = useState(true);
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const conversationsRef = useRef(conversations);
  conversationsRef.current = conversations;
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
  const [refresh, setRefresh] = useState(0);
  const [updates, setUpdates] = useState<Record<string, { revision: number; refresh: number }>>({});
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [pending, setPending] = useState<Pending[]>([]);
  const [discovering, setDiscovering] = useState(true);
  const [chatSearch, setChatSearch] = useState("");
  const [recipients, setRecipients] = useState<Record<string, Person>>({});
  const [focusComposer, setFocusComposer] = useState(false);
  const newChatRef = useRef<HTMLButtonElement>(null);
  const discoveryOrigin = useRef("new-chat-button");
  const conversationTitle = useRef<HTMLHeadingElement>(null);
  const clearWorkspace = useCallback(() => {
    setSelected(null); setParent(null); setConversations([]); setPending([]); setDrafts({});
    setCatalogCursor(null); setLoadingMore(false); catalogPages.current = 1; catalogGeneration.current++;
    setCatalogLoading(true); setCatalogError(""); setDiscovering(true); setChatSearch(""); setSearch(""); setFilter("all"); setRecipients({});
    setUpdates({});
  }, []);
  const expireSession = useCallback(() => {
    clearWorkspace(); setSession(null); setError("Your session no longer has access. Sign in again.");
  }, [clearWorkspace]);
  const refreshRef = useRef(() => {});
  refreshRef.current = () => setRefresh(n => n + 1);
  const { status: connected, online, reconnect } = useRealtime(session, {
    reconcile: () => refreshRef.current(),
    expired: expireSession,
    error: setError,
    hints: hints => {
      setUpdates(previous => {
        const next = { ...previous };
        for (const hint of hints) {
          const existing = next[hint.conversationId];
          if (!existing || hint.revision > existing.revision)
            next[hint.conversationId] = { revision: hint.revision, refresh: (existing?.refresh ?? 0) + 1 };
        }
        return next;
      });
      if (hints.some(hint => !conversationsRef.current.some(conversation => conversation.id === hint.conversationId)))
        refreshRef.current();
      setConversations(previous => previous.map(conversation => {
        const hint = hints.find(item => item.conversationId === conversation.id);
        return hint && hint.revision > conversation.revision ? { ...conversation, revision: hint.revision } : conversation;
      }));
    },
  });
  const enqueueSend = useSendQueue(session, online);

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
  const choose = (conversation: Conversation, compose = false) => {
    setSelected(conversation); setParent(null); setError(""); setDiscovering(false); setFocusComposer(compose);
    if (compose) { setSearch(""); setFilter("all"); }
  };
  const openDiscovery = (origin: HTMLButtonElement) => {
    discoveryOrigin.current = origin.id;
    setDiscovering(true); setParent(null);
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
      if (discovering) closeDiscovery();
      else if (parent) closeThread();
    };
    window.addEventListener("keydown", escape);
    return () => window.removeEventListener("keydown", escape);
  }, [discovering, parent]);
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
  if (!session) return <Login error={error} />;
  const visible = conversations.filter(c => (filter === "all" || c.kind === filter) &&
    `${c.title} ${c.team ?? ""}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));
  const feedProps = {
    session, refresh: refresh + (selected ? updates[selected.id]?.refresh ?? 0 : 0),
    minimumRevision: selected ? updates[selected.id]?.revision ?? 0 : 0, drafts, setDrafts, pending, setPending, check,
    changed: () => {
      if (selected) setUpdates(previous => ({ ...previous, [selected.id]: {
        revision: previous[selected.id]?.revision ?? 0, refresh: (previous[selected.id]?.refresh ?? 0) + 1,
      } }));
    }, online,
    unavailable: () => setParent(null),
    syncParent: (message: Message) => setParent(message),
    authenticationRequired: expireSession, enqueueSend,
  };
  return <div className={`shell ${selected || discovering ? "conversation-open" : ""} ${parent ? "thread-open" : ""}`}>
    <a href="#conversation-main" className="skip-link">Skip to conversation</a>
    <header className="app-header"><span className="brand">Conversations</span><span className="environment">{session.environment} · {session.source}</span>
      <div className="header-tools"><span className="connection" role="status">{connected}</span>
        {connected === "Synchronization unavailable" && <button onClick={reconnect}>Reconnect</button>}
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
          <span className="row-copy"><strong>{c.title}</strong><span>{c.team ?? "Chat"}</span></span>
        </button>)}
          {catalogCursor && <button className="older" disabled={loadingMore} onClick={() => void loadMoreConversations()}>
            {loadingMore ? "Loading conversations..." : "Load more conversations"}</button>}
          {!catalogLoading && !catalogError && !visible.length && <div className="state"><p>{search || filter !== "all" ? "No conversations match your search and filters." : "No conversations are available for this account."}</p>
            {(search || filter !== "all") && <button onClick={() => { setSearch(""); setFilter("all"); }}>Clear search</button>}</div>}
        </div>
        <div className="nav-bottom"><span className="avatar self" aria-hidden>{initials(session.name)}</span><span>{session.name}<small>Entra signed in</small></span></div>
      </nav>
      <main id="conversation-main" tabIndex={-1} className={`conversation ${discovering ? "discovery" : ""}`}>
        {error && <div className="notice" role="status">{error}<button onClick={() => setError("")} aria-label="Dismiss notice">Dismiss</button></div>}
        {discovering ? session.canStartDirectChats ? <PeopleDiscovery query={chatSearch} setQuery={setChatSearch}
          csrf={session.csrf} online={online} close={closeDiscovery} authenticationRequired={expireSession}
          opened={(conversation, person) => {
            setRecipients(previous => ({ ...previous, [conversation.id]: person }));
            setConversations(previous => [...previous.filter(item => item.id !== conversation.id), conversation]);
            choose(conversation, true);
          }} /> : <ChatDiscovery conversations={conversations} query={chatSearch} setQuery={setChatSearch}
          loading={catalogLoading} error={catalogError} more={!!catalogCursor} loadingMore={loadingMore}
          loadMore={() => void loadMoreConversations()} retry={() => setRefresh(n => n + 1)}
          choose={c => choose(c, true)} close={closeDiscovery} /> : selected ? <>
          <Feed key={selected.id} {...feedProps} conversation={selected} parent={null} onThread={setParent} focusComposer={focusComposer}
            header={testMessages => <header className="conversation-header"><button className="back" onClick={() => { setSelected(null); setParent(null); }}>Back</button>
            <span className={`avatar ${selected.kind === "channel" ? "channel" : ""}`} aria-hidden>{selected.kind === "channel" ? "#" : initials(selected.title)}</span>
            <div className="conversation-heading"><h1 ref={conversationTitle} tabIndex={-1}>{selected.title}</h1>
              <p>{recipients[selected.id]?.email ?? selected.team ?? "Chat"} · Teams content</p></div>
            {selected.kind === "chat" && <div className="conversation-actions">{testMessages}
              <button id="change-chat-button" className="recipient-action" onClick={e => openDiscovery(e.currentTarget)}>
                {session.canStartDirectChats ? "Change person" : "Change chat"}</button></div>}</header>} />
        </> : <div className="state welcome"><h1>A little context goes a long way.</h1><p>Choose a conversation to read recent messages and keep the discussion moving.</p>
          <button id="welcome-new-chat" className="primary" onClick={e => openDiscovery(e.currentTarget)}><Icon name="plus" />New chat</button>
          <p className="muted">Recent Teams messages are synchronized into this workspace.</p></div>}
      </main>
      {parent && selected && <aside className="thread-panel" aria-label="Channel thread">
        <header className="conversation-header"><div><h2>Thread</h2><p>{selected.title}</p></div>
          <button onClick={closeThread} aria-label="Close thread">Close</button></header>
        <div className="thread-parent"><strong>{parent.author}</strong><p>{parent.body}</p></div>
        <Feed key={`${selected.id}:${parent.id}`} {...feedProps} conversation={selected} parent={parent} onThread={() => {}} />
      </aside>}
    </div>
    <footer className="app-footer"><span>Development · Approved test accounts and conversations only · Microsoft Teams is the source of record. Updates may take time to synchronize.</span></footer>
  </div>;
}

function ChatDiscovery({ conversations, query, setQuery, loading, error, more, loadingMore, loadMore, retry, choose, close }: {
  conversations: Conversation[]; query: string; setQuery: (value: string) => void; loading: boolean; error: string;
  more: boolean; loadingMore: boolean; loadMore: () => void; retry: () => void;
  choose: (conversation: Conversation) => void; close: () => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  useLayoutEffect(() => { input.current?.focus(); }, []);
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
      <p className="capability-note" id="chat-scope">Only existing chats you can access are shown. Creating a chat and looking up people by email are not available in this workspace.</p>
      {error ? <div className="state" role="alert"><h2>Chats are unavailable</h2><p>{error}</p>
        <button onClick={retry} disabled={loading}>Try chat search again</button></div> :
        !waiting && <>
          {chats.map(chat => <button className="person-row" key={chat.id} aria-label={`Open chat: ${chat.title}`} onClick={() => choose(chat)}>
            <span className="avatar" aria-hidden>{initials(chat.title)}</span>
            <span className="person-copy"><strong>{chat.title}</strong><span>Teams chat</span>
              <span className="person-detail">Available to your signed-in account</span></span>
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
  session: Session; conversation: Conversation; parent: Message | null; refresh: number; minimumRevision: number;
  drafts: Record<string, string>; setDrafts: React.Dispatch<React.SetStateAction<Record<string, string>>>;
  pending: Pending[]; setPending: React.Dispatch<React.SetStateAction<Pending[]>>;
  check: (item: Pending) => Promise<void>; changed: () => void; online: boolean; onThread: (message: Message) => void;
  unavailable: () => void;
  syncParent: (message: Message) => void;
  authenticationRequired: () => void;
  enqueueSend: ReturnType<typeof useSendQueue>;
  header?: (testMessages: ReactNode) => ReactNode;
  focusComposer?: boolean;
};
function Feed(props: FeedProps) {
  const { conversation, parent, session, refresh, drafts, setDrafts, pending, setPending, check, changed, online, onThread } = props;
  const [sendError, setError] = useState("");
  const submittedDraft = useRef<string | null>(null);
  const titleRef = useRef<HTMLHeadingElement>(null);
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const initialScroll = useRef(true);
  const atBottom = useRef(true);
  const prepend = useRef<{ height: number; top: number } | null>(null);
  const key = `${conversation.id}:${parent?.id ?? ""}`;
  const current = pending.filter(p => p.conversationId === conversation.id && p.parentId === (parent?.id ?? null));
  const path = `/api/conversations/${conversation.id}/messages${parent ? `?parent=${encodeURIComponent(parent.id)}` : ""}`;
  const { page, loading, syncing, loadingMore, error: readError, preparing, older: loadOlder, retry } = useMessageFeed({
    path, changesPath: `/api/conversations/${conversation.id}/changes`, parent: parent?.id ?? null,
    refresh, minimumRevision: props.minimumRevision,
    unavailable: props.unavailable, expired: props.authenticationRequired, syncParent: props.syncParent,
  });
  const error = readError || sendError;
  useLayoutEffect(() => {
    if (props.focusComposer) composerRef.current?.focus();
    else titleRef.current?.focus();
  }, []);
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
    if (!page?.cursor || loadingMore || syncing) return;
    if (scrollRef.current) prepend.current = { height: scrollRef.current.scrollHeight, top: scrollRef.current.scrollTop };
    await loadOlder();
  };
  const dispatch = async (body: string, signal?: AbortSignal): Promise<Operation> => {
    if (!page) throw new Error("Reload the conversation before sending.");
    const id = crypto.randomUUID();
    const item: Pending = { conversationId: conversation.id, parentId: parent?.id ?? null, body,
      createdAt: new Date().toISOString(), responded: false,
      operation: { id, state: "pending", messageId: null, error: null } };
    setPending(items => [...items, item]);
    const operation = await props.enqueueSend(conversation.id, {
      operationId: id, window: page.window, body, parentId: parent?.id ?? null,
    }, signal);
    setPending(items => items.map(p => p.operation.id === id ? { ...p, operation, responded: true } : p));
    changed();
    return operation;
  };
  const send = async (event?: FormEvent) => {
    event?.preventDefault();
    if (!page || !drafts[key]?.trim() || !online || error || submittedDraft.current === drafts[key]) return;
    setError("");
    const body = drafts[key];
    submittedDraft.current = body;
    setDrafts(items => ({ ...items, [key]: "" }));
    try {
      await dispatch(body);
    } catch (e) { setError(problem(e)); }
  };
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send(); }
  };
  const optimistic = current.filter(item => item.operation.state !== "confirmed" ||
    !page?.messages.some(message => message.id === item.operation.messageId));
  const outgoing = new Map(optimistic.map(item => [`outgoing-${item.operation.id}`, item]));
  const messages: Message[] = [...(page?.messages ?? []), ...optimistic.map(item => ({
    id: `outgoing-${item.operation.id}`, actorId: session.id, author: session.name, body: item.body,
    createdAt: item.createdAt, modifiedAt: item.createdAt,
    parentId: item.parentId, deleted: false,
  }))].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
  return <>
    {props.header?.(!parent && conversation.kind === "chat" && conversation.isDirect
      ? <TestMessages recipient={conversation.title} available={!!page && !loading && !error && online} send={dispatch} /> : null)}
    <div className="messages" aria-busy={loading} ref={scrollRef} onScroll={event => {
      const element = event.currentTarget;
      atBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < 96;
    }}>
      <h2 ref={titleRef} tabIndex={-1} className="sr-only">{parent ? "Thread replies" : "Recent messages"}</h2>
      {error && <div className="state" role="alert"><h2>{preparing ? "Synchronizing recent context" : "Content is unavailable"}</h2><p>{error}</p><button onClick={() => { setError(""); retry(); }}>Try again</button></div>}
      {loading && <p role="status">Loading recent context...</p>}
      {!loading && syncing && <p role="status">Synchronizing recent context...</p>}
      {!loading && !error && page?.cursor && <button className="older" disabled={loadingMore || syncing} onClick={() => void older()}>
        {loadingMore ? "Loading earlier messages..." : "Load earlier messages"}</button>}
      {!loading && !error && !messages.length && <div className="state"><h2>{parent ? "Start the thread" : "No messages yet"}</h2><p>{parent ? "Add a reply to keep this discussion together."
        : "No messages are available in the recent history you can access."}</p></div>}
      {(!error || optimistic.length > 0) && <MessageList messages={error ? messages.filter(message => outgoing.has(message.id)) : messages}
        actorId={session.id} scrollRef={scrollRef} threaded={!parent && conversation.kind === "channel"} onThread={onThread}
        delivery={message => {
          const item = outgoing.get(message.id);
          return item ? <><Delivery item={item} onCheck={() => void check(item)} />
            {item.operation.state === "rejected" && <div className="delivery-details"><button onClick={() => {
              setDrafts(items => ({ ...items, [key]: item.body }));
              submittedDraft.current = null;
              setPending(items => items.filter(p => p.operation.id !== item.operation.id));
            }}>Edit before a new attempt</button></div>}</> : null;
        }} />}
    </div>
    <div className="composer-wrap">
      {!online && <p className="composer-warning" role="status">Offline. Sending is disabled; drafts stay in this page only.</p>}
      <form className="composer" onSubmit={e => void send(e)}>
        <label className="sr-only" htmlFor={`composer-${key}`}>{parent ? "Write a reply" : "Write a message"}</label>
        <textarea ref={composerRef} id={`composer-${key}`} placeholder={parent ? "Reply in this thread..." : "Write a message..."} maxLength={4096}
          aria-describedby={`composer-note-${key}`}
          value={drafts[key] ?? ""} onChange={e => { submittedDraft.current = null; setDrafts(items => ({ ...items, [key]: e.target.value })); }} onKeyDown={onKey} />
        <button className="primary" type="submit" disabled={loading || !!error || !page || !online || !drafts[key]?.trim()}>
          <Icon name="send" />{parent ? "Reply" : "Send"}</button>
      </form><p className="composer-note" id={`composer-note-${key}`}>Enter to send · Shift+Enter for a new line · Status updates beside each message</p>
    </div>
  </>;
}

function Login({ error }: { error: string }) {
  return <main className="login"><div><span className="brand">Conversations</span><h1>Keep the discussion moving.</h1>
    <p>A focused workspace for chats, channel updates, and the context that matters.</p>
    <p className="notice">Development · Microsoft Teams · Access limited to approved test accounts and conversations</p>
    {error && <p role="alert">{error}</p>}
    <a className="primary sign-in" href="/auth/signin">Sign in with Microsoft</a>
  </div></main>;
}

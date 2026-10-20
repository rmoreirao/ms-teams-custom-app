import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { api, ApiError, post } from "./api";
import type { Conversation, Person } from "./api";

export function PeopleDiscovery({ query, setQuery, csrf, online, close, opened, authenticationRequired }: {
  query: string; setQuery: (value: string) => void; csrf: string; online: boolean;
  close: () => void; opened: (conversation: Conversation, person: Person) => void; authenticationRequired: () => void;
}) {
  const [people, setPeople] = useState<Person[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [openError, setOpenError] = useState("");
  const [opening, setOpening] = useState<string | null>(null);
  const [retry, setRetry] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const busy = useRef(false);
  const active = useRef(true);
  useLayoutEffect(() => {
    active.current = true;
    input.current?.focus();
    return () => { active.current = false; };
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    api<Person[]>("/api/people", { signal: controller.signal }).then(value => {
      if (!controller.signal.aborted) { setPeople(value); setLoadError(""); }
    }).catch(error => {
      if (controller.signal.aborted) return;
      setLoadError(error instanceof Error ? error.message : "People could not be loaded. Try again.");
      if (error instanceof ApiError && error.status === 401) authenticationRequired();
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [retry, authenticationRequired]);
  const choose = async (person: Person) => {
    if (busy.current || !online) return;
    busy.current = true; setOpening(person.id); setOpenError("");
    try {
      const conversation = await post<Conversation>("/api/chats/direct", { personId: person.id }, csrf);
      if (active.current) opened(conversation, person);
    } catch (error) {
      if (!active.current) return;
      setOpenError(error instanceof Error ? error.message : "The chat could not be opened. No message was sent.");
      if (error instanceof ApiError && error.status === 401) authenticationRequired();
    } finally {
      busy.current = false;
      if (active.current) setOpening(null);
    }
  };
  const search = query.trim().toLocaleLowerCase();
  const matches = people.filter(person => `${person.name} ${person.email ?? ""}`.toLocaleLowerCase().includes(search));
  return <section id="chat-discovery" aria-label="Find a person">
    <header className="discovery-header">
      <div className="discovery-title"><h1>New chat</h1>
        <button className="icon-button" onClick={close} aria-label="Close people search">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M6 18 18 6" /></svg>
        </button></div>
      <p>Who would you like to reach? Find a person and open an approved one-to-one Teams chat.</p>
      <div className="people-search"><label htmlFor="people-search">To</label>
        <input ref={input} id="people-search" type="search" autoComplete="off" placeholder="Type a name or email address"
          value={query} onChange={event => setQuery(event.target.value)} aria-describedby="people-hint people-scope" aria-controls="people-results" />
        <p className="search-hint" id="people-hint">Choose a person to open your approved chat. No message is sent until you choose Send.</p></div>
    </header>
    <div className="people-results" id="people-results" aria-busy={loading || !!opening}>
      <div className="results-label"><h2>{search ? "Search results" : "Suggested people"}</h2>
        <p role="status">{loading ? "Loading people..." : loadError ? "People search unavailable" :
          `${matches.length} ${matches.length === 1 ? "person" : "people"}`}</p></div>
      <p className="capability-note" id="people-scope">Only people with an approved one-to-one chat and current policy access are shown. Creating new chats and organization-wide directory lookup are not enabled.</p>
      {openError && <p className="composer-warning" role="alert">{openError}</p>}
      {!online && <p className="composer-warning" role="status">Offline. Opening a chat is disabled; nothing is queued.</p>}
      {opening && <p role="status">Opening the chat in Teams. No message is being sent...</p>}
      {loadError ? <div className="state" role="alert"><h2>People are unavailable</h2><p>{loadError}</p>
        <button onClick={() => setRetry(value => value + 1)} disabled={loading}>Try people search again</button></div> : !loading && <>
        {matches.map(person => <button className="person-row" key={person.id} disabled={!!opening || !online}
          aria-label={`Choose: ${person.name}${person.email ? `, ${person.email}` : ""}`} onClick={() => void choose(person)}>
          <span className="avatar" aria-hidden>{person.name.split(" ").map(word => word[0]).slice(0, 2).join("")}</span>
          <span className="person-copy"><strong>{person.name}</strong><span>Approved contact</span>
            {person.email && <span className="person-detail">{person.email}</span>}</span>
          <span className="person-affordance"><span>{opening === person.id ? "Opening..." : "Open chat"}</span>
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 4h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9l-6 4V6a2 2 0 0 1 2-2Z" /></svg></span>
        </button>)}
        {!matches.length && <div className="state"><h2>{search ? "No matching people" : "No other people available"}</h2>
          <p>{search ? "Try another name. Search covers the approved contacts shown in this workspace." :
            "No other approved contacts are currently available. Check your workspace access."}</p>
          {search && <button onClick={() => { setQuery(""); input.current?.focus(); }}>Clear people search</button>}</div>}
      </>}
      <p className="directory-note">Verify the name and email before writing. Existing one-to-one chats are reused, not duplicated.</p>
    </div>
  </section>;
}

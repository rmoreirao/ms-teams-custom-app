import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { api, ApiError, post } from "./api";
import type { ChannelOperation, ChannelTeam, Conversation, DiscoveredChannel, RealtimeEvent, Session } from "./api";

type Job = { snapshot: ChannelOperation; teamId?: string; label?: string; requestError?: string };
const terminal = (state: ChannelOperation["state"]) => ["ready", "denied", "failed", "capacity"].includes(state);
const unique = <T extends { id: string }>(items: T[]) => [...new Map(items.map(item => [item.id, item])).values()];
function validate(snapshot: ChannelOperation, id: string): ChannelOperation {
  if (!snapshot || snapshot.id !== id || !Number.isSafeInteger(snapshot.revision) || snapshot.revision < 0 ||
      !["teams", "channels", "open"].includes(snapshot.kind) ||
      !["pending", "preparing", "deferred", "ready", "denied", "failed", "capacity"].includes(snapshot.state) ||
      snapshot.state === "ready" && (snapshot.kind === "teams" && !Array.isArray(snapshot.teams) ||
        snapshot.kind === "channels" && !Array.isArray(snapshot.channels) ||
        snapshot.kind === "open" && snapshot.conversation?.kind !== "channel"))
    throw new Error("Channel preparation returned an invalid response. Reload the channel list.");
  return snapshot;
}

export function useChannelBrowser(input: {
  session: Session | null; live: boolean; events?: RealtimeEvent[]; reconcile: number;
  opened: (conversation: Conversation) => void; expired: () => void;
}) {
  const callbacks = useRef(input);
  callbacks.current = input;
  const jobs = useRef<Record<string, Job>>({});
  const [snapshots, setSnapshots] = useState<Record<string, Job>>({});
  const [teamPages, setTeamPages] = useState<string[]>([]);
  const [channelPages, setChannelPages] = useState<Record<string, string[]>>({});
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [query, setQuery] = useState("");
  const [openId, setOpenId] = useState<string | null>(null);
  const currentOpen = useRef<string | null>(null);
  const opened = useRef(new Set<string>());
  const checks = useRef(new Map<string, number>());
  const generation = useRef(0);
  const controllers = useRef(new Set<AbortController>());
  useEffect(() => {
    generation.current++;
    jobs.current = {}; setSnapshots({}); setTeamPages([]); setChannelPages({}); setExpanded({}); setQuery("");
    setOpenId(null); currentOpen.current = null; opened.current.clear(); checks.current.clear();
    return () => { generation.current++; for (const controller of controllers.current) controller.abort(); controllers.current.clear(); };
  }, [input.session?.id]);
  const commit = (id: string, value: ChannelOperation) => {
    const previous = jobs.current[id];
    if (!previous || value.revision < previous.snapshot.revision ||
        value.revision === previous.snapshot.revision && terminal(previous.snapshot.state)) return;
    jobs.current = { ...jobs.current, [id]: { ...previous, snapshot: value, requestError: undefined } };
    setSnapshots(jobs.current);
    if (value.kind === "open" && value.state === "ready" && value.conversation && currentOpen.current === id &&
        !opened.current.has(id)) {
      opened.current.add(id);
      callbacks.current.opened(value.conversation);
    }
  };
  const run = async (id: string, request: (signal: AbortSignal) => Promise<ChannelOperation>) => {
    const version = generation.current;
    const controller = new AbortController();
    controllers.current.add(controller);
    try {
      const value = validate(await request(controller.signal), id);
      if (version === generation.current && !controller.signal.aborted) commit(id, value);
    } catch (error) {
      if (version !== generation.current || controller.signal.aborted) return;
      if (error instanceof ApiError && error.status === 401) { callbacks.current.expired(); return; }
      const job = jobs.current[id];
      if (job && error instanceof ApiError && [403, 404].includes(error.status)) {
        jobs.current = { ...jobs.current, [id]: { ...job, snapshot: { ...job.snapshot, state: "denied",
          teams: null, channels: null, conversation: null, error: error.message } } };
        setSnapshots(jobs.current);
      } else if (job && job.snapshot.revision === 0 && !terminal(job.snapshot.state))
        commit(id, { ...job.snapshot, state: error instanceof ApiError && error.status === 403 ? "denied" : "failed",
          error: error instanceof Error ? error.message : "The request failed. Check this operation or reload the list." });
      else if (job) {
        jobs.current = { ...jobs.current, [id]: { ...job, requestError: error instanceof Error ? error.message :
          "The latest preparation status could not be checked. Try Check again when live updates are available." } };
        setSnapshots(jobs.current);
      }
    } finally { controllers.current.delete(controller); }
  };
  const register = (kind: ChannelOperation["kind"], details: Partial<Job> = {}) => {
    const id = crypto.randomUUID();
    jobs.current = { ...jobs.current, [id]: { ...details, snapshot: {
      id, kind, state: "pending", revision: 0, error: null, teams: null, channels: null, cursor: null, conversation: null,
    } } };
    setSnapshots(jobs.current);
    return id;
  };
  const check = (id: string, revision = 0) => {
    if (!callbacks.current.live || !jobs.current[id]) return;
    const waiting = checks.current.get(id);
    if (waiting !== undefined) { checks.current.set(id, Math.max(waiting, revision)); return; }
    checks.current.set(id, revision);
    void run(id, signal => api<ChannelOperation>(`/api/channel-operations/${encodeURIComponent(id)}`, { signal }))
      .finally(() => {
        const demanded = checks.current.get(id) ?? 0;
        checks.current.delete(id);
        // A newer event arriving during the read gets one coalesced follow-up, not a status timer.
        if (demanded > revision && demanded > (jobs.current[id]?.snapshot.revision ?? 0)) check(id, demanded);
      });
  };
  const loadTeams = (cursor?: string | null) => {
    if (!callbacks.current.live || !callbacks.current.session?.canBrowseChannels) return;
    const id = register("teams");
    setTeamPages(previous => cursor ? [...previous, id] : [id]);
    const parameters = new URLSearchParams({ requestId: id });
    if (cursor) parameters.set("cursor", cursor);
    void run(id, signal => api<ChannelOperation>(`/api/teams?${parameters}`, { signal }));
  };
  const loadChannels = (teamId: string, cursor?: string | null) => {
    if (!callbacks.current.live || !callbacks.current.session?.canBrowseChannels) return;
    const id = register("channels", { teamId });
    setChannelPages(previous => ({ ...previous, [teamId]: cursor ? [...(previous[teamId] ?? []), id] : [id] }));
    const parameters = new URLSearchParams({ requestId: id });
    if (cursor) parameters.set("cursor", cursor);
    void run(id, signal => api<ChannelOperation>(`/api/teams/${encodeURIComponent(teamId)}/channels?${parameters}`, { signal }));
  };
  const select = (channel: DiscoveredChannel) => {
    if (!callbacks.current.live || !callbacks.current.session?.canBrowseChannels) return;
    const id = register("open", { teamId: channel.teamId, label: `${channel.teamName} / #${channel.name}` });
    currentOpen.current = id; setOpenId(id);
    void run(id, signal => post<ChannelOperation>("/api/channels/open",
      { operationId: id, selection: channel.selection }, callbacks.current.session!.csrf, signal));
  };
  const receive = (events: RealtimeEvent[]) => {
    const affected = new Map<string, number>();
    for (const event of events)
      if (event.type === "channel.operation.changed" && event.operationId && jobs.current[event.operationId] &&
          event.revision > jobs.current[event.operationId].snapshot.revision)
        affected.set(event.operationId, Math.max(event.revision, affected.get(event.operationId) ?? 0));
    for (const [id, revision] of affected) check(id, revision);
  };
  useEffect(() => { receive(input.events ?? []); }, [input.events]);
  useEffect(() => {
    if (input.reconcile > 0 && input.live)
      for (const id of Object.keys(jobs.current)) check(id);
  }, [input.reconcile, input.live]);
  const pageJobs = (ids: string[]) => ids.map(id => snapshots[id]).filter((job): job is Job => !!job);
  const teamJobs = pageJobs(teamPages);
  return {
    query, setQuery, expanded, openJob: openId ? snapshots[openId] : undefined, teamJobs,
    teams: unique(teamJobs.flatMap(job => job.snapshot.teams ?? [])),
    channels: (teamId: string) => {
      const pages = pageJobs(channelPages[teamId] ?? []);
      return { jobs: pages, items: unique(pages.flatMap(job => job.snapshot.channels ?? [])) };
    },
    toggle: (teamId: string) => {
      setExpanded(previous => ({ ...previous, [teamId]: !previous[teamId] }));
      if (!channelPages[teamId]) loadChannels(teamId);
    },
    loadTeams, loadChannels, select, check, receive,
  };
}

export function ChannelBrowser({ model, live, reconnect, close }: {
  model: ReturnType<typeof useChannelBrowser>; live: boolean; reconnect: () => void; close: () => void;
}) {
  const search = useRef<HTMLInputElement>(null);
  useLayoutEffect(() => { search.current?.focus(); }, []);
  const query = model.query.trim().toLocaleLowerCase();
  const latest = model.teamJobs.at(-1)?.snapshot;
  const busy = model.teamJobs.some(job => !terminal(job.snapshot.state));
  const teams = model.teams.filter(team => team.name.toLocaleLowerCase().includes(query) ||
    model.channels(team.id).items.some(channel => channel.name.toLocaleLowerCase().includes(query)));
  return <section id="channel-browser" className="channel-browser" aria-label="Browse channels">
    <header className="discovery-header">
      <div className="discovery-title"><h1>Browse channels</h1><button onClick={close}>Back</button></div>
      <p>Choose a team, then a standard channel. Selecting a channel prepares its recent posts; it never joins a team or sends a message.</p>
      <div className="people-search"><label htmlFor="browse-channel-search">Search loaded teams and channels</label>
        <input ref={search} id="browse-channel-search" type="search" autoComplete="off" value={model.query}
          onChange={event => model.setQuery(event.target.value)} />
        <p className="search-hint">Search covers loaded results only. Expand a team to load its eligible channels.</p></div>
    </header>
    <div className="people-results">
      {!live && <div className="state" role="status"><h2>Live connection required</h2>
        <p>Connect to receive channel preparation updates. Nothing will be queued while synchronization is unavailable.</p>
        <button onClick={reconnect}>Connect live updates</button></div>}
      <div className="channel-browser-actions"><button disabled={!live || busy} onClick={() => model.loadTeams()}>
        {model.teamJobs.length ? "Reload teams" : "Load teams"}</button></div>
      <OperationState job={model.openJob} live={live} check={model.check} />
      {model.teamJobs.map(job => <OperationState key={job.snapshot.id} job={job} live={live} check={model.check} />)}
      {teams.map((team: ChannelTeam) => {
        const channels = model.channels(team.id);
        const last = channels.jobs.at(-1)?.snapshot;
        const waiting = channels.jobs.some(job => !terminal(job.snapshot.state));
        const matches = channels.items.filter(channel => team.name.toLocaleLowerCase().includes(query) ||
          channel.name.toLocaleLowerCase().includes(query));
        return <section key={team.id} className="browse-team" aria-label={team.name}>
          <h2><button className="team-toggle" aria-expanded={!!model.expanded[team.id]} disabled={!live}
            onClick={() => model.toggle(team.id)}><span aria-hidden>{model.expanded[team.id] ? "⌄" : "›"}</span>{team.name}</button></h2>
          {model.expanded[team.id] && <div className="team-channels">
            {channels.jobs.map(job => <OperationState key={job.snapshot.id} job={job} live={live} check={model.check} />)}
            {matches.map(channel => <button key={channel.id} className="conversation-row" disabled={!live}
              aria-label={`Open channel: ${team.name} / ${channel.name}`} onClick={() => model.select(channel)}>
              <span className="avatar channel" aria-hidden>#</span><span className="row-copy"><strong>{channel.name}</strong>
                <span>Standard channel · Prepare and open</span></span></button>)}
            {last?.state === "ready" && !matches.length && <p className="nav-state">
              {query ? "No loaded channels match this search." : "No eligible standard channels are available in this team."}</p>}
            <button disabled={!live || waiting} onClick={() => model.loadChannels(team.id)}>Reload channels</button>
            {last?.cursor && <button disabled={!live || waiting} onClick={() => model.loadChannels(team.id, last.cursor)}>Load more channels</button>}
          </div>}
        </section>;
      })}
      {latest?.state === "ready" && !teams.length && <div className="state"><h2>{query ? "No loaded teams match" : "No teams available"}</h2>
        <p>{query ? "Try another search or load more teams." : "No joined teams are currently available to browse."}</p></div>}
      {latest?.cursor && <button disabled={!live || busy} onClick={() => model.loadTeams(latest.cursor)}>Load more teams</button>}
    </div>
  </section>;
}

function OperationState({ job, live, check }: { job?: Job; live: boolean; check: (id: string) => void }) {
  if (!job || job.snapshot.state === "ready" && !job.requestError) return null;
  const snapshot = job.snapshot;
  const failed = !!job.requestError || ["denied", "failed", "capacity"].includes(snapshot.state);
  const title = snapshot.state === "capacity" ? "Channel capacity reached" : snapshot.state === "denied" ? "Channel access denied" :
    snapshot.state === "failed" ? "Channel request unavailable" : snapshot.state === "deferred" ?
      snapshot.kind === "open" ? "Channel preparation delayed" : snapshot.kind === "teams" ? "Team loading delayed" : "Channel loading delayed" :
      snapshot.kind === "open" ? "Preparing channel" :
      snapshot.kind === "teams" ? "Loading teams" : "Loading channels";
  return <div className="channel-operation" role={failed ? "alert" : "status"}>
    <strong>{title}{job.label ? `: ${job.label}` : ""}</strong>
    <p>{job.requestError ?? snapshot.error ?? (snapshot.state === "deferred" ? "Preparation is delayed. Progress will appear when live updates arrive." :
      "Waiting for a live preparation update. You can leave this view without cancelling the request.")}</p>
    {snapshot.state === "denied" && <p>Your selection may have expired. Reload the team's channels before selecting again.</p>}
    {snapshot.state === "failed" && snapshot.kind !== "open" &&
      <p>Use Reload {snapshot.kind === "teams" ? "teams" : "channels"} to start a new discovery request.</p>}
    {(!terminal(snapshot.state) || job.requestError) &&
      <button disabled={!live} onClick={() => check(snapshot.id)}>Check {snapshot.kind === "open" ? "preparation" : "request"}</button>}
  </div>;
}

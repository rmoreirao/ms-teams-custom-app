import { useVirtualizer } from "@tanstack/react-virtual";
import { useState } from "react";
import type { ReactNode, RefObject } from "react";
import { MessageContent } from "./MessageContent";
import { MessageStatus } from "./MessageStatus";
import type { Message } from "./api";

export function MessageList({ messages, actorId, scrollRef, threaded, onThread, delivery, renderThread, compact, inline }: {
  messages: Message[]; actorId: string; scrollRef: RefObject<HTMLDivElement | null>;
  threaded: boolean; onThread: (message: Message) => void;
  delivery?: (message: Message) => ReactNode;
  renderThread?: (message: Message) => ReactNode; compact?: boolean; inline?: boolean;
}) {
  const virtual = !inline && messages.length > 60;
  const rows = useVirtualizer({
    count: messages.length, getScrollElement: () => scrollRef.current, enabled: virtual,
    getItemKey: index => messages[index].id, estimateSize: () => 112, overscan: 8,
    anchorTo: threaded ? "start" : "end", followOnAppend: !threaded, scrollEndThreshold: 96,
  });
  const render = (message: Message, index: number) => <article className={`message ${message.actorId === actorId ? "own" : ""}`}
    key={message.id} data-message-id={message.id} aria-posinset={index + 1} aria-setsize={messages.length}>
    <span className="avatar" aria-hidden>{message.author.split(" ").map(part => part[0]).slice(0, 2).join("")}</span>
    <div className="message-content">
      <div className="message-meta"><strong>{message.author}</strong>
        <time dateTime={message.createdAt}>{new Date(message.createdAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</time>
        {message.modifiedAt !== message.createdAt && !message.deleted && <span>Edited</span>}</div>
      <div className="message-body"><div className="message-bubble"><MessageBody message={message} compact={compact} /></div>
        {delivery?.(message) ?? (message.actorId === actorId && !message.deleted && <MessageStatus state="confirmed" />)}</div>
      {threaded && !message.deleted && !message.id.startsWith("outgoing-") && (renderThread ? renderThread(message) :
        <button className="reply-button" onClick={() => onThread(message)}>Open thread</button>)}
    </div>
  </article>;
  if (!virtual) return <>{messages.map(render)}</>;
  return <div role="feed" aria-label="Loaded messages" className="virtual-messages" style={{ height: rows.getTotalSize() }}>
    {rows.getVirtualItems().map(row => <div key={row.key} data-index={row.index}
      ref={rows.measureElement} className="virtual-message-row" style={{ transform: `translateY(${row.start}px)` }}>
      {render(messages[row.index], row.index)}
    </div>)}
  </div>;
}

function MessageBody({ message, compact }: { message: Message; compact?: boolean }) {
  const [expanded, setExpanded] = useState(false);
  const long = compact && !message.deleted && message.body.length > 280;
  return <>
    <MessageContent message={long && !expanded ? { ...message, body: [...message.body].slice(0, 280).join("") + "...", htmlBody: null } : message} />
    {long && <button className="reply-button" aria-expanded={expanded} onClick={() => setExpanded(value => !value)}>
      {expanded ? "Show less" : "Show more"}</button>}
  </>;
}

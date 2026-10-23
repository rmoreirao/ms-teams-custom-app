import { useMemo } from "react";
import DOMPurify from "dompurify";
import type { Message } from "./api";

export function sanitizeMessageHtml(html: string): string {
  const fragment = DOMPurify.sanitize(html, {
    ALLOWED_TAGS: ["p", "div", "br", "strong", "b", "em", "i", "u", "s", "blockquote", "pre", "code", "ul", "ol", "li", "a", "span"],
    ALLOWED_ATTR: ["href", "title"],
    ALLOW_DATA_ATTR: false, ALLOW_ARIA_ATTR: false, RETURN_DOM_FRAGMENT: true,
  });
  for (const link of fragment.querySelectorAll("a")) {
    const href = link.getAttribute("href");
    try {
      const url = new URL(href ?? "", "https://teams.microsoft.com/");
      if (!href || url.protocol !== "https:" || url.username || url.password) link.removeAttribute("href");
      else {
        link.setAttribute("href", url.href);
        link.setAttribute("target", "_blank");
        link.setAttribute("rel", "noopener noreferrer");
      }
    } catch { link.removeAttribute("href"); }
  }
  const container = document.createElement("div");
  container.append(fragment);
  return container.innerHTML;
}

export function MessageContent({ message }: { message: Message }) {
  const html = useMemo(() => message.htmlBody && !message.deleted ? sanitizeMessageHtml(message.htmlBody) : null,
    [message.htmlBody, message.deleted]);
  const source = useMemo(() => {
    if (!message.teamsUrl || message.deleted) return null;
    try {
      const url = new URL(message.teamsUrl);
      return url.protocol === "https:" && url.port === "" && !url.username && !url.password &&
        ["teams.microsoft.com", "teams.cloud.microsoft"].includes(url.hostname) ? url.href : null;
    } catch { return null; }
  }, [message.teamsUrl, message.deleted]);
  return <>
    {message.deleted ? <p className="message-text">This message was deleted.</p> :
      html ? <div className="message-text rich-message" dangerouslySetInnerHTML={{ __html: html }} /> :
        <p className="message-text">{message.body}</p>}
    {!message.deleted && message.contentNotice && <p className="composer-note">{message.contentNotice}</p>}
    {source && message.contentNotice && <a href={source} target="_blank" rel="noopener noreferrer">Open in Teams</a>}
  </>;
}

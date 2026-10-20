import type { DeliveryState } from "./api";

export function MessageStatus({ state }: { state: DeliveryState }) {
  const label = state === "confirmed" ? "Sent" : state === "rejected" ? "Not sent"
    : state === "unknown" ? "Delivery uncertain" : "Sending";
  const description = state === "confirmed" ? "Sent to Teams. This is not a read receipt."
    : state === "unknown" ? "Delivery uncertain. Check the original send; do not resend."
    : state === "rejected" ? "Not sent. Review the error before a new attempt."
    : "Sending. Confirmation will appear here.";
  return <span className={`message-status ${state}`} role="status" aria-label={label} title={description}>
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d={state === "confirmed" ? "m8 12 2.5 2.5 5-5"
        : state === "unknown" || state === "rejected" ? "M12 7v6m0 3h.01" : "M12 7v5l3 2"} />
    </svg>
    <span className="sr-only">{label}</span>
  </span>;
}

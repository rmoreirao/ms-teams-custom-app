# ADR 0014: Event-driven channel discovery and admission

## Status

Accepted. **Activated across all five Development roles on 2026-10-29,
following separate explicit approval.** After the image-only release, additive
registry/queue access preceded registry-only then full activation, with Api last.
Registry, discovery and dynamic sending are enabled for the same three admitted
users and eligible selected channels, with at most seven days of recent history.
Existing mappings/history, identities and admission are preserved; no Entra
permission changes were needed. Signed-in new-channel and real-send acceptance
remain distinct from the healthy runtime/configuration activation.

## Decision

Keep channel discovery metadata-only and restricted to admitted users' joined
teams. Admit only same-tenant standard unmoderated channels in unarchived teams
under an explicit history policy. Selecting a channel requests durable,
idempotent Control-owned preparation; it neither joins nor creates a Teams
resource.

An additive Control-written registry supplies immutable resource mappings to all
five runtime roles. Preserve configured IDs and existing histories. Registry
activation is separate from admitting new channels, so disabling discovery does
not abandon registered resources or accepted operations.

Use the existing Cosmos projection and optional Redis message-page cache.
Authorization and parent/history checks apply even to warm cache hits. Keep
seven-day message retention, short bounded Redis pages, revision/generation
fencing, and explicit unknown versus known-empty views. Redis failure falls
back to Cosmos, never directly to Graph.

Graph notifications drive content changes. Durable actor/view outbox events
drive browser progress through Cosmos change feed and Web PubSub. No recurring
browser catalog/content/status polling or polling fallback is introduced.
Reconnect uses authorized durable catch-up; authorization renewal and Graph
subscription renewal remain separate control-plane work.

The Control worker also recovers accepted, unleased operations from durable
metadata after process/broker loss. This bounded control-plane repair is not
browser polling. Discovery selections/cursors are signed and actor-bound;
opening rechecks source membership and channel policy rather than trusting a
previous discovery result.

Read moderation settings through a dedicated read-only Graph beta metadata path:
the property is not present in the v1.0 channel schema. Keep messages,
subscriptions and generic Graph clients on v1.0. Bind metadata continuations to
the selected team and fixed fields without relationship expansion. Share strict
archive/membership/moderation checks between discovery, opening and authority
renewal; accept explicit unmoderated settings, not merely a null-only fixture.
This beta dependency is for Development, not production-supported API acceptance.

## Consequences

New queue/container grants and compatible code across all roles precede
activation. Private/shared/moderated/archived cases, channel creation, arbitrary
application users, and destructive channel cache-cleanup controls remain outside
scope. Offline evidence does not establish real Teams/cloud acceptance.

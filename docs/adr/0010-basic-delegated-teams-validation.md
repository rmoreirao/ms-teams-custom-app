# ADR 0010: Basic delegated Teams validation before production architecture

| Field | Value |
| --- | --- |
| Status | Accepted for the explicitly approved three-user synthetic validation scope |
| Date | 2026-10-19 |
| Scope | Alex, Sam and Jordan; one dedicated new group chat/team/channel; existing Azure deployment |
| Related decisions | ADRs 0002, 0005, 0008, 0009 |

## Decision

The user chose basic functional validation before additional billable Strong-consistency infrastructure. Add an explicit `GraphPilot` mode using delegated source reads/writes on the existing BFF and Session-consistency operation store. Keep the production event-driven `Live` mode separate and gated.

Only selected tagged synthetic resources and the three directly assigned users are admitted. Message bodies are fetched from Graph into request memory and are stripped before persistence. Retain body-free journal/idempotency/uncertainty safeguards; use polling rather than claim rich notification coverage.

Use exact, user-approved per-user delegated read/send grants. Setup-only application permissions are short-lived and removed; ordinary sends never use application impersonation or migration APIs. Defer optional passkey registration only under explicit user opt-in when Entra offers the exact deferral after existing password/TOTP; never bypass mandatory authentication/setup.

## Consequences and evidence

All three users completed fresh password/TOTP sign-in and real chat read/send, channel read/post and thread reply journeys, with real Graph IDs and matching author/body read-back. No new Azure resource/SKU, user licence, tenant role or security policy was required.

This acceptance is bounded functional evidence, not production acceptance of Graph-dependent read scaling or a substitute for G0-G7. See [basic validation guide/evidence](../graph-pilot.md). ADR 0009's production profile and its policy/consistency/recovery gates remain unchanged.

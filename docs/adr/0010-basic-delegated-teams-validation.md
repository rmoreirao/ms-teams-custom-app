# ADR 0010: Basic delegated Teams validation before production architecture

| Field | Value |
| --- | --- |
| Status | Historical GraphPilot runtime superseded by Live-only Development; approved scope/consent and evidence retained |
| Date | 2026-10-19 |
| Scope | Alex, Sam and Jordan; dedicated group chat/team/channel plus explicitly enabled exact-pair chats; existing Azure deployment |
| Related decisions | ADRs 0002, 0005, 0008, 0009 |

## Decision

**Amendment:** GraphPilot is no longer a supported application runtime. The current Live profile reuses approved existing chats under fresh authority, without application-driven creation or pilot polling. This record preserves earlier choices/results; do not deploy its old mode or commands. Runtime removal does not revoke existing grants, rename persisted resources or broaden admission.

The user chose basic functional validation before additional billable Strong-consistency infrastructure. Add an explicit `GraphPilot` mode using delegated source reads/writes on the existing BFF and Session-consistency operation store. Keep the production event-driven `Live` mode separate and gated.

Only selected tagged synthetic resources and the three directly assigned users are admitted. Message bodies are fetched from Graph into request memory and are stripped before persistence. Retain body-free journal/idempotency/uncertainty safeguards; use polling rather than claim rich notification coverage.

Use exact, user-approved per-user delegated read/send grants. Setup-only application permissions are short-lived and removed; ordinary sends never use application impersonation or migration APIs. Defer optional passkey registration only under explicit user opt-in when Entra offers the exact deferral after existing password/TOTP; never bypass mandatory authentication/setup.

## Consequences and evidence

### Three-person direct-chat follow-up

On 2026-10-19 the user explicitly approved people search and one-to-one chats between Alex, Sam and Jordan, plus delegated `Chat.Create` for those three users only. Filtering the original group chat's title did not meet that requirement.

Discovery uses real membership metadata of the original approved chat under existing `Chat.Read`. Admitted direct chats must be `oneOnOne` with the exact approved pair and use deterministic body-free mapping metadata plus a separate activation/history cutoff. Creation can return an existing pair's chat; selection sends no message, and uncertain message sends remain non-replayable. No organization-wide directory grant, arbitrary recipient, new group/team, app-only runtime permission or production-gate relaxation is authorized.

All three users completed fresh password/TOTP sign-in and real chat read/send, channel read/post and thread reply journeys, with real Graph IDs and matching author/body read-back. No new Azure resource/SKU, user licence, tenant role or security policy was required.

This acceptance is bounded functional evidence, not production acceptance of Graph-dependent read scaling or a substitute for G0-G7. See [basic validation guide/evidence](../graph-pilot.md). ADR 0009's production profile and its policy/consistency/recovery gates remain unchanged.

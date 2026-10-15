# ADR 0002: Permission separation and copied-data authorization

| Field | Value |
| --- | --- |
| Status | Accepted for reference design; production acceptance conditional |
| Date | 2026-10-15 |
| Related requirements | FR-01, FR-02, FR-05, NFR-04, NFR-05 |

## Context

Interactive user actions and unattended ingestion have different permission requirements. A background application's permission to read a resource does not grant every user access to the copied content.

Cache/projection reads do not execute the original delegated Graph permission and policy checks on every access. Membership and identity notifications can be delayed, and browser storage policy cannot erase content already copied from an unmanaged device.

## Decision

Use distinct interactive and background identities with the least privileged documented scopes for their responsibilities.

Send ordinary user-authored messages through delegated authorization. Do not use migration permissions or a general app-only identity to impersonate users.

Maintain and enforce explicit conversation access, history visibility, policy state, application-session revocation, and authorized connection/group membership. Clients cannot select their own access groups or publish messages that bypass Graph confirmation.

Define an approved freshness bound and a trustworthy revocation/entitlement mechanism. Fail closed on missing or expired authorization state. Notification receipt alone is not proof that no newer access change exists.

Protect server-side tokens and decryption keys. Do not persist business content or tokens in browser storage or offline caches by default. Include source-to-application lifecycle delay in the security assessment.

## Alternatives considered

| Alternative | Reason not selected |
| --- | --- |
| One broadly privileged identity for everything | Increases privilege and confuses interactive versus unattended authorization |
| Treat app-only ingestion consent as user access | Can disclose content beyond a user's rights and bypass organizational policy |
| Live Graph access check on every cached read | Reintroduces dependency/throughput constraints; cannot be assumed viable at the target scale |
| Notification-only immediate revocation guarantee | Unsupported when access-change delivery is delayed or missing |
| Browser cache clearing as complete device erasure | Cannot remove screenshots, downloads, or copied content |

## Consequences

Permission boundaries and auditability are clearer. Application-owned authorization and policy enforcement become security-critical components.

The reference does not guarantee instantaneous enforcement of a Teams access change. Production needs a demonstrated bound and accepted authority; unresolved authorization freshness blocks deployment.

Device-level data assurances stronger than preventing persistent application caching require a separately approved protection/management model.

## Deployment acceptance conditions

- Verify endpoint/version permissions, tenant consent, user licensing, and any Teams app installation requirements.
- Approve background subscription scope and data-processing purpose.
- Demonstrate revocation, history restrictions, DLP/policy changes, and cross-user isolation on reads and pushes.
- Demonstrate session/connection invalidation and measure lifecycle propagation.
- Review Conditional Access and other controls that are no longer evaluated through a live delegated Graph read.
- Prove that uncertainty and recovery do not fail open.

## Links

- [Technical design: identity and data protection](../technical-design.md#8-identity-authorization-and-data-protection)
- [ADR 0001: bounded read model](0001-teams-system-of-record-and-bounded-read-model.md)
- [Graph best practices](https://learn.microsoft.com/en-us/graph/best-practices-concept)
- [Chat sending permissions](https://learn.microsoft.com/en-us/graph/api/chat-post-messages?view=graph-rest-1.0)

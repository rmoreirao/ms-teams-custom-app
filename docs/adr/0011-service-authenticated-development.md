# ADR 0011: Service-authenticated, role-isolated Development

## Status

Approved cloud migration deployed on 2026-10-21. Five role-isolated workloads, sync consent/federation, scoped access and Receiver callback routing are applied; all five app-owned subscriptions, covered resources and expected memberships were verified before admission reopened. Renewal, unattended and fresh interactive acceptance, and final legacy source cleanup remain separate checks. This does not claim production acceptance.

## Context

The compact Development profile validates real Teams with three users and five conversations. Its retained delegated user contexts cannot outlive their original interactive sessions. Borrowing those identities for background work makes subscriptions, source policy refresh and repair depend on user sign-in.

The API also hosts Receiver/Control, and its managed identity has authority/control writes. Merely adding a privileged identity to the same process does not isolate it from the API. A production-like Dev environment must exercise the intended service-authentication and role boundaries, not weaken session expiry.

## Decision

- Keep the initial three admitted users and five existing resource mappings/history floors in the confirmed dedicated test tenant.
- Keep Web sign-in and normal current-user sends delegated. Keep the interactive session lifetime unchanged.
- Run Api, Receiver, Projector, Delivery and Control in separate workloads with distinct identities. Reuse the private platform and canonical edge origin; explicitly review the additional workloads and their costs.
- Federate only Control to the owned sync application for background Graph access. Do not store a fourth user's credentials, attach Control to the API, or fall back to a developer/user identity.
- Implement the existing entitlement-feed contract with a bounded Graph-backed provider inside Control. Its input is real membership/history, direct Web admission and enabled local-member account evidence, plus the approved resource policy boundary. Cached evidence does not renew itself.
- The provider does not claim general enterprise DLP/visibility-policy equivalence. Unsupported/private/shared/archived/moderated/foreign or unproven source cases fail closed; broader policy needs an explicit contract/provider extension.
- App-level read permission is not a user grant. Only current admitted source members receive fresh entitlements. Revocations and history changes remain enforced independently of the service's read capability.
- Use only approved resource/policy paths and Graph v1.0. Tenant-wide application read grants remain broad Entra permissions and require explicit approval despite the application allowlist.
- Preserve immutable-image, all-target preflight/drift and exact-revision release protections. Routine releases cannot grant access, provision infrastructure, alter admission or reset data.
- Preserve P1 canonical pagination, resource budget, repair/backoff, generation fencing and quiet structured diagnostics.

## Migration

Inventory the current source/identity/permission state privately. Produce exact consent, federation, publisher admission, effective privilege-retirement and resource/edge/cost diffs. Create only the separately approved missing identities, resolve their actual IDs, then reconcile reviewed trust/access and isolated workloads with synchronization/admission initially off.

Fence the old controller. A separate sync application does not inherit the old delegated application's subscriptions; use journaled, approved replacement scopes and validate callback audiences/publisher/encryption bindings. Preserve logical IDs, original cutoffs, keys and operation/repair/lease evidence. Adding narrow RBAC does not remove old broad roles; remove the reviewed compact-role excess before admission.

Unattended acceptance requires source token/subscription renewal and repair with the test users signed out, plus correct denied access and independent delegated sends. After acceptance, remove the old runtime branches/settings/helpers and separately review obsolete grants/cached contexts for retirement. No incidental deletion of users, source resources, registrations or data is authorized.

## Consequences

Dev uses the same authentication and workload contracts intended for production, with bounded users/resources and explicit sizing. User-session expiry affects interactive access, not the connector's credentials. Consent and policy approval remain real prerequisites; the staged code must not be described as a completed live migration before those gates and unattended evidence are established.

## References

- [Live runtime](../live-runtime.md)
- [Development migration gates](../dev-guide.md#service-identity-migration-approval-gated)
- [Graph notification permissions](https://learn.microsoft.com/en-us/graph/teams-changenotifications-chatmessage)

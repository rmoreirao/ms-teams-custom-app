# Dev functional validation report

## Development Service Bus Standard cutover (2026-10-26)

**The five Development roles and existing manual diagnostic job now reference
the new Standard namespace. Following explicit user approval, the old Premium
namespace and its obsolete Service Bus private endpoint were deleted on
2026-10-26; independent post-deletion checks passed.**
This was an endpoint-only infrastructure cutover: all deployed immutable images,
identities, other environment settings, scale, ingress, callback bindings and
admission policy were verified unchanged. No application image was rebuilt.

| Evidence | Result / boundary |
| --- | --- |
| Scoped infrastructure deployment | Created only `teamsrefdev-unc3jasrpkgbo-bus-standard`, three active queues and seven exact queue-scoped sender/receiver grants; old Premium and unrelated resources were untouched |
| Standard security and contracts | Standard SKU, public access, TLS 1.2 and local/SAS authentication disabled; TTL, lock, retry count, duplicate detection, capacity and dead-letter-on-expiry match the three active old queues |
| Container Apps connectivity and access | One-off executions under the already attached Api, Control and Projector identities opened permitted sender/receiver links and rejected cross-role/cross-queue operations; permitted sender links negotiated exactly 262,144-byte batches. These probes sent and consumed no messages |
| Maintenance cutover | Stopped producers, then consumers using app-level stop/start without changing Single mode or replica limits. After every workload stopped, waited 75 seconds, observed all four old queues empty, and repeated the empty check before switching; no purge or automatic transfer occurred |
| Exact workload acceptance | All five new revisions active and healthy; full preserved-state comparison allowed only the Service Bus endpoint and revision suffix to change. The idle manual job changed only its endpoint |
| Public application | Readiness, shell loading, anonymous API denial and canonical Entra redirect passed after the switch |
| Normal read service | Twelve content-bearing reads across all three admitted actors passed through the existing deployed `Messaging` service; three excluded-pair reads were denied. This uses the Api managed identity and approved actor context, not browser authentication |
| Bounded refresh transport | One explicit refresh of an already complete approved view was accepted through Standard and completed by the normal Control worker in about 2.5 seconds, with zero retry attempts. This proves Api-to-Control transport and checkpoint completion, not a new full Teams history hydration |
| Local compatibility | All 76 targeted transport, notification, read-refresh and outbox tests passed; affected PowerShell safety tests and Bicep compilations passed |
| Future deployment inputs | Private service-target and both existing Development platform parameter files pin Standard. The image-only release guard now requires the exact selected endpoint. GitHub reports zero configured environments and zero repository variables, so no existing protected target input was changed |
| Approved Premium retirement | Reconfirmed all four old queues empty and no active app/job or private provisioning input targeting Premium. Deleted only `teamsrefdev-unc3jasrpkgbo-bus` and `teamsrefdev-unc3jasrpkgbo-bus-pe`; verified both absent, with all four unrelated private endpoints and five shared private DNS zones preserved |
| Post-retirement acceptance | All five exact revisions remained healthy with unchanged preserved configuration; Standard security, all three queue contracts and seven exact queue grants passed. Public checks, twelve content-bearing reads and three excluded-pair denials passed again; a new Standard refresh completed in about 2.7 seconds with zero retry attempts |

Diagnostics used execution-only overrides on the existing manual job with
automatic retries disabled and its already attached identities. The read probe
loaded the current deployed Host/Core/Infrastructure assemblies without rebuilding
or replacing them. Persistent job configuration was verified unchanged by these
executions. Recovery snapshots, exact revision/image identities, cutover journal,
body-free read results and refresh receipts remain private outside the repository.

No signed-in browser journey, Teams message creation, fresh rich-notification
round trip, end-to-end Web PubSub delivery test, cache cleanup, Git commit or Git
push was performed. Full notification/projection/delivery acceptance is not
claimed. All three new queues were empty after the public check, and sampled
runtime logs across all five roles showed no Service Bus authorization, oversize
or transport-failure indicators. These bounded checks are not a load/SLA test.

A follow-up on 2026-10-26 at approximately 13:18 local time reconfirmed all five
current Standard revisions healthy, the diagnostic-job endpoint and passing
public checks, but found **one dead-lettered `graph-notifications` message**.
A non-consuming Projector-identity peek reported `projection_failed` for a
message enqueued at 12:28 local time. Its underlying application error has not
been isolated; this does not establish a Standard transport or size failure.
No message was consumed, purged or replayed by the inspection. Notification
processing therefore was not clean end-to-end acceptance.

The subsequent read-only inspection identified that item as a
`reauthorizationRequired` lifecycle event, with no encrypted message content.
At approximately 13:28, the existing worker's normal retention policy durably
scheduled scope repairs and removed the expired dead letter; this was not a
manual purge or replay. All three Standard queues were empty afterward. Control
inspection confirmed all five recovery signals handled and all five subscriptions
registered with future expirations. Four resource views were coverage-ready; one
still had a progressing repair deferred by `graph_429`. This does not establish
completed source reconciliation.

The preceding 24-hour logs contained seven notification application failures
before the cutover and six afterward, all recorded only as `AppError`; the exact
historical application code is not available. The error class therefore predates
Standard, but its precise root cause is not claimed to be resolved. Standard
metrics from 11:00 to 12:30 UTC reported incoming/outgoing message activity and
zero `UserErrors`, `ServerErrors` and `ThrottledRequests`. Message operations
include retries and dead-letter handling, not unique end-to-end deliveries.
The final follow-up again passed public checks and all five preserved workload
contracts. These observations support the infrastructure cutover while retaining
the separate source-health and signed-in/end-to-end acceptance boundaries.

Standard's 256 KB encoded message/batch ceiling is an accepted PoC limitation:
individual oversized notifications fail durable acceptance explicitly; no payload
offloading or truncation was introduced. The approximately $667/month retail
difference remains an estimate before Standard operation charges; actual billing
was not retrieved. Premium namespace deletion completed at approximately 13:45
local time, removing the resource responsible for its ongoing fixed charge;
billing totals and proration have not been measured. The old-endpoint rollback
option no longer exists. Recovery snapshots and retirement evidence remain
private; shared DNS zones, unrelated endpoints and application state were retained.

## Chat message presentation release (2026-10-23)

**Deployed to all five existing Development roles.** Ordinary text and supported
formatted messages no longer show the app-added **Open in Teams** link;
attachments and embedded content retain it. **Concurrent messages** defaults to
**1**, independently of the unchanged default message count of **30**.

ACR `dt19` built the uncommitted workspace based on
`07fe4cecd16c66a050d0cf96233a5fa00f2bcd09`, with workspace SHA-256
`1be616f525b7d75cda2f63a7f2e1199bb9e7511ca8d8849dd737a4ddc07e0437`,
into immutable image
`sha256:ef6e8f74694dd1dd76ea4d0080518209f6a756894554c28fcf0de622e4d4e6ae`.
The workspace matched that source during the build and public acceptance,
before this evidence-only documentation update.

The guarded image-only rollout verified exact healthy Api, Receiver, Projector,
Delivery and Control revisions, preserving configuration, identities, grants,
networking, scale, admission and data. Public readiness, anonymous API denial and
the canonical Entra redirect passed. Public JavaScript and stylesheet bytes match
the verified local production build. All 126 frontend tests, production
build/type-check and offline deployment safety contracts passed.

No signed-in journey, live Teams send, cache cleanup, Git commit, Git push or
GitHub Actions dispatch was performed. Existing source-health and authenticated
acceptance boundaries remain separate. Exact revisions, recovery identities,
source manifest and release proofs remain private outside the repository.

## Nonblocking cache cleanup release (2026-10-23)

**Deployed to all five existing Development roles; normal cache-read acceptance
passed, including reads through the existing unfinished cleanup.** Strict source
health and a signed-in new cleanup cycle remain separate acceptance boundaries.
ACR `dt16` built the uncommitted workspace based on
`58bf22708e216a8517ba69869223e9d1b529233b`, not that unchanged commit alone.
The recorded workspace SHA-256 is
`d65aca20d8e107147f448f6277e2ed77ee49a2a16adbf72a4b30ac0ddabbbada`;
the immutable image digest is
`sha256:e0964477a7ffd7e961deb4bdd9a7e88e4bc9fadcc7a7be203c390ee047e4fbc2`.
The workspace matched the recorded build source during the build and at
post-deployment acceptance, before this evidence-only documentation update.

The image separates logical cache invalidation from bounded physical deletion.
Unfinished cleanup no longer locks reads, Teams refill, repair or coverage writes.
Refill receives a current projection revision, and completing deletion does not
invalidate it again. Existing unfinished cleanup is recognized without a bulk
data migration.

| Evidence | Result / boundary |
| --- | --- |
| Guarded image-only release | Exact healthy Api, Receiver, Projector, Delivery and Control revisions verified; configuration, identities, grants, networking, scale, admission and approved scope preserved |
| Public application | Readiness, anonymous API denial and canonical Entra redirect passed; Cache Diagnostics denies anonymous access with `401` |
| Served frontend | Public JavaScript and stylesheet are byte-identical to the verified production build |
| Installed-Edge public check | Real deployed Microsoft sign-in screen rendered without page errors; no credentials entered or session fabricated |
| Local regressions | 334 backend passes and one explicit real-Cosmos opt-in skip; all 119 frontend tests, production build/type-check and five compiled installed-Edge Cache Diagnostics scenarios passed, including unfinished Cosmos cleanup |
| Offline release safety | Deployment contract, Development deployment and five-role service release suites passed |
| Real normal read service | 24 content-bearing reads across three admitted actors passed through the unchanged deployed `Messaging` service; three excluded-pair reads were denied |
| Real cache-aside evidence | Five Redis misses fell back to five Cosmos content queries and five successful Redis writes; nineteen reads hit Redis |
| Existing unfinished cleanup | Four reads returned 20 messages each while the original cleanup and invalidation cutoff were present and source coverage was false. The first observed Redis miss queried Cosmos and refilled Redis; the following read hit Redis. No cleanup was resumed or started |
| Read-only Control inspections | Both samples verified expected current memberships and all five exact remote rich subscription bindings; the persistent manual job definition was unchanged |
| Strict Api `ProjectionCheck` | Failed its completed-source-reconciliation precondition; this is not a passing projected-read acceptance result |
| Bounded recovery observation | Four of five resources remained coverage-ready in both samples. The same direct chat retained a reauthorization reconciliation gap and `graph_429` repair evidence; its repair advanced but was not complete. The follow-up did not rerun projection acceptance while coverage remained incomplete |
| Signed-in cleanup/refill acceptance | Not performed: scoped destructive-cleanup approval and normal authenticated user participation were unavailable |
| Final workload recheck | All five original `dt16` revisions remained exact, active and healthy after the read-service probe |

The read-service check used a diagnostic-only ACR `dt18` image based on the exact
immutable `dt16` application image. Only the small probe entry assembly and its
runtime/dependency manifests were added; the deployed Host, Core and Infrastructure
assemblies were copied unchanged, not rebuilt. Their loaded hashes and body-free
read evidence were recorded. This image ran once under the already attached Api
identity on the existing manual job; it was never deployed to the five workloads.
The persistent job definition was verified unchanged. A preceding diagnostic
`dt17` build failed because it selected the repository Dockerfile instead of the
probe Dockerfile; the corrected-context build passed. The failed build is not
application release evidence.

The probe waited for normal page expiry rather than evicting any cache. It also
observed best-effort background refresh requests for the incomplete source view;
it does not claim that an initially empty Cosmos view was hydrated from Teams
during this probe. The existing unfinished cleanup provided a real, non-destructive
acceptance case, so no new live cache deletion was necessary to verify the reported
read blocker.

The final frontend run used the direct Vitest runner with one worker and unchanged
test timeouts/assertions, after initial parallel invocations timed out. The final
browser rerun used a private runner override with an explicit installed Vite path
and a longer preview-startup allowance; test assertions were unchanged. Earlier
failed invocations are not represented as passing runs.

Diagnostics used one-off execution-template overrides on the existing manual
job with already attached role identities and automatic job retries disabled.
The failed projection attempt and both inspection samples are retained privately;
no manual repair, subscription reauthorization, cache cleanup, Teams message
write, permission/configuration change, Git commit, Git push or GitHub Actions
deployment dispatch was performed. Generated execution payloads were removed
after recording body-free evidence. Exact revisions, prior-image recovery
identities, source manifest, build/release logs and validation proofs remain
private outside the repository.

Normal read-service acceptance establishes that the existing unfinished cleanup
does not block authorized content or Redis/Cosmos fallback. It does not close
the separate strict source-health or signed-in new cleanup/full source-refill
acceptance gates, and it is Api-identity evidence rather than browser authentication.

## Cache Diagnostics release (2026-10-23)

**Deployed to the five existing Development roles; no live cache cleanup was
performed.** Source commit `58bf22708e216a8517ba69869223e9d1b529233b` was built
as ACR `dt15`, digest
`sha256:a0aec6f6ab7d85e21b7f655a3364daae11c6365b82cea5adea667f802f54b768`.
The image-only release verified exact healthy Api, Receiver, Projector, Delivery
and Control revisions while preserving configuration, identities, grants,
networking, scale, admission and approved user/resource scope.

Public readiness, anonymous API denial and the canonical Entra redirect passed.
The publicly served script and stylesheet are byte-identical to the verified
production build, including the Cache Diagnostics popup, cleanup selections,
observation history and explicit reload/validation controls. Anonymous access
to the cache diagnostics endpoint returns `401`.

Release-source CI, the registry build and offline deployment safety contracts
passed. Signed-in inspection, actual Redis Lua execution and destructive
Cosmos/Redis cleanup acceptance remain separate, unperformed live checks.
No cache cleanup requests, Teams message writes, permission changes, Git commits,
Git pushes or GitHub Actions deployment dispatch occurred in this release.
Exact revisions, prior-image recovery identities, build/release logs and the
body-free verification proof remain private outside the repository.

## Prior direct Graph concurrency and reactive retries (2026-10-22)

**Deployed and validated within the agreed release scope. Real Graph send
capacity was not measured.**
ACR `dt14`, digest
`sha256:6c1918ec94775aee5e0d40b7c06f70d8c6394e743923bd2d4e898918439fc388`,
then ran on all five existing Api/Receiver/Projector/Delivery/Control workloads.
The release verified each exact healthy revision and preserved configuration,
identities, grants, networking, scale, admission and approved user/resource scope.
This was an image-only release, not a new platform or identity deployment.

Initial sends now start without browser pacing or application-created send
budgets. Each Graph operation independently uses the bounded reactive policy:
three additional retries, a 120-second total deadline and 20-second attempts
by default. Message writes retry only explicit Graph `429` responses, honoring
`Retry-After`; ambiguous writes are never automatically replayed. Safe reads
also handle the selected transient failures described in the runtime guide.

| Evidence | Result / boundary |
| --- | --- |
| Five-role release and public smoke | Exact immutable image and healthy ready revisions verified; readiness, anonymous API denial and canonical Entra redirect passed |
| Served frontend | Public entry bundle is byte-identical to the verified local production build; immediate-concurrency controls and initial Graph-response counters are present |
| Installed-Edge anonymous check | Development sign-in screen rendered with the normal Microsoft sign-in link; no credential entry or fabricated session |
| Read-only Api `ProjectionCheck` | Passed for three actors, twelve snapshots, three excluded-pair denials and six existing direct-chat opens; source writes were zero |
| Read-only Control inspections | All five remote rich subscriptions matched resource, callbacks and client state, with expected current memberships; the follow-up observed all five resources coverage-ready |
| Observed background recovery | The first inspection recorded a recent `graph_429` repair failure and one reauthorization coverage gap; the follow-up showed that failure cleared and coverage restored. Runnable reconciliation/thread work remained; this is not a continuous source-health or retry-timing certification |
| Local regressions before release | 299 backend passes with one explicit real-Cosmos opt-in skip, 106 frontend passes, production build/type-check and five installed-Edge concurrency scenarios passed, including 30 submissions before confirmation |
| Final controlled recheck | 19 backend burst/retry tests and 26 frontend batch/queue tests passed after deployment |
| Real two-user, 60-message Teams burst | Separately scoped and not run: explicit approval for the real message writes and normal authenticated users were unavailable. Controlled two-user dispatch tests do not establish actual Graph send capacity |

The diagnostics used one-off execution-template overrides on the existing manual
job, with already attached role identities and automatic job retries disabled.
Each execution succeeded and the persistent job definition was verified unchanged.
No Graph message was sent, source data reset, access granted, commit created or
Git push performed. Exact revisions, body-free proofs and resolved settings remain
private outside the repository. Earlier migration, signed-in journey and
continuous recovery acceptance gates are not closed by this release.

The scoped queue-access preflight was also corrected for Azure CLI compatibility:
it retains the exact queue scope and inherited assignments without the
incompatible subscription-wide `--all` option. The mocked service-release
regressions passed with a guard for that invocation.

## Prior combined service rollout (2026-10-22)

**Historical dt13 baseline; migration acceptance/cleanup remained pending.**
ACR `dt13`, digest
`sha256:e8bb2357c55871fa16193892ed01b3e862710abccb793400c5df0ff2a216a57b`,
then ran on the five existing isolated Api/Receiver/Projector/Delivery/Control
workloads with exact `readConsistency: "Session"`, synchronization and admission
enabled. The matching bundled frontend is deployed. The three users and five
source resources, account policy, network, scale, identities and Graph grants
were preserved.

The existing namespace now contains `graph-read-refresh` with five-minute TTL,
one-minute lock/duplicate window, five deliveries and expiry dead-lettering.
Only the exact queue-scoped Api Sender and Control Receiver grants were added;
real queue-access startup checks and all five exact healthy revisions passed.
The account's eleven normal Cosmos assignments and four `readCurrentV1`
procedures remain intact.

| Evidence | Result / boundary |
| --- | --- |
| Real isolated Cosmos `SessionCheck` | Independent-client content convergence, current revocation and epoch fencing passed; eight tracked synthetic documents cleaned; exact temporary Api grant removed and original manual job restored |
| Strict source-health `ProjectionCheck` | Passed for three actors, twelve snapshots, three excluded-pair denials and six existing direct-chat opens; this is Api-identity evidence, not browser authentication |
| Current Control inspection | All five remote rich subscriptions match, with current expected member counts and progressing reconciliation; intermittent DM coverage gaps remain observable and are not a continuous source-health pass |
| Application authentication beyond the old user-context limit | Five matched renewed subscriptions and current memberships observed 24.24 hours after original Control creation; seven sync application read grants, zero Web application grants and Control-only federation verified. This is not a new continuous source-health soak |
| Public deployment smoke | Bundled frontend/readiness, anonymous API denial and canonical Entra redirect passed |
| Signed-in warm/cold/older reads | Pending normal user-assisted sign-in; the user was unavailable. No fabricated session or password/TOTP automation was used |
| Cold queue consumption and bounded recovery | Startup permissions are verified; full signed-in on-demand-path acceptance remains pending |
| Operational alerts | Explicitly deferred by the user to future work; no recipients, routing or test notifications configured |

Combined offline evidence is **280 backend passes with one explicit real-Cosmos
opt-in skip**, **99 frontend**, **19 authentication-helper** and **35 installed-Edge
journeys**, plus script/protocol checks and compiled templates. The initial
parallel `Validate.ps1 -Browser` invocation timed out in three frontend files and
cascaded to failures; its logs are retained, not reported as a passing invocation.
The same frontend assertions/timeouts passed with one worker, and the remaining
original checks passed separately. The integration includes the equal-timestamp
older-page hydration regression/fix.

Admission uses current authority, exact subscription bindings and operational
progress, not blanket global coverage. Strict source-health acceptance remains
separate: available history does not certify reconciliation or bounded recovery.
Missed source edits/deletions can remain until reconciliation/retention. Native
current-guard RU/latency, full read-path and unattended/recovery acceptance are
not inherited from dt12 or the earlier failed/paused observers.

Keep dt11 with its matching SessionPrimary settings as historical rollback
evidence; dt12 is superseded Session-only evidence. Guarded retained-user runtime
and obsolete validation-grant retirement await remaining acceptance, followed by
the cleanup image/retest. No real Teams messages, source reset, commit or Git push
were performed. Resolved settings, grant IDs, exact revisions and operation logs
remain private outside the repository.

## Prior availability-first source increment

**Historical source-only handoff: implemented and verified locally, not deployed by that increment.**
[ADR 0013](adr/0013-availability-first-reads-and-bounded-hydration.md) removes the
source-coverage display gate, retains valid authorized history during operational
failures and adds typed cold loading through durable, bounded Control hydration.
Source freshness remains distinct from availability: missed edits/deletions may
remain until reconciliation or seven-day expiry. Authority/history/parent/epoch
checks, transactional merge/outbox and uncertain-send guarantees remain strict.

The final post-cleanup `Validate.ps1 -Browser` run passed **279 backend tests**,
**99 frontend tests**, **19 authentication-helper tests**, **nine Azure protocol
contracts**, all mocked management/release suites and **35 installed-Edge
journeys**. The real-Cosmos opt-in test was explicitly skipped. The frontend
build/type-check passed. `live.bicep`, `synthetic-live.bicep`,
`service-dev-access.bicep` and `service-dev.bicep` compiled with available Bicep
0.46.1, including the new refresh queue and monitoring modules; CI remains pinned
to 0.48.1 and was not dispatched. These are controlled offline results,
not real queue/Graph, deployment, alert-routing or capacity acceptance.

Implementation and cleanup replaced obsolete coverage-denial expectations and
retained coverage for initialization/source-health evidence. The post-test pass
restored bounded lag jitter and tightened critical-guard failure classification. Regression coverage
also checks a restriction observed during a cache read against its returned
access proof, and a parent appearing after an earlier source `404` against
false-empty reply evidence. Historical ADRs, resource names and independent
migration changes remain intact.

No image was built/pushed to ACR, no workload was deployed and no queue/grant,
account/network, Graph permission, Teams content or admission setting was changed
by this increment. The live baseline at that handoff was `dt11`, digest
`sha256:3f76605f3723be02c5c5083a0b91be21e7655110ab4315391dc9c507c3e8f901`,
with SessionPrimary settings. Prepared `dt12`, digest
`sha256:fc3e2373cec2c7c6b70eedfb982921029dff0733d389c6bbe9ef5e8251cec6ab`,
predates this increment and is not its release package. A rebuilt coordinated
image, reviewed Session settings, separately approved `graph-read-refresh` queue
and exact API Sender/Control Receiver grants and real-service acceptance were
subsequent release requirements. The combined rollout and explicit support-alert
deferral are recorded above. The separate migration's unattended,
signed-in, isolated-Cosmos and legacy-cleanup gates are not certified by these
tests. See the [release/support runbook](live-runtime.md#availability-first-reads-and-hydration).

## Prior Session consistency source increment

**Session consistency source update:** [ADR 0012](adr/0012-session-content-reads-and-revision-convergence.md)
replaces the historical profiles below with explicit Session content reads and
targeted current reads. The new image/settings have not been deployed by this
work; earlier Azure evidence is not acceptance for them. Use the
[migration runbook](live-runtime.md#session-configuration-migration) and
[current storage checks](testing.md#session-storage-checks). Historical counts,
digests and failed observations below are retained unchanged.

The Session source regression run passed **247 backend tests**, **86 frontend
tests**, **34 installed-Edge browser journeys** and the existing script,
deployment-contract and authentication-helper suites. The real-Cosmos opt-in
test was explicitly skipped. The four consistency-related Bicep entry points
compiled; the synthetic template retains its existing nullable-key warning.
These are local/controlled-service results, not a new Azure acceptance result.

**Live-only source migration:** Simulator and legacy GraphPilot executables, local persona login, fixture seed/reset and pilot polling have been removed. Offline tests now inject dependencies only from test assemblies/runners. This migration is hosted by the image-only Development release recorded below; existing Live configuration, identity, consent, services and sizing were preserved. GitHub Actions was not dispatched by this release.

**Historical outcome (2026-10-20 UTC): chat-name and synchronization-recovery changes were deployed to the existing two Azure apps. Read-only inspection confirmed coverage, current exact membership and matching rich subscriptions for all five approved resources. No Teams messages were sent during this release.**

## Chat names and synchronization recovery (2026-10-20)

ACR run `dtp` produced immutable digest `sha256:07178032def7abecf0aed0d357674d005bbf448f7d5853e54df9904f3c55b4c4`. The accepted API revision is `--dev-035069909cbc46d9`; the worker revision is `--dev-87e6468efa994bd8`. `Deploy-Dev.ps1` verified both exact healthy revisions, complete preserved configuration fingerprints and public frontend/readiness/Entra/anonymous-denial contracts. Resolved settings and operational evidence remain private outside the repository.

One-to-one catalog titles now use the other participant's current name, selected by identity rather than editing a combined title. Repair and authorization/subscription maintenance run independently, repair work is bounded per resource, completed coverage writes can recover without fabricated proof renewal, and a root repair cannot restart while its thread repairs remain pending. A reauthorization `404` for a confirmed-missing subscription now enters checkpointed replacement recovery; other failures and uncertain creation outcomes do not cause replacement POST replay.

| Check | Observed result |
| --- | --- |
| Backend regressions | **165 passed, one explicit real-Cosmos opt-in skip**; includes missing-subscription recovery, uncertain replacement outcomes, lease/generation safety and pending-thread sequencing |
| Release guards | **Passed**: 35 preflight refusal cases, 22 failed/partial rollout cases, exact configuration preservation, date/computed-path compatibility, healthy max-scale acceptance and read-only request-field handling |
| Actual Azure synchronization | **5/5** resources had verified coverage and fresh membership; all five remote rich subscriptions matched resource, callback URLs and client state at **2026-10-20 09:34 UTC** |
| Authenticated browser recheck | **Not completed**: Chromium's public Entra navigation timed out before credential entry; installed Edge completed password/TOTP and the app callback but disconnected before session/read verification. No certificate/security bypass or credential retry after rejection was performed. |

The explicit three-user delegated profile still requires fresh source sign-ins when its original bounded sessions expire; this release does not extend their eight-hour limit or introduce unattended credentials. No new event-delivery/send acceptance, production scale or recovery certification is claimed.

**Historical event-path outcome (2026-10-19 UTC): the bounded Azure Live profile was verified end to end in Sweden Central. Real Graph chat/channel changes reached two open browsers through signed encrypted notifications, queues, projection/outbox and Web PubSub. Normal application chat/reply sends were Graph-confirmed, persisted exactly once and remained readable after an API revision restart.**

## Bounded Azure event-path rollout (2026-10-19)

The existing API now hosts `Api`/`Receiver`/`Control`, and the existing worker hosts `Projector`/`Delivery`, in explicit `DelegatedSynthetic` mode. The scope is Alex, Sam and Jordan, their dedicated group chat/channel and three approved pairs. Existing delegated consent/federation, service tiers, serverless Session Cosmos and both apps' 0.5 CPU/1 GiB/one-replica sizing were preserved. This is not an organizational policy/DLP authority or production scale/recovery result.

The immutable deployed image is ACR `dth`, digest `sha256:863e826aee2fa0a22201a011006faeede9b10cf392abc35e2e51b7fa85a5b9c1`; final API revision `--0000020`, worker `--0000014`. Revision `--0000020` removes temporary verbose authentication diagnostics from the same tested image. Resolved inputs, original resource/job snapshots, public-key proofs, post-authentication screenshots and body-free operation evidence remain private outside the repository. The manual operation job is restored after each execution.

| Check | Observed result |
| --- | --- |
| Real SessionPrimary | Independent in-network SDK clients passed current projection, revocation and epoch-fence checks; eight tracked synthetic documents were cleaned up. The local opt-in test remains explicitly skipped rather than claiming it ran locally. |
| Notification key | Version-pinned remote RSA certificate signing/public-certificate OAEP round-trip passed; no private key was exported. |
| Source/user scope | All three users passed fresh password/TOTP and real Live WebSockets. Each saw exactly four authorized conversations/two contacts; unassigned pair reads returned 404. All five resources had coverage, current exact three shared/two pair members and real rich subscriptions with matching resources, callbacks and client state. Pair preparation sent zero messages. |
| Local regressions | 149 backend passes/one explicit opt-in skip; 45 React tests; 25 installed-Edge browser journeys; 13 authentication-helper contracts; eight primary-read/WAF contracts. Identity/deployment safety suites and infrastructure compilation also passed. |
| Azure edge integration | Real WebSockets exposed WAF RFI false positives on standard CloudEvent `claims.iss`, `claims.aud` and forwarded `headers.Origin` URL values. Only those three JSON metadata fields are excluded from enabled rule `931130`; prevention, other rules/fields and callback JWT authentication remain enabled. No public REST/Trace, access keys or anonymous admission were enabled. |
| Graph publisher admission | The assignment-required web app initially received callbacks without `validationTokens`. One Default Access assignment admits only Microsoft's existing Change Tracking principal to this app. User-assignment requirements, all three user grants and federation remained unchanged; identity reconciliation subsequently reported zero drift. No new Microsoft Graph message-reading permission was added. |
| Subscription lifecycle | Actual lifecycle envelopes omitted `tenantId`; current tenant-scoped subscription lookup plus matching secret state now binds them, and supplied tenants must match. Successful HTTP 200 empty reauthorization responses are handled only for that control endpoint. Existing subscriptions were reauthorized and gap repair scheduled without replaying sends. |
| Event delivery | **Passed**, run `19e0b3fd-35e5-4592-b82e-91aa54d23988`: two original external Graph messages had exact authenticated receipts/source versions/published revisions and appeared through browser change-feed IDs. Observed Graph-creation-to-browser-delta times were **2.114 s for chat** and **2.250 s for channel**, a bounded functional observation rather than an SLO certification. |
| Application writes | **Passed**: one normal chat and one channel-thread reply were confirmed by Graph. The runner admitted exactly those two browser send POSTs, with no resend. A later fresh session after API restart read both IDs exactly once. |
| Browser/privacy | **Passed**: no catalog reload during the measured **31,026 ms** window, no business local/session storage, desktop chat/mobile thread screenshots inspected, no fabricated poll transport or fallback-repair evidence. Preparing projections recover through bounded safe GETs. |
| Operation evidence recovery | **Passed with actual failure injection**: when the live job log stream was unavailable, the runner recovered the exact succeeded execution's body-free proof from persistent Azure logs. No Graph POST was replayed; the original job definition was restored. |

Follow the [bounded Azure runbook](live-runtime.md#bounded-existing-platform-azure-validation). The remainder of this report preserves earlier simulator, GraphPilot and source-only milestones; old revisions and "not deployed" statements below describe those historical increments, not the current runtime.

## New chat source integration (2026-10-19)

The executable frontend now adopts `prototypes\05-new-chat.html` with an explicit backend-capability boundary. It searches authorized, loaded chat titles, opens the existing chat/composer without a business POST, preserves independent in-page drafts, supports paged discovery, and distinguishes loading, empty, no-match and unavailable states. It does not invent profile/email information or create new chats. GraphPilot offers its dedicated approved chat without implying a one-to-one recipient.

| Local check | Result | Boundary |
| --- | --- | --- |
| React component regressions | **13 passed** | Includes catalog continuation/recovery, draft reuse, authorization expiry, honest capability labels and the retained 30-second pilot poll |
| Browser journeys in installed Edge | **19 passed** | Actual local simulator read/send/post/reply, uncertainty across chat switching without duplicate POSTs, rejected/offline sends, authorized history, offboarding, search recovery, and mocked GraphPilot metadata |
| Responsive/keyboard/theme checks | **Passed** | 1280px, 390px and 320px in light/dark; focus entry/return, Escape, Shift+Enter, IME non-submission, reduced motion, no horizontal overflow, zero business browser storage and no page-script errors |
| Sampled rendered text contrast | **At least 4.5:1** | Actual computed colors in the discovery heading, copy, input, result identities/actions and notes in both themes; not a full accessibility certification |
| Frontend production build/type-check | **Passed** | Vite build plus strict TypeScript; authentication-runner type-check also passed |
| Backend contracts | **92 passed** | Debug xUnit suite; no backend/API contract changes in the UI integration |
| Authentication-runner unit checks | **12 passed** | Controlled local checks only; no credentials loaded or live authentication performed |
| Mechanical UI detector | **No deterministic findings** | Human visual comparison also performed against the served local reference |

The live-validation helper now scopes conversation selection to navigation so the new search results cannot create ambiguous locators. This source UI is **not deployed** by this work. No real Graph write, cloud/identity mutation, consent, tenant setting, production-gate activation, commit or push was performed. Local simulator and mocked metadata checks do not replace the separate real-Teams evidence above.

## People and one-to-one follow-up (2026-10-19)

Jordan's inability to find Alex exposed a real missing capability: existing-chat title search was not people search. The corrected flow has `/api/people` backed by the approved chat's actual membership and `/api/chats/direct` for exact approved pairs. It uses existing `Chat.Read` plus separately approved `Chat.Create` for the three users only. No organization-wide directory permission or arbitrary recipient was introduced.

Local checks passed: **106 backend tests**, **17 React component tests**, **22 browser scenarios**, **13 authentication-helper tests**, frontend build/type-check, authentication-helper type-check, script parsing and the identity/API-only deployment guard suites. The new mocked Graph browser cases prove Jordan can find Alex by name/email without an existing chat title and open the real composer contract on desktop/mobile; these are distinct from the live proof below.

The direct-chat source was built as ACR `dt6` and deployed by the API-only path to revision `--0000006`, image digest `sha256:3288a008ecbeda7a63e72d5327cd58fc47a924f0e2ee264236cabf792356a85a`. It includes uncommitted follow-up source changes based on `d0b6f45`; this work did not commit or push them. The three approved `Principal` grants reconciled with zero drift after the user refreshed an authorized administrator session. No role assignment was made by the implementation. The original test resources, runtime identities, network, sizing and scaling were preserved.

With separate explicit permission for one test message, a fresh real Jordan password/TOTP session passed all of: finding Alex by name and email, opening the expected one-to-one pair, selection sending nothing, one `UI validation: Jordan to Alex` message, definitive Graph creation, exact Graph-ID/author/body read-back, and reopening the chat with exactly one message. The runner blocked other business writes and admitted exactly one creation/open POST and one message POST. It did not run the separate three-user group/channel suite or send to Sam. No credential-entry/live-authentication screenshot, token or cookie export was created.

Body-free confirmation metadata is private under the `graph-pilot-direct` validation directory. This demonstrates the reported real Jordan-to-Alex flow, not production G0-G7 acceptance or live write testing of all three pair combinations.

## End-state source increment (2026-10-19, not deployed)

This increment implements targeted/coalesced push hints, authorized message deltas, minimum-revision snapshots, renewed command windows, preserved loaded history, reset/reconnect/foreground catch-up and explicit synchronization failure. Push modes no longer perform the pilot's unconditional 30-second refresh. GraphPilot keeps its negotiated polling contract; the hosted app and its mode were not changed.

Production discovery can open approved existing chat pairs only after both individual grants and fresh, source-verified exact-pair/type authority pass. Configuration cannot assert that a group chat is private. The new authority evidence stays in the separate authority container; the API cannot write it. Missing authority, automatic new-resource onboarding and broader discovery remain explicit blockers rather than fabricated success.

Supported rich formatting uses DOMPurify with a narrow tag/attribute/HTTPS-link allowlist. Plain-text sends remain literal; attachments and embedded media stay in Teams with explicit notices/source links. Large loaded histories use measured variable-height virtualization, stable message keys and scroll anchoring.

| Local check | Result | Evidence boundary |
| --- | --- | --- |
| Release backend suite | **128 passed** | Includes complete controlled intake/decryption/projection/outbox/push/catch-up journeys for chats, channel posts and replies, plus exact-pair policy HTTP/lease contracts; not actual Azure/Graph integration |
| React/hook/component suite | **42 passed** | Delta/reset/history/denial states, transport negotiation/coalescing/cleanup, mocked Web PubSub SDK, approved-contact UI and rich-content safety |
| Installed Edge browser suite | **25 passed** | Actual local SSE/changes path, safe sends/uncertainty/offboarding, keyboard/themes/mobile, and controlled-API 80-message virtualization/sanitizer cases |
| Frontend production build and TypeScript | **Passed** | Runtime dependency lock updated for DOMPurify and TanStack Virtual |
| Authentication helper type/unit checks | **13 passed** | Controlled local identity flows only; no `.env` credential loading or real sign-in |
| Management/deployment checks | **Passed** | Script parsing plus offline identity, live-deployment, public-contract and GraphPilot API-only guards; example discovery defaults remain closed |
| Rendered large-history check | **Passed** | Desktop/mobile screenshots inspected; last message remains in viewport after variable-height measurement, with bounded mounted rows and no CSP/script errors |
| Patch formatting | **Passed** | CRLF-aware whitespace check matches the repository's committed Windows line endings |

The task-owned browser server used localhost:5429 and stopped after validation. Existing uncommitted changes were preserved; no commit, push, image release, new grant, billable provisioning or business-data admission was performed. The [end-state status](live-integration-plan.md#end-state-app-integration-follow-up-2026-10-19) records outstanding policy/privacy/platform inputs, automatic production onboarding and real Graph/Azure/scale/regional recovery acceptance. These local results do not complete G0-G7.

### Read-only production prerequisite check

A subsequent read-only Azure inspection on 2026-10-19 confirmed the remaining rollout blockers against the existing Sweden project rather than relying only on the design documents:

| Prerequisite | Observed result |
| --- | --- |
| Selected Azure subscription | Existing project subscription is Enabled; CLI context was not changed |
| Cosmos consistency | Existing private Cosmos account is serverless with **Session** consistency. This conflicted with the original Strong-only implementation, not with pub/sub itself; the SessionPrimary correction below supports this account type without upgrading it |
| Deployed workloads | Existing API revision `--0000006` and simulator/pilot worker revision `--0000002`; no five-role live topology |
| API mode and live bindings | `App__GraphMode=GraphPilot`; no `Live__` binding names are configured |
| Repository production inputs | Only `live.parameters.example.json` is present under `infra\parameters`; no resolved production parameter file was supplied |

Only account/resource metadata and runtime mode/binding names were reported; credential values and complete environment payloads were not printed. No cloud resource, runtime mode, permission, processing/storage boundary or admission flag was changed. The requested rollout-scope decision was unavailable, so no approval was inferred. These observations identify integration/configuration work; they do not prove that a replacement Cosmos account or five separate processes are necessary for real-time updates. A missing parameter file is an engineering deliverable, not an external blocker by itself. The remaining copied-data authority must be supported for the selected scope rather than fabricated from simulator decisions.

### Session-consistency correction (2026-10-19, source only)

This subsection records the earlier SessionPrimary increment, now superseded by
ADR 0012. `CosmosSessionReadTests` is the current opt-in entry point;
the old test name/instructions below are historical, not current commands.

The original Strong-only guard has been replaced by two explicit supported profiles. `Strong` preserves backward compatibility. `SessionPrimary` uses a Session SDK client and the canonical bounded read-only `readCurrentV1` procedure for authoritative point reads in projection, authority and control containers. Microsoft documents primary-replica/strong read semantics for stored procedures; the SDK captures their partition session tokens for following queries. There is no shared-token parsing/cache and no fallback to ordinary stale reads.

Both runtime metadata validation and deployment preflight accept a single-region Session account under SessionPrimary, refuse unsupported regions/consistency modes, and leave account configuration unchanged. Bicep provisions the procedure before workloads and defines a reader with stored-procedure execution but no item-write actions. The private live example now selects SessionPrimary. None of this source was deployed or used to process additional Teams content.

| Check | Result | Boundary |
| --- | --- | --- |
| Release backend suite | **139 passed, 1 explicitly skipped** | Includes primary result identity/schema/ETag/session checks and runtime storage-profile guards |
| Canonical procedure and permission contracts | **6 passed** | Executes the actual deployed JavaScript source against controlled server callbacks; includes absence/error/budget/identity behavior and reader-role boundaries |
| Deployment guards and script parser | **Passed** | SessionPrimary accepts a single-region Session preview without account changes; unsafe profile/topology/public-network combinations remain rejected |
| Live Bicep compilation and ARM inspection | **Passed** | Canonical procedure compiles; custom reader's compiled actions include execution but exclude item writes |
| Frontend production build/type-check | **Passed** | No UI/runtime contract regression |
| React/hook/component tests | **42 passed** | One-worker rerun avoided shared-host CPU contention after a parallel run hit a test timeout; no timeout was raised or assertion removed |
| Installed Edge browser journeys | **25 passed** | Existing real local SSE, safe-send, history, offboarding, rich-content and virtualization cases remain intact |
| Independent real Cosmos clients | **Unexecuted, explicit opt-in skip** | The dedicated synthetic target/procedure/data access were not supplied; no real replica/RU or capacity result is claimed |

The then-current `CosmosSessionPrimaryTests` provided the separately executable existing-account check with two Gateway/Session clients, no manually forwarded writer token, synthetic-only tracked writes/cleanup and no Graph calls. It checked current messages, source revocation, principal denial and epoch fencing. See the [current opt-in instructions](testing.md#session-storage-checks). The remaining real-service and rollout acceptance must not be confused with an intrinsic serverless limitation.

## Earlier deployment history

**Application:** https://teamsrefdev-unc3jasrpkgbo-web-htdnfyf3dbbjbca2.b02.azurefd.net

**2026-10-19 identity update:** Fresh unattended password/TOTP sign-ins are verified for Alex, Sam and Jordan with Chromium and expected Entra-backed `/api/session` responses. The source fixture dataset is now `split-view-v2` with these three personas; Jordan also covers the group-chat history cutoff. The deployment evidence below describes the earlier `split-view-v1` rollout and is not proof that the three-user image/data migration has been deployed. Final local credentials use `TEST_ALEX_*`, `TEST_SAM_*` and `TEST_JORDAN_*`; no credential values or session material belong in this report.

| Context | Value |
| --- | --- |
| Validation date | Local application: 2026-10-15; deployment retries and region wiring: 2026-10-16 |
| Source | Local implementation checks based on `fa09e7f`; deployment retry working tree based on `bb55dee`. This deployment work made no commits or pushes. |
| Environment | Windows, .NET SDK 10.0.402, Node 22.14.0, npm 10.9.2, installed Edge |
| Dataset | Source: `split-view-v2`, six conversations, three synthetic personas. Earlier hosted rollout: `split-view-v1`. |
| Azure target | Subscription `67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c`; resource group `rg-teams-reference-dev-swc`; region `swedencentral` |
| API image | `acrteamsrefdevunc3jasrpkgbo.azurecr.io/teams-reference@sha256:15c16ae26cc95df0cd21790bc2d6a05d31f086299e9d51b2e4fb8d04e370d37c` (ACR `dt3`) |
| Worker/seed image | Existing `dt2` image `sha256:119dcb11117bd12d0daf9c9c70435186ac423958f67ae7a4cdda2d0f13657905`; unaffected by the API-only callback fix |
| Deployment result | Baseline `teams-reference-release`: `Succeeded`; API-only callback correction rolled out to healthy active revision `teamsrefdev-unc3jasrpkgbo-api--0000003` |
| Authentication exercised | Local cookie/CSRF journeys; deployed canonical Entra redirect and actual first-login attempt reaching mandatory security enrollment. No completed deployed user session is claimed. |
| Persistence exercised | Real Azure Cosmos seeding/verification and Redis readiness through private endpoints; no claim that all hosted send transactions or failure cases have passed |

## Checks executed

| Check | Result | Evidence / boundary |
| --- | --- | --- |
| Backend/domain/API/Graph-transport contracts | **37 passed** | Includes a real middleware/MSAL code-redemption regression with a controlled identity transport, six invalid-origin cases, and previous HTTP/CSRF/CSP/send coverage |
| Canonical callback regression | **Passed** | Under internal HTTP/origin-host forwarding, both authorization and the captured token POST use the same configured public HTTPS `/auth/callback`; PKCE remains present |
| React component tests | **4 passed** | Unknown-send safety, simulator labels, and server-controlled local-login visibility |
| Local browser scenarios | **8 passed** | Running local Web App in Edge; not a hosted authenticated journey result |
| Deployed public smoke | **Passed** | Compiled UI/asset load, fixture and Redis readiness, anonymous API 401, disabled local sign-in, correct Entra tenant/client/callback redirect |
| Deployed browser rendering | **Passed** | Actual Sweden site, desktop light and mobile dark, no overflow, local-login option hidden, zero CSP violations |
| Origin and TLS boundary | **Passed** | Direct public Container Apps origin returns 403; HTTP public origin redirects to HTTPS with 307 |
| Hosted authenticated journeys | **Pending fresh sign-in after callback fix** | Consent is verified. The user's subsequent callback failed with `AADSTS500112`; the canonical URI fix is deployed and regression-tested, but a completed new user session/business journey remains unverified. |
| TypeScript and production frontend build | **Passed** | Vite build and strict type checking |
| Locked .NET restore and Release publish | **Passed** | Published frontend `wwwroot` assets verified present |
| Bicep / ARM | **Compiled and deployed** | Sweden platform and corrected workload rollout succeeded. This is not production capacity or SLO certification. |
| GitHub Actions | **Lint passed** | actionlint 1.7.7; remote workflow not executed |
| PowerShell management scripts | **Parse and 6 isolated checks passed** | Explicit subscription, checkpointing, Sweden region, required network feature, strict workload identities and early tenant mismatch denial |
| Sign-in consent helper | **5 isolated checks; user-approved grants verified** | Exactly four user-specific sign-in grants, no tenant-wide grant, and assignment restriction remains enabled. This is not a completed browser authorization test. |
| UI mechanical detector | **No deterministic findings** | Not an accessibility certification or performance result |
| Azure ARM preflight | **Passed in Sweden** | Redis B0 and Cosmos allocated successfully; earlier German capacity denials remain historical |
| OCI container build | **Succeeded** | Sweden ACR `dt1`, UI/CSP correction `dt2`, and canonical callback correction `dt3`. The latest fix was scoped to the API. |
| Real-time configuration | **Configured** | Protected managed-identity PubSub connect handler and API admission flag enabled; user connection/render journeys remain unverified until sign-in completes |
| Load testing / regional recovery | **Excluded by request** | No tooling, execution, capacity result, or load report |

Backend evidence covers command replay/payload conflicts, concurrent duplicate submission while the budget is occupied, conditional expiry of an undispatched claim, unknown receipt resolution, ownership checks, history/parent visibility, revocation/offboarding, equal-timestamp pagination, coverage gaps, tombstones/out-of-order changes, fixture reset epochs, serialized projection bounds, and live HTTP send outcome classification without POST retries.

The simulator restart test creates a new simulator instance against the same store. It does not claim that the local memory store survives a server-process restart or that Cosmos/broker crash recovery has been demonstrated.

Final browser validation exposed shared-actor rate-budget coupling between cases. The harness now waits for actual send responses, distinguishes confirmed content from optimistic rendering, and respects the unchanged 1.25-second sender budget between cases. The final complete eight-case run passed with no retries or limiter relaxation.

## Local browser journeys

1. Browse and page conversations, preserve an in-memory draft across navigation, send once, and sign out.
2. Navigate channel threads, send a reply, receive a queued external update, and render hostile markup as text rather than HTML.
3. Lose send confirmation, block automatic resend, check the original operation, and show exactly one created message.
4. Sign in as the restricted persona and deny both discovery and direct access to another conversation.
5. Offboard an already signed-in persona, deny its existing session, close its local real-time connection, and clear its open view.
6. Reject a send without creating a message; disable sending offline while retaining only the open-page draft; restore sending availability online.
7. Apply external edits/deletions through the inbox/worker/hint/refetch path.
8. Exercise desktop/mobile Split View navigation, themes and wrapping, and verify zero localStorage/sessionStorage entries for the test application.

The suite starts/stops its own localhost host. No long-lived dev server was intentionally left running. Local SSE exercises application hint/refetch logic, not Azure Web PubSub's protocol, identity, network rules, admission, or connection capacity.

## Hosted deployment evidence

The dedicated resource group contains the API and worker Container Apps, seed job, Cosmos DB, Managed Redis B0, Service Bus Premium, Web PubSub, Key Vault, ACR, telemetry, VNet and private data endpoints. Front Door Premium/WAF are global resources with a Sweden private-origin connection. Runtime remains `Simulator`; no Teams messages or existing business content are accessed.

The initial compute environment failed before infrastructure initialization because the subscription lacked `Microsoft.Network/AllowBringYourOwnPublicIpAddress`. Registering the feature did not fully repair that already-created empty environment: apps had no revisions and the seed pod could not start. Only those newly created empty compute resources were recreated; the data services and identities were preserved. The working environment is `teamsrefdev-unc3jasrpkgbo-env-ready` on the dedicated `container-apps-ready` subnet. The original subnet is retained while Azure completes old-environment cleanup.

An actual browser then caught a CSP hash mismatch caused by CRLF versus HTML parser newline normalization. The hash calculation was corrected without adding `unsafe-inline`; regression coverage and a new image were deployed. Azure sign-in now hides the unavailable local-login option based on a credential-free server capability response.

The final seed execution `teamsrefdev-unc3jasrpkgbo-seed-wmlmjnr` succeeded after seeding/verifying the allowlisted dataset. Repeated seeding did not reset it. Public `/health/ready` returned exactly `status=ready`, `mode=simulator`, and `fixtureVersion=split-view-v1`.

The first Entra login attempt used the dedicated Alex account without logging its password or exporting browser tokens. It stopped at mandatory security setup. Complete that interaction manually, then execute the authenticated scenarios against the public app; current public smoke results are not a substitute.

The later user screenshot showed an additional **"Need admin approval"** gate for `teams-reference-dev-web`. The live authorization redirect requests exactly `openid profile offline_access`, with an empty static `requiredResourceAccess` list. Initially, no grant existed. The user then explicitly approved and executed the user-specific helper. A subsequent read verified four `Principal` grants with exactly those scopes, zero tenant-wide grants, and `appRoleAssignmentRequired=true`. No Teams, chat, mail or file permissions, MFA changes, or app redeployment were needed. Fresh sign-in must still verify Entra acceptance and the resulting BFF session.

### Callback URI correction

The user's next sign-in reached `/auth/callback` but returned `dependency_unavailable`, trace `0HNPBCP2ANTDE:00000015`. Azure logs for that exact trace identified `AADSTS500112` / `invalid_client`: authorization used the public HTTPS Front Door callback, while MSAL code redemption reconstructed an internal HTTP Container Apps callback. The dependencies themselves remained ready.

`PublicAuthenticationOrigin.AddCanonicalOidcCallback` now configures both the OIDC authorization redirect and the named MSAL confidential-client redirect from the same validated `App:PublicOrigin`. It does not register an internal HTTP reply URL, trust arbitrary forwarded hosts, weaken TLS/token validation, or change consent/MFA. Existing redirect event handlers are awaited and preserved.

`OidcCallbackTests` uses the actual OIDC middleware and MSAL redemption path with proxy-like internal HTTP/host values, real protected state/correlation cookies, and a controlled identity HTTP transport. It asserts the actual token POST's `redirect_uri` matches the public authorization callback and retains a PKCE verifier. The transport intentionally rejects the synthetic code after capture; this is not a real Entra session test.

ACR `dt3` was built and rolled out only to the API. Its active revision `--0000003` is Healthy/Running, and deployed public smoke checks pass. A new Alex sign-in is still needed; an already-used authorization response or direct refresh of `/auth/callback` cannot validate the fix.

## Visual evidence

These screenshots contain generated test content only.

![Deployed Sweden sign-in, desktop light](validation/sweden-signin-light.png)

[Deployed Sweden sign-in, mobile dark](validation/sweden-signin-dark.png)

The following earlier screenshots show the local authenticated Split View, not an authenticated Azure session:

![Desktop Split View](validation/split-view-desktop.png)

[Mobile dark-theme Split View](validation/split-view-mobile.png)

Local detailed outputs are generated under `tests\Teams.Tests\TestResults`, `src\Web\playwright-report`, and `src\Web\test-results` and are ignored by Git. CI publishes only backend results and screenshots; traces are off by default and HTML credential-entry reports are not uploaded.

## Blocking prerequisites and remaining scope

### Deployment retries

The user re-enabled the initial subscription on 2026-10-15. After propagation, actual-identity ARM validation/what-if succeeded, tagged Entra identities and partial Azure infrastructure were created, and ACR build `cb1` successfully built a Linux container. Full platform deployment did not succeed: West Europe Cosmos access and Redis B0/B1 allocation were denied. A North Europe Cosmos fallback was created; no functioning public application was established. The original resources were left untouched when the target changed.

On 2026-10-16, the user selected subscription `67bfd3c6-6d7e-4a79-9a85-19f4a6928d7c`, in tenant `f1640304-f816-412c-9e6f-4b6b818cf492`. Initial bootstrap stopped at user creation with `Authorization_RequestDenied` under Global Reader. The later session included Global Administrator and completed all four dedicated users, assignments, and managed-identity/callback bindings. No old-tenant IDs or fabricated fixture users were substituted.

The bootstrap scripts accept explicit subscription selection without changing the CLI default, reject mismatched tenant inventories before external calls, and preserve partial progress as `bootstrapComplete=false`.

The user next selected Germany West Central, where Redis B0/B3/B5 preflights failed. The subsequent explicit Sweden Central selection succeeded. Current Bicep defaults, parameter templates, platform metadata, private-origin placement and GitHub Actions inputs use `swedencentral`, targeting `rg-teams-reference-dev-swc`.

Cosmos access is allowed in Germany West Central. Actual Germany subscription what-if attempts with Managed Redis Balanced B0, B3 and B5 each failed with `BadRequest`/`InvalidTemplateDeployment`, reporting insufficient capacity for this subscription in that region. No complete Germany apply was attempted after those failures. The reports are not successful capacity or deployed-app validation.

Platform provisioning is independent of test-user creation, but application/seed deployment now requires three distinct approved fixture actor IDs, enforced by the release script, nested workload template and host startup. Isolated PowerShell checks cover this separation and the network prerequisite. No authentication bypass was introduced.

| Gate | Current state |
| --- | --- |
| Enabled Azure target | Current subscription is Enabled; Sweden resources are deployed in their dedicated group. |
| Directory bootstrap | Four tagged apps/principals; the active test users are Alex, Sam and Jordan. Web assignments, redirect URI, managed-identity trust and callback role are configured. |
| Sign-in consent | `openid profile offline_access` is approved for the three active assigned users; fresh password/TOTP sessions are verified. No Teams message permissions are implied. |
| Teams licensing | Alex, Sam and Jordan have user-approved E5 (no Teams) and Teams Enterprise licences with Netherlands usage locations. Real Teams readiness still needs separate approval and evidence. |
| Cloud deployment | Sweden rollout succeeded, real private-store seeding and public/browser smoke passed. Authenticated message/send/reconnect/revocation journeys await fresh sign-in and any remaining Entra first-use setup. Earlier-subscription resources remain separate and may still charge. |
| Git publication and remote CI/CD | No branch changes, commits, pushes, or remote workflow execution. Environment protection/OIDC/RBAC setup remains a bootstrap action. |
| Live Teams implementation/readiness | Runtime intentionally rejects live mode. Resource onboarding, current Graph entitlement/policy authority, rich callback/subscription/repair wiring and real-tenant validation remain unfinished. The isolated delegated-send transport is not a complete Teams integration. |

This evidence does **not** pass production gates G0-G7, demonstrate a 200,000-user envelope, prove a real Graph send/freshness SLO, certify DLP/history/offboarding equivalence, or authorize processing existing business data. The approved synthetic dev scope and remaining work are recorded in [ADR 0008](adr/0008-simulator-first-single-region-development.md).

## Reproduction

Use [test guidance](testing.md), [dev setup/deployment](dev-guide.md), and the versioned fixture manifest. For a fresh local environment:

```powershell
.\scripts\Validate.ps1 -Browser
```

For deployed public checks, run `scripts\Test-DeployedApp.ps1` with the actual origin, tenant and web client ID from the private inventory. Approve the narrowly scoped sign-in consent and finish any required security setup before the authenticated scenario pass. Do not replace missing evidence with simulator success or open private endpoints to make a check pass.

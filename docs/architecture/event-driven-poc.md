# Event-driven Teams application: Azure PoC architecture

| Field | Scope |
| --- | --- |
| Audience | Customer solution architects and technical decision-makers |
| Reviewed | 2026-10-26 |
| Architecture baseline | Current repository implementation, including local infrastructure changes; not a live Azure inventory |
| Purpose | Explain a small functional PoC and its implemented scale-out mechanisms |
| Scope boundary | One application region; no regional failover design, production capacity claim or contractual SLA |

## 1. The customer story

**Serve repeated reads from Azure, process Teams changes asynchronously, and keep Teams authoritative for messages.**

The application separates three different concerns:

- **Commands:** the API sends a message to Microsoft Graph as the signed-in user and records the result.
- **Events:** Graph notifications enter a durable queue; background processing updates the Cosmos DB projection. A transactional outbox and Cosmos Change Feed drive client notifications.
- **Queries:** the API authorizes the request and reads recent content from Redis or Cosmos DB, without calling Graph in the normal content-read path.

This is a CQRS-style separation of commands, projections and queries, not an event-sourced system. The Cosmos outbox and change journal are bounded operational records, not a permanent history from which all application state can be rebuilt.

The event-driven parts can process independently of the original HTTP request and of each other. The send itself is **not** a queued asynchronous command: Graph dispatch and its bounded retries run inside the API request.

The PoC demonstrates architectural mechanisms, not a validated number of users or requests per second. Adding Azure replicas does not increase Microsoft Graph quotas.

## 2. Azure resources and execution boundaries

### Azure resource overview

The five roles are independently deployable Azure Container Apps using the same application image. They are not five different implementations of the business rules. The overview groups resources by responsibility; the focused diagrams below explain individual paths.

![Azure resource overview showing global Front Door outside the single application region, five Container Apps roles, three Service Bus queues, Cosmos DB, Managed Redis, Web PubSub and supporting services.][preview-azure-resource-overview]

**Figure 1. Azure resource overview.** Major relationships only, not a network inventory or one serial request path. Browser WebSockets connect directly to Web PubSub, not through Front Door. The regional boundary is a logical grouping, not an ARM resource group or VNet enclosure.

[Open full-size PNG](diagrams/azure-resource-overview.png) | [Vector SVG](diagrams/azure-resource-overview.svg) | [Editable draw.io source](diagrams/azure-resource-overview.drawio)

**Diagram conventions:** solid blue arrows indicate synchronous requests or control flow; dashed teal arrows indicate asynchronous events/work. Numbered steps show the normal journey, with failure behavior in adjacent notes. In the operation-state diagram, arrows represent state transitions rather than messages. Official Azure icons identify resources; neutral labeled shapes identify external systems or in-process activities.

### Resource responsibilities

| Azure resource | Implemented responsibility | Important boundary |
| --- | --- | --- |
| Front Door Premium and WAF | Public application and callback ingress; private application origins | No authenticated message-response caching at the edge |
| Container Apps environment and five role apps | API, callback acceptance, projection, delivery and control | Independent deployment/replica bounds do not establish throughput |
| Cosmos DB for NoSQL | Durable recent-message projection, operation evidence, outbox and security/control state | Required for correctness, not merely an optional cache |
| Azure Managed Redis | Revision-keyed recent pages and backend session/token-related caches | Content-cache failure can fall back to Cosmos; authentication-state loss has different consequences |
| Service Bus | Three queues with locks, retries, expiry and dead-letter subqueues | Durable work does not mean completed business processing |
| Web PubSub | Browser connectivity, admission and authorized revision hints | Not a durable message archive; hints contain no message bodies |
| Key Vault | Session-protection key and notification decryption-key operations | Receiver does not need private decryption-key access |
| Managed identities and Entra registrations | Role-scoped Azure access; separate interactive and background Graph identities | Azure RBAC, Graph consent and end-user authorization are different controls |
| Container Registry | Immutable application image delivery | Deployment infrastructure, not part of a message's runtime path |
| Application Insights, Log Analytics and Azure Monitor | Logs, metrics and diagnostic evidence | Existing documentation defers operational alert routing; telemetry is not proof of staffed support |
| VNet, private endpoints and private DNS | Private application origins and supported backend connectivity | Graph/Entra remain external HTTPS dependencies; Web PubSub clients use public WSS |

**Development configuration is not a production sizing template.** The platform template uses serverless Cosmos with Session consistency, a small Managed Redis configuration without HA, and Standard Web PubSub. It also supports a reviewed Service Bus Standard namespace alternative to the original Premium/private namespace. Standard uses shared capacity and has a 256-KB encoded-message/batch limit; the notification producer checks the SDK-negotiated batch size and explicitly rejects an individually oversized envelope.

The five-role deployment consumes an existing platform. Its source files do not prove which namespace, SKU or replica count is currently live. The Service Bus Standard exception permits public TLS with Entra-only authentication; it must not be depicted as a private endpoint. Do not infer a live cutover from a local template change.

### Azure Container Apps: application services

The application runs as five role-specific Container Apps, each using the same image but its own configuration and managed identity. These are application services, not five different Azure products. The five-role template configures ingress for `Api` and `Receiver`; `Projector`, `Delivery` and `Control` run background workers without application ingress.

| Application service | Primary input | Main result |
| --- | --- | --- |
| `Api` | Authenticated browser requests | Authorized reads, delegated message-send outcomes, operation status and real-time connection admission |
| `Receiver` | Graph notification and lifecycle callbacks | Validated work durably accepted into `graph-notifications` |
| `Projector` | Work from `graph-notifications` | Conditional Cosmos projection/change/outbox updates and repair scheduling |
| `Delivery` | Cosmos Change Feed and work from `graph-delivery` | Authorized, body-free Web PubSub hints with durable fan-out progress |
| `Control` | Subscription/policy maintenance, repair state and `graph-read-refresh` | Renewed subscriptions, refreshed authority and bounded source-to-projection synchronization |

#### Api: interactive requests

`Api` is the browser's backend-for-frontend. It checks current access and history boundaries, reads recent content through Redis/Cosmos, and sends messages through Graph with the signed-in user's delegated token. It also exposes original-operation status and negotiates authenticated Web PubSub access.

When requested history has not been loaded yet, it submits approved refresh context to `graph-read-refresh`; it does not fetch arbitrary Graph history inline. Message sends remain synchronous commands, not queued background sends. Redis accelerates content/session handling but does not replace the Cosmos operation journal. See the [write/status path](#3-write-path-and-asynchronous-operation-status-check) and [read fallback](#read-fallback-redis-cosmos-then-load-from-teams).

#### Receiver: fast, durable callback acceptance

`Receiver` handles the Graph notification and lifecycle endpoints, including subscription challenges and validation of signed tokens, client state and approved resource scope. Valid notification work enters `graph-notifications` before durable acceptance is acknowledged.

It deliberately does not decrypt message content, hydrate Graph history or perform broad projection/authority writes. This keeps callback acceptance small and separates public service callbacks from private processing and key access. A callback `202` alone is not enqueue evidence because some validation failures are intentionally discarded; see [Teams event ingestion](#teams-event-ingestion).

#### Projector: source events into the read model

`Projector` consumes the notification queue, decrypts and validates rich notifications, and applies version-aware message changes, edits and deletions. Material changes commit the projection, revision, change journal and outbox together in the conversation's Cosmos partition.

It completes queue work only after the required processing succeeds, including a verified duplicate/no-op. It also records gaps, schedules repair and handles bounded notification-DLQ maintenance. It has the decryption and projection permissions needed for that work, but does not independently acquire Graph credentials or send messages as users.

#### Delivery: committed changes into client hints

`Delivery` contains two cooperating activities in one deployed role. The Change Feed dispatcher observes committed outbox records and publishes durable work to `graph-delivery` before its feed checkpoint advances. The fan-out handler consumes that work, claims recipient pages, rechecks current access and publishes body-free revision hints through Web PubSub.

Durable page progress lets processing resume without skipping recipients; hints may repeat. The browser then reads authorized deltas or a snapshot through `Api`. This role distributes change awareness, not message bodies or user-send commands. See [Change Feed and client delivery](#change-feed-and-client-delivery).

#### Control: maintain source synchronization and authority

`Control` owns background subscription renewal, policy refresh, bootstrap, reconciliation and bounded hydration. It consumes `graph-read-refresh`, coalesces matching work through conditional leases, reads approved Graph resources and merges source pages using the same projection/outbox rules.

Its separately approved application Graph access is distinct from the user's delegated send permission. It refreshes authority and can revoke affected connections, but does not post normal user-authored messages. Background message loading continues independently of the API response; see [history not loaded yet](#when-message-history-has-not-been-loaded-yet).

**Why separate these services?** Callback acceptance, interactive requests, decryption, fan-out and source maintenance have different latency, privilege and workload characteristics. Independent roles reduce coupling and allow different replica bounds and processing concurrency. They still share dependencies: more workers do not remove Graph limits, Cosmos contention or Web PubSub capacity constraints.

### Azure Service Bus: queues

The selected Service Bus namespace contains three logical work queues, not three separate Service Bus deployments. Each queue buffers a distinct stage so the producer can record durable acceptance without waiting for all downstream processing. Multiple worker instances consuming a queue compete for work; a message is not broadcast to every application instance.

| Queue | Producer | Consumer | Payload and purpose |
| --- | --- | --- | --- |
| `graph-notifications` | Primarily `Receiver`; `Projector` also emits lifecycle continuations and maintenance repair markers | `Projector` | Encrypted source-change envelopes and body-free lifecycle/repair work awaiting projection processing |
| `graph-delivery` | `Delivery` dispatcher and continuation/recovery handling | `Delivery` fan-out handler | Body-free committed-change context and recipient-page work awaiting authorized hint delivery |
| `graph-read-refresh` | `Api` | `Control` | Body-free approved tenant/conversation/epoch/thread/page context requesting bounded loading of missing history from Teams |

#### graph-notifications: decouple callbacks from projection

This queue separates webhook acceptance from decryption and Cosmos writes. Normal rich notifications retain encrypted content until `Projector` decrypts and validates it; lifecycle continuations and repair markers can be body-free. A durable enqueue is acceptance of work, not evidence that the read model has been updated.

Notifications can repeat or arrive out of order, so the projection's conditional merge and tombstone rules provide correctness beyond broker delivery. Work completes after required processing, not on receipt. Failed work is retried or dead-lettered; expired notification dead letters lead to durable scope-repair scheduling before removal.

#### graph-delivery: decouple outbox publication from fan-out

This queue carries internal work derived from committed Cosmos outbox records, not raw Graph notifications or message bodies. The producer and consumer belong to `Delivery`, but have different responsibilities: reliable publication first, then bounded recipient processing.

Publishing before the Change Feed checkpoint prevents a publication failure from silently advancing past work. Fan-out progress, leases and stable identifiers support retries and continuation pages; duplicates can still produce repeated hints. Eligible dead-lettered delivery work has a re-drive path, subject to retained evidence and current resource approval.

#### graph-read-refresh: load requested history from Teams

This queue lets `Api` request missing history from Teams without holding the HTTP request open for a complete source scan. The request contains approved context, not delegated credentials, message bodies or an arbitrary Graph URL. `Control` derives source paths from the resource registry, conditionally owns/coalesces the work, and persists page progress.

Accepted work may still be pending when the API returns `202`; submission failure is not acceptance. A Redis miss alone does not trigger this recovery if Cosmos already has usable content. Control receives from this queue without needing a sender grant, and there is no automatic refresh-DLQ replay.

**Shared queue behavior:** consumers use PeekLock and explicit completion, with lock renewal for bounded processing. Failed work is abandoned or dead-lettered under its queue policy. Each queue has a dead-letter subqueue; it is not an additional application work queue. At-least-once delivery and competing consumers mean duplicates and ordering must be handled by application state.

The queues isolate stages and absorb bursts, but do not increase Graph quotas, provide exactly-once effects or create a durable outgoing-send queue. For the configured TTLs, delivery thresholds, duplicate-detection windows and distinct recovery policies, see [dead-letter handling](#64-dead-letters-isolate-failures-and-choose-recovery-by-work-type); for lock and checkpoint behavior, see [locks, leases and checkpoints](#65-locks-leases-and-checkpoints-are-different-mechanisms).

### Cosmos containers and transaction scope

| Container | State | Partition boundary |
| --- | --- | --- |
| `live-state` | Messages, conversation metadata, operations, changes, outbox and delivery progress | `/pk`; conversation records use tenant plus hashed conversation identity |
| `live-authority` | Membership/history evidence, principal revocation and related access records | `/pk`; conversation or principal scope |
| `live-control` | Subscription, reconciliation, notification-receipt and hydration ownership/checkpoint records | `/pk`; control-work scope |
| `live-leases` | SDK Change Feed ownership and checkpoints | `/id`; required lease-container layout |

A batch is atomic only within one container and one logical partition. Authority/control updates and cross-partition catalog maintenance are not part of the message/outbox transaction. Redis is rebuildable acceleration, but Cosmos also contains security and operation evidence that cannot safely be treated as disposable content.

## 3. Write path and asynchronous operation-status check

### Successful write

![Six-step successful write: browser submission, Api authorization, conditional Cosmos dispatch claim, delegated Graph POST, atomic Cosmos confirmation and projection/outbox commit, then HTTP 201 to the browser.][preview-write-path]

**Figure 2. Successful write.** The API remains responsible for the command throughout. Step 5 is a local Cosmos transaction; Graph creation in step 4 is a separate external commit. Only a material projection change creates a new outbox event.

[Open full-size PNG](diagrams/write-path.png) | [Vector SVG](diagrams/write-path.svg) | [Editable draw.io source](diagrams/write-path.drawio)

For the complete path from this send through Change Feed to another person's browser, see [sender A to recipient B](#full-journey-sender-a-sends-recipient-b-sees-the-message).

Retries and uncertain outcomes are separate from this successful-flow picture. Explicit Graph `429` rejections permit bounded retries under the original operation; ambiguous POSTs do not. Known rejection returns HTTP `422`. HTTP `202` is not confirmation that Teams created the message.

The application creates one operation ID per user attempt, binds commands to a short-lived server-issued window, fingerprints the payload, and conditionally owns dispatch. Reusing an operation ID with different content is a conflict. Reusing the same valid operation returns its recorded state rather than posting again.

### What "async check" means in this implementation

The browser offers **Check original send** after an uncertain or returned-pending operation. This is a user-triggered HTTP status request, not automatic status polling and not a queued send processor.

![Send-operation states showing conditional dispatch, bounded retry after definite 429, confirmation, pre-dispatch or Graph rejection, and unknown commitment, with a separate user-triggered status-check flow that never resends.][preview-send-operation-status]

**Figure 3. Operation evidence and status checks.** The two Rejected boxes are the same state reached at different boundaries. The retry arrow requires a definite `429` and remaining budget; unknown has no automatic resolution arrow.

[Open full-size PNG](diagrams/send-operation-status.png) | [Vector SVG](diagrams/send-operation-status.svg) | [Editable draw.io source](diagrams/send-operation-status.drawio)

Status lookup can mark an expired pending operation rejected, or an expired dispatching operation unknown. It invokes a resolver interface for unknown results, but the Live `GraphTransport.ResolveAsync` currently returns `null`. Therefore **an ambiguous send can remain unknown**. Receiving a matching-looking message through synchronization is not implemented proof that it belongs to that operation.

The API's operation journal does not retain the message body for deferred dispatch. There is no durable offline-send queue, app-only send fallback or automatic replay of an unknown POST.

**Persistence failure is a separate boundary.** A Graph send can succeed before its Cosmos confirmation transaction fails. The current service can return `503 confirmation_persistence_unavailable`; it does not guarantee a successful response carrying a separate "projection pending" state. Keep the original operation ID and do not resend automatically. Local idempotency cannot make a Graph POST and a Cosmos transaction atomic.

### Where the caches participate

The send correctness path uses current Cosmos operation, authority and conversation evidence. Redis supports backend session/token handling, but it is **not the durable command journal, a Graph rate limiter or a write-behind message buffer**.

A known confirmed send applies the message to Cosmos without waiting for its Graph notification. A material projection change creates an outbox record. Subsequent reads select the new revision's Redis page; old pages are not synchronously rewritten by the send path.

## 4. Event ingestion and Cosmos Change Feed path

### Full journey: sender A sends, recipient B sees the message

The application **pushes a small change hint, not the message body**, to the recipient's connected browser through Azure Web PubSub. That hint prompts the browser to fetch the latest authorized content from `Api`, then update the conversation on screen.

The earlier [successful-write diagram](#successful-write) stops at the sender's confirmation; the [Change Feed diagram](#change-feed-and-client-delivery) starts from committed outbox work. The view below joins those paths and includes the recipient's content read and display.

**Starting conditions:** A and B are signed in, both can access the conversation, and B has it open in a connected application tab. B has already negotiated a short-lived Web PubSub connection through `Api`; messages for that authenticated user reach the tab over WSS. This is live push within this web application, not an email, mobile/OS push notification or guarantee that someone has read the message.

![End-to-end sender-to-recipient journey: A submits a message to Api, Graph stores it in Teams, Cosmos commits the app copy and outbox, Delivery observes Change Feed and queues graph-delivery work, authorized fan-out publishes a body-free Web PubSub hint, and B fetches authorized Api content and displays the message.][preview-sender-to-recipient]

**Figure 3a. Complete sender-to-recipient flow.** Read left to right across the first row, then the second. Steps 5 and 7 are activities inside the same Delivery Container App. Teams stores the message at step 3; `Api` writes the application copy/outbox into Cosmos at step 4, not Graph itself. The dashed Web PubSub-to-read connection means B receives a hint and initiates a separate content request; Web PubSub does not return the message body.

[Open full-size PNG](diagrams/sender-to-recipient.png) | [Vector SVG](diagrams/sender-to-recipient.svg) | [Editable draw.io source](diagrams/sender-to-recipient.drawio)

| Step | What happens |
| --- | --- |
| 1. A submits | The browser sends the body, original operation ID and command window to `Api`. |
| 2. Api dispatches | The API checks current access, records/claims the operation in Cosmos, and posts to Graph using A's delegated user token. |
| 3. Teams stores | Graph creates the Teams message and returns usable creation evidence, including its message ID. Local API acceptance alone is not this confirmation. |
| 4. Api saves the application copy | The shared persistence logic conditionally commits the operation result and any material message/revision/change/outbox updates in one Cosmos conversation partition. The sender can receive confirmed `201` without waiting for B to receive a hint or display the message. |
| 5. Change Feed observes the outbox | Delivery's dispatcher processes the committed outbox and publishes stable-ID work. The feed checkpoint advances only after successful queue publication. |
| 6. Service Bus buffers delivery | `graph-delivery` durably separates publication from recipient processing. It carries body-free work, not the message content or a queued Teams-send command. |
| 7. Delivery checks recipients | The fan-out handler leases a page of recipients, rechecks current access and principal revocation, and sends hints only for eligible users. Page progress is durable; retries can repeat hints. |
| 8. Web PubSub pushes to B | The backend uses `SendToUserAsync` for B's tenant/user identity. B's connected browser receives the conversation ID and target application revision over WSS. |
| 9. B fetches content | The browser records the highest hinted revision and requests authorized changes. With an existing loaded view, it calls `/api/conversations/{id}/changes` with its previous revision and the required minimum revision. A missing view or required reset uses an authorized page snapshot instead. |
| 10. B displays | The API returns permitted content and access evidence; the browser applies the update and renders it. A reader behind the requested revision reports that explicitly and is retried rather than returning an older page as current. |

#### What is actually pushed?

The live payload contains only the conversation handle and an application change counter, for example:

```json
{ "conversationId": "chat-example", "revision": 42 }
```

It means **"this conversation has changed; fetch at least revision 42"**, not "here is the message." The follow-up API read enforces B's access and visible-history limits again. Delta reads use Cosmos; a page snapshot can use the Redis recent-page cache with Cosmos authorization/metadata checks. Receiving a hint never grants access to the message.

**The notification path can race with the send response.** This diagram illustrates a successful API confirmation producing the application update. A matching Graph notification may reach Receiver/Projector first and commit the same message; both paths use the shared duplicate/older-change checks. Delivery follows the committed projection/outbox, not a requirement to process the send response and its notification in a fixed order. Messages created in the native Teams client enter through the [notification-ingestion path](#teams-event-ingestion) and then join this same post-commit delivery flow.

**If B is disconnected or not viewing the chat:** do not interpret backend publish acceptance as delivery or display. On reconnection or a later conversation read, the client catches up through authorized API reads; Web PubSub is not the message archive. A hint alone does not force an unopened conversation to display its message body. The browser also reconciles on reconnect/visibility changes and periodically while eligible, so a missed hint does not become the only record of a message. The current flow provides no read receipt proving B saw or read the message.

### Source-event and application-event boundaries

There are two distinct event boundaries: **Graph reports a source change**, then **Cosmos exposes a committed application change**.

### Teams event ingestion

![Teams notification pipeline from Microsoft Graph through Receiver validation, encrypted graph-notifications queue and Projector processing to a conditional Cosmos projection and outbox commit.][preview-teams-event-ingestion]

**Figure 4. Source change to durable projection.** Queue acceptance decouples the callback from decryption and projection. The projector completes work after successful processing, not merely after receipt.

[Open full-size PNG](diagrams/teams-event-ingestion.png) | [Vector SVG](diagrams/teams-event-ingestion.svg) | [Editable draw.io source](diagrams/teams-event-ingestion.drawio)

### Change Feed and client delivery

![Post-commit pipeline from the Cosmos outbox through a Change Feed dispatcher, graph-delivery queue, authorized paged fan-out and Web PubSub to a browser that fetches content from the Api.][preview-change-feed-delivery]

**Figure 5. Durable projection to client refresh.** Feed dispatch and fan-out are activities in the same Delivery Container App, not additional deployments. Queue acceptance precedes the Change Feed checkpoint; hints can repeat.

[Open full-size PNG](diagrams/change-feed-delivery.png) | [Vector SVG](diagrams/change-feed-delivery.svg) | [Editable draw.io source](diagrams/change-feed-delivery.drawio)

Key distinctions:

- Graph notifications do not write Redis and do not publish directly to the browser.
- The Change Feed processor watches `live-state` and filters pending outbox documents. It is not a direct Teams subscription, nor a stream that repairs content absent from Cosmos.
- Both confirmed API sends and background source merges can create outbox records. They converge through conditional, version-aware projection rules.
- Outbox and delivery work are body-free. Browsers fetch bodies through the authorized read API.
- Delivery rechecks recipients, persists leased page progress and coalesces revisions. A publish acknowledgment is not evidence of browser receipt or rendering.
- Normal freshness uses notifications. Control maintains subscriptions and performs bounded hydration/reconciliation for gaps; those paths use the same merge/outbox rules.

Graph validation challenges are a separate branch: echo the validation token as plain text rather than enqueueing it. The Receiver deliberately returns `202` for certain invalid-payload validation failures after logging and discarding them. Consequently, a callback `202` alone is not proof that a particular notification entered the queue. Broker acceptance failure is not handled as successful durable acceptance.

## 5. Read path: cached messages and loading history from Teams

**Missing cached content does not mean an empty conversation.** The application may not have loaded the requested messages yet, for example after cache cleanup, on first access, or when opening a previously unloaded thread or page.

After checking current access, the API tries Redis, then the Cosmos message copy. If the requested history is not available and has not been established as empty, it requests background loading from Teams through Graph. This process is called **hydration** in the implementation. A **cold view** means this "history not loaded yet" state; it is not another service, a Container Apps cold start, or simply a Redis miss.

| What the application finds | What happens |
| --- | --- |
| Redis has the requested page | Serve it after authorization and history checks |
| Redis misses; Cosmos has usable messages | Read Cosmos and attempt to refill Redis |
| No usable messages are stored and the requested history has not been established as empty | Queue bounded loading from Graph, save the results in Cosmos, then reread |
| The requested history has been checked and is empty within the user's allowed range | Return an empty `200` page without another Graph request |

These are content-read decisions, not fallback permissions. Cosmos must remain available for authorization and metadata, including on a Redis hit. An empty Redis page alone is not proof that Teams has no messages. An available partial page can be returned with a continuation while requesting optional background loading.

### Read fallback: Redis, Cosmos, then load from Teams

![Complete read fallback: authorize with Cosmos, try Redis content, then the Cosmos projection; if requested history is not loaded, queue Control to read Graph and persist pages into Cosmos before the Api rereads, refills Redis and returns authorized content or pending status.][preview-read-path]

**Figure 6. Complete content-read fallback.** Try Redis, then the Cosmos message copy. If the requested history is not loaded rather than established as empty, durably queue loading from Teams. Control fetches source pages and writes them into Cosmos; the API rereads the saved content and attempts to refill Redis. The API returns ready content or accepted `202 pending`, not a Graph-only response that bypasses Cosmos.

[Open full-size PNG](diagrams/read-path.png) | [Vector SVG](diagrams/read-path.svg) | [Editable draw.io source](diagrams/read-path.drawio)

### Cosmos projection: how Teams messages become readable

Cosmos stores the application's recent-message read model: the message copies used to serve reads without fetching Teams history every time.

The same message can reach the application through a successful send response, a Teams notification, or background loading from Teams. These copies can arrive more than once or out of order. Before updating Cosmos, the receiving service checks the incoming message against the copy already stored.

**This check runs inside Api, Projector or Control using shared code. It is not a separate service, and it does not combine the text of different messages.** The three input paths are:

| Input | Responsible service | When it updates the projection |
| --- | --- | --- |
| Confirmed message send | `Api` | Graph returns usable creation evidence; the API records confirmation and applies the returned message without waiting for a notification |
| Teams change notification | `Projector` | A rich notification reports a source message creation, edit or deletion |
| Background loading or synchronization repair | `Control` | Requested history has not been loaded yet, or stored content needs source verification/repair |

![Projection overview showing Api, Projector and Control each checking incoming messages against the stored copy through shared code inside the calling role, then conditionally updating Cosmos and separating authorized reads from asynchronous delivery hints.][preview-projection-flow]

**Figure 6a. Three ways to update the same Cosmos read model.** The middle box represents a check performed inside each calling service: should this incoming message update the stored copy, be ignored, or require verification with Teams? It is a code step, not another Azure resource or service-to-service call. Once a change is saved, authorized reads and asynchronous client notifications proceed independently.

[Open full-size PNG](diagrams/projection-flow.png) | [Vector SVG](diagrams/projection-flow.svg) | [Editable draw.io source](diagrams/projection-flow.drawio)

How an incoming message is handled:

1. **Compare it with the stored copy.** Check the message identity, source-version evidence, modification time and any deletion marker.
2. **Decide whether to apply it.** Accept supported new changes, ignore duplicates or older copies, and preserve deletions. If conflicting information cannot be safely ordered, flag it for background verification with Teams.
3. **Save the change and notification record together.** Write the accepted message state, application change counter, change-journal entry and body-free outbox record in one Cosmos transaction. This prevents a message update from being committed without its corresponding delivery work.
4. **Serve reads and notify clients separately.** The API can read committed content, enforce access/history boundaries and populate Redis. Delivery independently sends a change hint so connected browsers know to refresh; reads do not wait for that notification.

> **Example:** an edited message is already stored in Cosmos. A delayed notification then arrives containing its earlier text. The application ignores that older copy instead of undoing the edit. A deletion marker, also called a *tombstone*, similarly prevents an old notification from restoring a deleted message.

**Implementation details:** the transaction is scoped to one `live-state` conversation partition. A known send confirmation also includes its operation result. Duplicate or older content does not create a new material revision/outbox event; repopulating a logically invalidated message after cleanup can legitimately create a new revision. Conditional Cosmos writes protect against concurrent updates.

The application change counter, or **revision**, tracks changes to the local read model; it is not an ordered Teams message version. Redis pages are selected by this revision, not updated by a synchronous message-body broadcast. A reader may need to converge on a newly committed revision before returning content as current.

The message/outbox transaction does not include separate-container receipt or repair bookkeeping, the Graph request, or Service Bus completion. Processing therefore tolerates redelivery after a successful local commit. The projection remains a bounded read model, not a per-user message copy or complete event-sourced archive.

#### Projector sequence: commit before completing queue work

This sequence follows one valid rich message notification after Receiver has already accepted the callback. It focuses on the Projector, not on all three input paths.

![Short sequence with Service Bus delivering an encrypted notification under PeekLock, Projector decrypting and validating, reading current Cosmos message/tombstone/revision evidence, conditionally committing the projection and outbox, finishing required receipt/repair state, and only then completing the queue message.][preview-projector-sequence]

**Figure 6b. Durable processing before broker completion.** The Projector compares the notification with the stored copy, conditionally saves accepted changes, and performs required receipt/repair bookkeeping before `CompleteMessageAsync`. The sequence groups internal calls for readability; receipt/repair writes are not part of the projection's atomic batch.

[Open full-size PNG](diagrams/projector-sequence.png) | [Vector SVG](diagrams/projector-sequence.svg) | [Editable draw.io source](diagrams/projector-sequence.drawio)

| Processing outcome | Implemented behavior |
| --- | --- |
| Accepted material change | Commit the projection/change/outbox state, finish required bookkeeping, then complete queue work |
| Duplicate or older notification | Avoid a new material revision/outbox event; complete only after the remaining required processing succeeds |
| Conflicting source evidence | Mark source coverage for repair and durably schedule repair instead of choosing an unsupported order |
| Processing, persistence or bookkeeping failure | Do not complete as success; record repair for known affected resources and abandon, or dead-letter at the fifth delivery under the notification-worker policy |

**Two different acknowledgments:** Receiver's Graph callback response acknowledges accepted notification work. Projector's queue completion happens later and acknowledges successful processing. A crash between a Cosmos commit and broker completion can redeliver the notification; the conditional merge makes that repeat safe. See [Teams event ingestion](#teams-event-ingestion) and [queue-specific dead-letter handling](#64-dead-letters-isolate-failures-and-choose-recovery-by-work-type) for the surrounding mechanics.

**Teams remains authoritative; Cosmos makes the bounded copy readable; Redis accelerates those reads.** Projection availability is distinct from source freshness: stored authorized content can remain available while reconciliation is incomplete. That freshness trade-off remains an unresolved customer decision, not a guarantee of current Teams content.

### What "Redis and Cosmos caches" means

| Layer | Contents and behavior | What it does not guarantee |
| --- | --- | --- |
| Teams | Authoritative message source | Unlimited or low-latency Graph access |
| Cosmos projection | Durable, bounded recent-message copy plus journals, outbox and correctness state | Instant source freshness or general Teams policy equivalence |
| Redis recent-page cache | Shared candidate pages, selected by tenant/conversation, epoch, revision, cleanup generation, thread and cursor; user history filtering occurs before response | Authorization, freshness of unseen source changes, or removal of Cosmos from the request path |
| Browser memory | Loaded authorized views, drafts and optimistic operations | A durable archive or persistent offline-send capability |

Ordinary content uses native Cosmos **Session** reads. Current security/control decisions use targeted `ReadCurrentAsync` operations backed by `readCurrentV1`. Do not describe the whole account as Strong, or assume independent SDK clients share session tokens.

A Redis hit still requires Cosmos authorization and metadata reads. It saves content queries; it does not eliminate all Cosmos RU or latency.

Recent pages expire after at most **15 seconds**, shortened by content expiry. They hold at most 21 candidate messages for a 20-message response plus continuation detection. Per-conversation active-page indexing is bounded to 256 entries; oversized cache payloads are not stored. A connection failure falls back to Cosmos, whereas invalid serialized cache data produces an explicit error rather than being trusted.

**Invalidation is revision-based.** A material projection commit changes the revision, selecting a different cache key. Old pages expire naturally; there is no Change Feed worker that synchronously refreshes every Redis page. Administrative cleanup also uses a generation fence so older fills cannot become current after invalidation.

**Delta reads are different:** `/changes` reads the Cosmos change journal and projected documents. Do not claim that every read endpoint is served by the Redis recent-page cache. Hint-driven refresh may use deltas or an authorized snapshot.

### When message history has not been loaded yet

**Why use a queue?** Graph loading can take longer or be throttled. Service Bus lets Control continue independently of the browser request. After durable acceptance, the API briefly checks Cosmos again: it returns messages if they are ready, otherwise `202 pending` tells the browser to retry. Control saves the Graph results into Cosmos before the API serves them.

![Two-lane background loading flow showing requested history not yet loaded, durable graph-read-refresh submission, bounded Api rereads and a 200 or pending 202 response independently of Control claiming work, reading Graph and saving messages into Cosmos.][preview-cold-view-hydration]

**Figure 7. Load missing history from Teams.** The API rereads saved Cosmos content while Control loads messages independently. The solid cross-lane arrow is a read dependency, not a barrier requiring the background job to finish before the HTTP response.

[Open full-size PNG](diagrams/cold-view-hydration.png) | [Vector SVG](diagrams/cold-view-hydration.svg) | [Editable draw.io source](diagrams/cold-view-hydration.drawio)

**Example: after clearing both content caches.** Redis misses and the invalidated Cosmos message copy needs to be loaded again. A new read can queue that work, Control retrieves the approved messages from Teams, and the API reads the refilled Cosmos copy and populates Redis. Cleanup preserves conversation metadata, authorization and send-operation evidence; it neither makes Cosmos unavailable nor deletes the original Teams messages. Background synchronization can also refill the copies. If the requested range is checked and empty, the API returns an empty page without repeatedly fetching it.

#### Not loaded, potentially stale and unavailable are different

| Condition | Meaning | Read behavior |
| --- | --- | --- |
| **Not loaded** | The requested content is missing and has not been established as empty | Request background loading; return ready content or accepted pending status |
| **Potentially stale** | Stored content exists, but an edit or deletion may not have synchronized | Existing authorized history can remain readable while repair runs; this is the current availability-first behavior |
| **Unavailable** | A required dependency or authorization check failed | Return an explicit error or denial, not a fabricated empty result or an unsupported claim that work was accepted |

Loading missing history does not mean the whole conversation must be copied before anything can be displayed. Work is bounded to the requested context; available messages can be returned while more history remains to be loaded. A completed loading marker also does not prove that the copy includes every latest Teams change.

#### Execution bounds and failure behavior

The API has a five-second total read budget. After a loading request is durably accepted, it can return `202` with `Retry-After: 1` and `retryAfterMs: 1000`; the browser retries within a 120-second recovery budget. Queue submission failure is `read_refresh_unavailable`, not accepted pending work. A partial available page may request optional best-effort loading.

Control coalesces matching requests through durable leases. Each loading job has a 45-second execution budget, at most three 50-item source pages plus an optional parent lookup, and at most two concurrent handlers per Control process. Source-page absence does not establish deletion, and a completed loading marker does not certify fresh source coverage.

**Unresolved customer concern: stale content.** The current availability-first implementation can display previously stored, authorized history despite unhealthy source synchronization. A missed edit or deletion may remain visible until reconciliation or seven-day content expiry. The customer has not accepted that freshness trade-off for the final architecture. This document describes it; it does not approve it or silently substitute a stricter behavior.

Authorization remains fail-closed. Expired or unverifiable access cannot be extended to keep cached messages visible, and a Cosmos/security-store failure does not switch the API to a Graph-only bypass.

## 6. Reliability and event-driven best practices

### 6.1 Transactional outbox: close the database/event dual-write gap

**Problem:** updating Cosmos and independently publishing an event can leave committed data without a notification, or a notification without committed data.

**Implemented pattern:** within one `live-state` logical conversation partition, a Cosmos transactional batch conditionally commits message changes, conversation revision, change-journal entry and outbox record. A confirmed operation result joins that transaction when applicable. No material message change means no new material-change outbox event.

The Delivery role publishes outbox work through Change Feed. The feed callback succeeds only after queue submission succeeds, allowing the SDK to checkpoint. Publication failure retains feed progress for retry.

The transaction does not span Cosmos and Service Bus. A crash after queue acceptance but before the feed checkpoint can publish twice. Likewise, no local transaction spans Graph and Cosmos. **The outbox solves reliable publication of committed local changes, not exactly-once external side effects.**

### 6.2 At-least-once processing: design for duplicates

Broker deliveries, callbacks and feed batches can repeat. Correctness therefore depends on application state, not solely on Service Bus duplicate detection.

| Boundary | Implemented protection |
| --- | --- |
| Browser send | Actor-scoped operation ID, payload fingerprint and conditional dispatch ownership |
| Source merge | Source-version/content comparison, timestamps, tombstones and conditional ETag writes |
| Outbox publication | Stable work identifier derived from change and page context; feed checkpointing |
| Fan-out | Durable page cursor, conditional lease and completion record; duplicate hints remain possible |
| Browser convergence | Application revisions, minimum-revision reads and bounded delta/snapshot recovery |
| Read hydration | Context-keyed lease, completion evidence, epoch and generation checks |

Notification envelope IDs are random receipt IDs, not semantic message deduplication keys. Repeated notifications for an unchanged source version must not create repeated material revisions.

### 6.3 Retry only when the operation is safe

The shared Graph retry defaults are **three retries**, a **120-second operation budget**, and a **20-second attempt budget**. A narrower caller budget, cancellation or insufficient time for `Retry-After` can end work sooner. These are execution limits, not promised response times.

| Operation | Actual retry behavior |
| --- | --- |
| Delegated message POST | Automatic retry only for definite Graph `429`; recheck authority and dispatch ownership before another attempt |
| Ambiguous message POST | Keep original operation unknown; never retry merely because a timeout or `5xx` occurred |
| Background GET | Retry `429`, selected transient `408/500/502/503/504` responses, and selected transport/timeouts within budget |
| Background non-GET | Retry explicit `429`, not an ambiguous subscription write |
| Broker consumer failure | Do not complete successful-looking work; abandon/dead-letter according to that queue's policy |

Honor Graph's `Retry-After` when present. Otherwise use capped exponential backoff with jitter. Initial Graph calls dispatch immediately: the application does not implement a proactive shared quota gate, global cooldown or Redis-based rate admission.

Not every broker retry has exponential delay. The notification worker abandons failed deliveries without an explicit application sleep; the read-refresh worker adds bounded backoff. A single generic "all retries use exponential backoff" claim would misdescribe the code.

### 6.4 Dead letters: isolate failures and choose recovery by work type

These are configured queue contracts in the infrastructure and worker code, not a live inspection of broker properties.

| Queue | Normal expiry / delivery threshold | Implemented failure and recovery |
| --- | --- | --- |
| `graph-notifications` | One-hour TTL; five deliveries | Worker abandons failed processing and dead-letters at the fifth delivery. It marks known affected resources for repair. Its DLQ maintenance retains newer entries; once their enqueue age exceeds one hour, it durably queues scope-repair work before completing/removing the expired encrypted item. It does not blindly replay old ciphertext. |
| `graph-delivery` | One-hour TTL; ten deliveries | Processing failures abandon work. DLQ maintenance reschedules valid approved work while its change is younger than 14 days, using a new recovery ID and one-minute delay. Durable page progress prevents skipping completed work. Invalid/unapproved items are quarantined for one hour; expired evidence is removed with snapshot recovery required. |
| `graph-read-refresh` | Five-minute TTL; five deliveries | Invalid, fenced or exhausted work is explicitly dead-lettered. Other failures wait/back off and abandon. **No automatic refresh-DLQ replay.** Investigate the cause, then use a fresh authorized request or separately reviewed recovery. |

All three configured queues dead-letter expired messages. Delivery duplicate detection uses a one-hour window; read-refresh uses one minute. Neither replaces semantic deduplication.

**DLQ is not an archive or an infinite recovery guarantee.** Application maintenance explicitly removes some dead letters after repair scheduling or retention checks. Conversely, do not assume Service Bus TTL automatically cleans a DLQ. Operational ownership must cover stuck work, repeated re-drives and repair that cannot complete before content expiry.

### 6.5 Locks, leases and checkpoints are different mechanisms

Service Bus uses PeekLock with a one-minute configured lock. Notification and delivery consumers enable up to five minutes of automatic renewal; read-refresh enables three minutes. Automatic completion is disabled. Complete only after the required durable work succeeds.

Cosmos leases serve different purposes: distribute Change Feed ownership, claim subscription/control work, coalesce hydration, and track delivery pages. Conditional ETags prevent two workers from both progressing the same claimed state.

Checkpointing after work is the core recovery rule. If publishing or progress persistence fails, retry from retained evidence. A repeated body-free hint is safer than advancing past undelivered work.

Expired ownership is not proof that Graph did not create a message. Do not reclaim a timed-out dispatch as a new send.

### 6.6 Ordering, deletions and convergence

Do not assume a globally ordered event stream. Graph notifications and reconciliation pages can race with a successful send response or each other.

The merge compares available source evidence, preserves newer edits, and uses tombstones to prevent stale content resurrection. Conflicting equal-time evidence can mark coverage for repair rather than inventing an order. Graph ETags are opaque, not sortable sequence numbers.

The application revision orders local projection changes. It is not a Teams sequence number or a substitute for Cosmos consistency. A reader behind a requested revision returns `503 projection_behind`, not an old page labeled current.

### 6.7 Backpressure and scale-out

Queues separate fast durable acceptance from slower decryption, projection and fan-out. Concurrency and per-job bounds prevent one request from scanning an unlimited history.

| Role | Implemented distribution mechanism | Scaling boundary |
| --- | --- | --- |
| Api | Stateless request handling with shared durable evidence and backend caches | Authority/metadata RU, sessions, response bandwidth and Graph send limits |
| Receiver | Independent bounded acceptance and broker producer | Validation cost, envelope size and queue ingress capacity |
| Projector | Competing Service Bus consumers; configurable concurrency, default eight per process | Cosmos RU, hot conversation contention and decryption capacity |
| Delivery | Change Feed leases plus competing delivery consumers and paged fan-out | Feed ranges/leases, recipient checks, Cosmos checkpoints and Web PubSub capacity |
| Control | Conditional subscription/repair ownership and refresh-context leases | Graph read/policy budgets; two refresh handlers maximum per process, not globally |

These mechanisms allow additional replicas to cooperate; they do not prove linear scaling. A conversation's messages, revision and outbox share a logical partition. More application replicas or aggregate RU cannot remove that conversation's hot-partition and revision-write contention.

The five-role template exposes minimum/maximum replicas and scale timing, but does not declare queue-depth-based custom scale rules. Do not claim implemented backlog-driven autoscaling. Queue age, processing duration, downstream saturation and configured replica bounds must be observed before making a scale claim.

### 6.8 Security, subscriptions and observability belong in the reliability story

**Copied-data authorization:** the API checks current revocation, membership, visible-history boundaries and resource context. Delivery rechecks recipients after queue delays. Body-free hints reduce exposure but do not replace authorization. Redis reads cannot renew source authority.

**Separate privileges:** the API uses delegated Graph permission for sends; Control uses application-authenticated reads and subscriptions. Receiver accepts encrypted notifications without broad projection-write or decryption privileges. The current Graph-backed policy provider supports a bounded approved resource/user model, not arbitrary enterprise Teams visibility, DLP or moderation equivalence.

**Maintain subscriptions:** Control owns renewal, lifecycle handling and bounded reconciliation. A webhook-only design without lifecycle repair would miss changes after subscription loss. Reconnecting a browser does not create a subscription per browser.

**Observe business outcomes:** track callback acceptance, queue age/DLQs, processing failures, source coverage, feed publication, delivery pages, Graph attempts/retries, unresolved operations and Redis hit/miss/Cosmos fallback. Measure actual browser display separately from broker or Web PubSub acceptance. Never log routine message bodies, credentials or connection tokens.

Instrumentation and historical diagnostic evidence exist. Alert definitions or healthy deployments do not prove routed alerts, operational ownership or a contractual service level.

## 7. Architecture assumptions and customer decisions

The baseline below is the current implementation, as requested. An implemented value is not automatically an approved final customer requirement.

| Concern | PoC baseline | Final architecture decision / evidence still needed |
| --- | --- | --- |
| Workload and RPS | Small functional PoC; no inherited 200,000-user, 15,000-RPS or burst target | Concurrent users, read/write mix, external Teams activity, peak duration and reconnect behavior |
| Graph capacity | Endpoint-specific app, tenant, conversation and user limits apply | Observe actual throttling with approved identities and resources; quota ceilings are not guaranteed throughput |
| Read latency | Five-second server budget; bounded pending/retry behavior while history loads | Measure Redis hits, Cosmos fallback, loading of previously missing history, delta reads and browser rendering separately |
| Send latency | Default 120-second operation and 20-second attempt budgets; at most three retries | Measure confirmation latency including throttling; agree acceptable unknown/rejected outcomes |
| Availability / SLA | No end-to-end SLA is claimed | Agree service hours, dependency boundaries, eligible-request definition and measurement period |
| Freshness | Availability-first stored history; no source-age deadline that automatically hides content | **Unresolved:** acceptable stale edits/deletions and behavior during source degradation |
| Content retention | Seven days from message creation, bounded further by user history; Redis at most 15 seconds | Approve window and deletion requirements; distinguish logical invisibility from physical expiry, backups and operational records |
| Additional records | Change/outbox/tombstone evidence generally retains 14 days; queue copies have separate lifecycles | Approve every copy and retention class rather than applying "seven days" to everything |
| Data-copy approval | Assume current Cosmos, Redis and encrypted-notification processing for describing the PoC | **Open:** customer approval of content/metadata processing, location, access, deletion, backup and DLQ handling |
| Tenant and scope | One approved tenant; explicitly configured users/resources and supported policy conditions | Approve onboarding, consent, history boundaries and unsupported scenarios |
| Feature scope | Text and supported formatting, existing chat/channel resources and replies; attachment/embedded-content handoff to Teams | Confirm required chat types, attachments, moderation, guests and cross-tenant behavior |
| Consistency | Session content reads plus targeted current security/control reads | Measure convergence and RU/latency; no blanket Strong-consistency claim |
| Delivery semantics | At-least-once work and potentially repeated hints | Accept duplicate-tolerant UX; no exactly-once or guaranteed-browser-display claim |
| Unknown sends | Original-operation status is available; automatic Live resolution is not implemented | Agree user/support handling and required resolution capability |
| Scale-out | Independent role apps, queue consumers, conditional claims and feed leases | Establish workload-specific replica rules, partition behavior and measured bottlenecks |
| Hosting and SKUs | Existing Development resources; optional reviewed Service Bus Standard path | Verify actual inventory, SKU limits, networking and capacity before any deployment promise |
| Operations | Diagnostics and queue-specific recovery mechanisms | Assign DLQ/repair ownership and configure/verify alert routing |

### Microsoft Teams Graph limits relevant to this PoC

Snapshot verified against the [official Microsoft Graph throttling documentation](https://learn.microsoft.com/en-us/graph/throttling-limits#microsoft-teams-service-limits) on **2026-10-26**.

| Graph operation | Per app across tenants | Per app per tenant | Per conversation | Per user |
| --- | ---: | ---: | --- | --- |
| List chat messages | 200 RPS | 20 RPS | 1 RPS per chat | No dedicated value in this row |
| List channel messages | 200 RPS | 20 RPS | 1 RPS per channel | No dedicated value in this row |
| Send chat message | 200 RPS | 20 RPS | 1 RPS per chat | 1 RPS |
| Send channel message | 500 RPS | 50 RPS | 1 RPS per channel | 1 RPS |
| Send channel reply | 500 RPS | 50 RPS | 1 RPS per channel | 1 RPS |
| Get chat metadata | 2,000 RPS | 200 RPS | 1 RPS per chat | 5 RPS |
| Get channel metadata | 600 RPS | 30 RPS | 1 RPS per channel | No dedicated value in this row |
| Get team members | 1,200 RPS | 60 RPS | 4 RPS per team | No dedicated value in this row |

The first applicable limit reached triggers throttling. Microsoft also documents a longer-window sustained limit of approximately **83% of the listed value**; continuously running at the table ceiling can still throttle. Limits can change. These are not Azure API-read limits or an allocation reserved for this PoC.

For example, two different users sending concurrently to one chat still encounter the chat's 1-RPS constraint. Spreading traffic across chats does not remove the app/tenant ceiling. Adding API/Control replicas does not change either constraint.

Redis and Cosmos reduce repeated **content retrieval** from Graph. Background policy checks, hydration, reconciliation, subscription operations, retries and external Teams activity still affect dependency demand. Application read RPS cannot be calculated directly from the Graph message-list limit, nor inferred without measured cache effectiveness and Cosmos/security-read cost.

Subscription counts/lifetimes and notification timing are additional constraints, not message RPS. Validate them for the selected resource/subscription scope against [Teams message notification guidance](https://learn.microsoft.com/en-us/graph/teams-changenotifications-chatmessage) and the [subscription resource documentation](https://learn.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-1.0). Do not convert expected notification timing into a contractual freshness SLA.

## 8. What the PoC evidence establishes

The repository's [Development validation report](../dev-validation.md) records deployments of the five roles and bounded functional evidence. In particular, the 2026-10-23 normal read-service probe records 24 authorized content reads, five Redis misses with Cosmos fallback/refill, nineteen Redis hits, and denial of three excluded-pair reads. That is useful cache-aside and authorization evidence, not a throughput benchmark or browser-latency measurement.

The same report distinguishes available authorized history from incomplete source reconciliation: strict projection/source-health acceptance remained incomplete for one resource. Historical successful journeys must not be represented as acceptance of every later image, deployment change or failure mode.

| Claim the document supports | Claim it does not support |
| --- | --- |
| Event-driven ingestion and post-commit delivery are implemented | All paths are asynchronous queued commands |
| Redis cache-aside and Cosmos projection reads have bounded functional evidence | A specific production RPS, user count or cache-hit ratio is guaranteed |
| Conditional state, outbox, leases and queue-specific recovery are implemented | Exactly-once Graph sends or exactly-once client delivery |
| Roles can cooperate through shared durable state | Linear replica scaling or implemented queue-depth autoscaling |
| Current stored authorized content can remain available during source repair | Customer acceptance of stale edits/deletions |
| Deployment/diagnostic evidence exists for the bounded Development environment | A production SLA, fully accepted source freshness or approved production-data handling |

A later customer presentation can follow the same sequence: problem and boundaries, Azure resources, write/status path, event/Change Feed path, read/cache path, reliability patterns, and unresolved decisions. This document does not introduce a separate future architecture or a slide deck.

## Appendix A. Review of the earlier detailed architecture

The [earlier document](../detailed-architecture.md) mixes target-reference guidance with implementation details. It remains unchanged. This new document deliberately makes the following corrections for the current PoC:

| Earlier-document area | Implementation-grounded treatment here |
| --- | --- |
| Regional topology, failover flow and surviving-region sizing | Excluded from this single-region PoC narrative |
| Production-oriented resource inventory | Name the implemented resources; separate template defaults, supported alternatives and unverified live settings |
| Reference-scale capacity and proposed SLOs | Do not inherit large user/RPS assumptions or percentile/SLA promises |
| Uncertain-send investigation/repair | Identify the actual user-triggered status check and unimplemented Live resolver |
| Known confirmation with projection-pending response | Explain the actual Cosmos persistence-error boundary rather than promising a separate response contract |
| Proposed API table | Use implemented callback `/callbacks/graph/notifications`; catch-up uses numeric revisions, while page cursors and command windows are signed |
| Generic cache discussion | Distinguish Redis recent pages, Cosmos durable projection, targeted security reads and Cosmos-backed deltas |
| Example outbox event schema | Describe the actual `OutboxChange` contract and wrapper rather than treating the conceptual example as executable schema |
| General failure/recovery guidance | State concrete queue-specific delivery thresholds, DLQ maintenance and non-replay cases |
| Notification durable acknowledgment | Distinguish accepted valid work from intentionally discarded validation failures that also receive `202` |
| Scale signals | Distinguish architectural distribution mechanisms from absent queue-depth scale rules and unmeasured capacity |

## Appendix B. Implementation map

Paths link to the reviewed source, not a frozen deployed image.

| Concern | Primary implementation |
| --- | --- |
| Send ownership, outcomes, status check and read orchestration | [`Messaging.cs`](../../src/Teams.Core/Messaging.cs) |
| Live delegated Graph POST and unresolved-result stub | [`GraphTransport.cs`](../../src/Teams.Host/GraphTransport.cs) |
| API route/status contracts | [`Program.cs`](../../src/Teams.Host/Program.cs) |
| Browser Check original send | [`App.tsx`](../../src/Web/src/App.tsx) |
| Browser pending-read recovery | [`useMessageFeed.ts`](../../src/Web/src/useMessageFeed.ts) |
| Browser push reception and hint-driven reads | [`useRealtime.ts`](../../src/Web/src/useRealtime.ts), [`App.tsx`](../../src/Web/src/App.tsx), [`useMessageFeed.ts`](../../src/Web/src/useMessageFeed.ts) |
| Conditional projection/outbox, cache keys and delta reads | [`LiveStateStore.cs`](../../src/Teams.Infrastructure/LiveStateStore.cs) |
| Cosmos reads and transactional batches | [`PartitionDocuments.cs`](../../src/Teams.Infrastructure/PartitionDocuments.cs) |
| Redis page limits, TTL and generation fencing | [`RedisRecentMessages.cs`](../../src/Teams.Host/RedisRecentMessages.cs) |
| Callback validation and encrypted intake | [`GraphNotifications.cs`](../../src/Teams.Host/GraphNotifications.cs), [`LiveServices.cs`](../../src/Teams.Host/LiveServices.cs) |
| Notification projection, failure handling and DLQ retention | [`NotificationWorker.cs`](../../src/Teams.Host/NotificationWorker.cs) |
| Change Feed dispatcher, delivery pages and DLQ re-drive | [`OutboxDelivery.cs`](../../src/Teams.Host/OutboxDelivery.cs) |
| Tenant/user-addressed, body-free Web PubSub hints | [`MessagingAdapters.cs`](../../src/Teams.Infrastructure/MessagingAdapters.cs) |
| Missing-history queue, leases, background loading and dead letters | [`ReadRefresh.cs`](../../src/Teams.Host/ReadRefresh.cs) |
| Graph retry budgets and response-driven retry rules | [`GraphRetry.cs`](../../src/Teams.Core/GraphRetry.cs), [`GraphRequests.cs`](../../src/Teams.Host/GraphRequests.cs) |
| Subscription, repair and policy control | [`LiveWorkers.cs`](../../src/Teams.Host/LiveWorkers.cs), [`GraphPolicyFeed.cs`](../../src/Teams.Host/GraphPolicyFeed.cs) |
| Base Development platform | [`main.bicep`](../../infra/main.bicep) |
| Containers, identities and notification/delivery queues | [`live.bicep`](../../infra/live.bicep) |
| Refresh queue and exact-scope grants | [`read-refresh.bicep`](../../infra/modules/read-refresh.bicep) |
| Five-role workload settings and replica bounds | [`service-dev.bicep`](../../infra/service-dev.bicep) |
| Runtime constraints and evidence | [Live runtime](../live-runtime.md), [Development validation](../dev-validation.md) |

## Appendix C. Editing and exporting the diagrams

Each diagram has an editable `.drawio` source, a vector SVG and a 2x-resolution PNG under [`diagrams`](diagrams). The document embeds **self-contained PNG previews as base64 image references**, a supported image format in the GitHub Copilot App Editor. Inline diagrams do not depend on local image-path resolution, an external image server or SVG rendering. The separate full-size PNGs and SVGs remain linked for reuse.

Edit the source in draw.io Desktop and regenerate both exports rather than editing an exported image independently. Sources embed their icon images, and SVG exports use native text without HTML labels or remote image/font dependencies.

The Azure resource icons come from Microsoft's [official Azure architecture icon collection](https://learn.microsoft.com/en-us/azure/architecture/icons/), **V24**, downloaded on 2026-10-26. Used assets are Worker Container App, Container Apps Environments, Front Door and CDN Profiles, Azure Cosmos DB, Azure Managed Redis, Azure Service Bus, PubSub, Managed Identities, Key Vaults, Container Registries and Monitor. Their original artwork is embedded without recoloring, distortion or rotation. Microsoft's linked icon terms apply. External systems and Delivery's internal activities use neutral text shapes rather than unrelated product icons.

These exports use draw.io Desktop **32.6.1**. From the repository root, export one diagram locally with:

```powershell
$drawio = 'C:\Program Files\draw.io\draw.io.exe'
$source = '"{0}"' -f (Resolve-Path 'docs\architecture\diagrams\read-path.drawio').Path
Start-Process -FilePath $drawio -ArgumentList @(
    '--export', '--format', 'svg', '--embed-svg-images',
    '--embed-svg-fonts', 'false', '--theme', 'light', '--size', 'page',
    '--disable-update', '--timeout', '30', $source
) -Wait
Start-Process -FilePath $drawio -ArgumentList @(
    '--export', '--format', 'png', '--scale', '2',
    '--theme', 'light', '--size', 'page',
    '--disable-update', '--timeout', '30', $source
) -Wait
```

This writes matching `.svg` and `.png` files beside the source; the quoted source argument also supports repository paths containing spaces. Keep labels plain text with explicit line breaks and text wrapping disabled; this avoids HTML `foreignObject` labels that some SVG consumers cannot display. Inspect both exports after editing, at document width and presentation size.

After changing the PNG exports, refresh the image references with [`embed-previews.py`](diagrams/embed-previews.py), using Python (no extra packages required):

```powershell
python docs\architecture\diagrams\embed-previews.py
```

The helper verifies that the PNG exports exist and replaces only the image references and generated preview block. Standard Markdown image references use repository-relative PNG paths so diagrams render directly in GitHub's Markdown preview and in local viewers. Do not use base64 data URLs: GitHub does not render them as images. Keep the generated references rather than editing them manually. SVG remains the vector format for resizing and presentation reuse.

### External references

- [Microsoft Azure architecture icons and permitted use](https://learn.microsoft.com/en-us/azure/architecture/icons/)
- [draw.io SVG export and portable text guidance](https://www.drawio.com/doc/faq/export-to-svg)
- [Microsoft Graph service-specific throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits#microsoft-teams-service-limits)
- [Microsoft Graph throttling guidance](https://learn.microsoft.com/en-us/graph/throttling)
- [Teams chatMessage change notifications](https://learn.microsoft.com/en-us/graph/teams-changenotifications-chatmessage)
- [Microsoft Graph subscription resource](https://learn.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-1.0)
- [Cosmos DB transactional batch](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/transactional-batch)
- [Cosmos DB Change Feed processor](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/change-feed-processor)
- [Service Bus message locks and settlement](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-transfers-locks-settlement)
- [Service Bus dead-letter queues](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues)

<!-- BEGIN GENERATED DIAGRAM PREVIEWS -->
[preview-azure-resource-overview]: diagrams/azure-resource-overview.png
[preview-write-path]: diagrams/write-path.png
[preview-send-operation-status]: diagrams/send-operation-status.png
[preview-sender-to-recipient]: diagrams/sender-to-recipient.png
[preview-teams-event-ingestion]: diagrams/teams-event-ingestion.png
[preview-change-feed-delivery]: diagrams/change-feed-delivery.png
[preview-read-path]: diagrams/read-path.png
[preview-projection-flow]: diagrams/projection-flow.png
[preview-projector-sequence]: diagrams/projector-sequence.png
[preview-cold-view-hydration]: diagrams/cold-view-hydration.png
<!-- END GENERATED DIAGRAM PREVIEWS -->

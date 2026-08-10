# Execution Kernel Compatibility Inventory

This inventory records the compatibility behavior characterized by ARI-153 and the kernel substrate introduced since then against the normative EK scenarios in the [Cohesive Execution Kernel Specification](https://app.notion.com/p/3ab8cf7881f981f78ef1e34d7a907c70). It is a migration baseline, not an alternative semantic contract. Missing behavior remains required by the specification.

Semantic-conformance meanings:

- **Pass**: the provider-neutral reference and durable profiles satisfy the scenario's normative semantic guarantees.
- **Partial**: useful semantic substrate exists, but one or more normative semantic guarantees are missing.
- **Absent**: the scenario's core semantic construct has no current representation.

Production adapters, stores, and topologies qualify independently against the same scenarios. An unqualified or
unsupported concrete realization does not downgrade provider-neutral semantic conformance; it must retain exact
capability evidence and fail validation, planning, or preflight rather than silently weakening the scenario.

EK-01 now passes through the canonical Transition compilation and reference-interpretation path. Canonical
interaction contracts and runtime envelopes provide the shared event, Request, Signal, and Reply vocabulary, and a
canonical durable-operation reference protocol now interprets Request attempts, acknowledgement, reconciliation,
and result admission. Canonical Process control now interprets protocol-neutral lifecycle commands, safe-point
coordination, attempt lineage, and write-once attempt affinity while remaining independent of its durable runtime
realization.
The canonical protocols are composed by a versioned `Cohesive.Storage.Processes` checkpoint, a copy-on-write
atomic reference store, and a Storage-owned durable reference driver with compatibility-first restore, inbox
admission, exact activation replay, CAS revisions, worker fencing, Request dispatch/recovery, lifecycle control,
and crash-cut injection. EK-06, EK-07, and EK-10 pass in the provider-neutral reference profile. The Process identity,
attempt, recovery, affinity, bounded child coordination, baseline materialization, incremental synchronization,
convergence, target-local activation, canonical rebuild planning, placement-bound leaves, independent promotion, and
placement-scoped backend routing portions of EK-08 pass in the provider-neutral reference profiles. Deterministic
production-shaped Cosmos DB/PostgreSQL-to-Elasticsearch vertical slices compose the leaf authorities through
canonical Relations hydration and readback, adaptive rejection feedback, generation recovery, target promotion, and
an explicit route swap. Production Process and backend-routing stores remain unqualified until they pass the same
crash, fencing, and replay matrix, and durable parent interpreters have not yet qualified the coordinated
multi-leaf promotion modes. Those realization gaps do not weaken the provider-neutral EK-08 semantics. EK-11 passes in the
provider-neutral reference profile through
exact Relation evaluation, bounded durable recurrence, progress retention, and checkpoint restore.
Canonical Process IR and its pure reference interpreter now
provide the persisted semantic graph, typed bindings, exact references, immutable token/wait state, deterministic
finite activations, and interaction intents needed by subsequent checkpoint work. EK-09 now passes:
representative Transitions and Processes now have typed C# producers that lower to fingerprint-equivalent direct
IR, survive strict round trips, and compile from their persisted documents without their producer assemblies. The
delegate-bearing Process model, source generator, single-cursor runtime, and adapters that executed it are no longer
shipped execution authorities. The Motion DQ fixture now supplies the first complete business-shaped composition
of those authorities: versioned profile resolution and entity-local Transitions, typed review and cancellation
Signals, common vendor/manual Requests, a seven-way post-terms Fork/Join, and five independently gated subject
activations. The matrix below records semantic conformance separately from concrete-realization qualification.

## Scenario matrix

| Scenario | Semantic conformance | Current compatibility | Concrete-realization qualification |
| --- | --- | --- | --- |
| EK-01 — structured DQ branching | Pass | `Cohesive.Transitions.IR` provides canonical persisted structured definitions with stable nodes, typed contracts and outcomes, ordered branching and matching, algebraic sparse patches, exact interaction-contract emission references, and fingerprint-bound Machine-edge references. `TransitionStaticCompiler` performs target-independent type, flow, exhaustiveness, access/effect, derived-field, invariant, and Machine-link analysis. `TransitionReferenceInterpreter` executes either complete state or finite sparse observations through one deterministic core and returns typed outcomes, committable patches, emission intents, Machine movements, guarantee demands, conflicts, diagnostics, and ordered actual-execution evidence. | None within the EK-01 reference decision. Observation acquisition and authoritative commit remain explicit external interpretations of the returned demands and intents. |
| EK-02 — durable human review | Pass | The Motion DQ Process creates a typed review task, retains only its stable task reference, and reaches a five-clause `AwaitMatch` over Hire, Hold, Not Eligible, cancellation, and an absolute timer. Its explicit late, stale, duplicate, missing-target, retention, priority, and tie-break policies survive canonical round trip and durable restore; Hold records one entity decision and returns through a fresh durable wait occurrence. Input evidence records the closed semantic reason independently from the policy action applied to it. | None in the provider-neutral reference profile. Production timer, task, and Signal adapters must pass the same scenario for their claimed realization. |
| EK-03 — vendor/manual fulfillment | Pass | Seven post-terms branches each issue one provider-neutral typed fulfillment Request. Vendor failure, semantic timeout, or cancellation is retained as typed provider-attempt evidence and routes to a manual occurrence using the same exact Request contract and durable binding without settling requirement authority. Only a fulfilled endogenous evaluation enters the generic case-scoped requirement Transition; the durable scenario proves a failed vendor attempt leaves the requirement Pending before manual success settles it exactly once. Replay-stable Request identity, fenced operation execution, acknowledgement, and Reply admission retain accepted authority against duplicate or later evidence. | None in the provider-neutral reference profile. Concrete vendor and manual-provider adapters must qualify against the same contract and recovery scenario. |
| EK-04 — parallel gates and join recovery | Pass | The Motion DQ Process exercises an All/unobservable seven-way post-terms Fork/Join. The reference interpreter retains reciprocal branch membership and branch-local progress, while the durable reference driver serializes and restores a partially completed fork, preserves completed operations, and converges without re-executing their Requests or Transitions. Completion order is intentionally unobservable and stable branch identity supplies the tie-break. | None in the provider-neutral reference profile. A production durable-store adapter must pass the same partial-branch and crash-cut matrix for its claimed realization. |
| EK-05 — capability-safe multi-entity coordination | Pass | Generic entity-local Transitions retain independent case, requirement, applicant, carrier/owner-operator, driver, truck, and trailer authority. The Process carries bounded commands and references rather than copied business-state snapshots, admits carrier activation before a four-way independent subject fork, and supplies each activation as an exact gate decision. Static compilation rejects `WholeDefinition` atomicity because the workflow crosses durable and external boundaries. Concurrent subject activation remains independently authoritative and stale gate evidence fails differentially without advancing aggregate case state. | Canonical branch-result aggregation and authored nested scope regions remain deferred. As required by the specification, the exact gate decision is supplied as Transition input until canonical aggregation exists; a target claiming an atomic scope must provide realization evidence or fail preflight. |
| EK-06 — durable effect crash matrix | Pass | The Request `EmissionId` is the logical operation identity and its scoped deduplication key survives every physical attempt. The durable driver atomically commits origin progress plus pending operation, records claim and dispatch before adapter I/O, repeats only the same fenced invocation when idempotency evidence permits, routes ambiguous outcomes to authored reconciliation, persists one acknowledgement, and atomically couples final operation disposition with Reply inbox admission. Exact store retries resolve pre/post-boundary crashes without changing intent, and stale fences cannot publish returned evidence. | None in the provider-neutral reference profile. Concrete store and operation adapters must pass the same crash matrix for their claimed realization. |
| EK-07 — signal arbitration | Pass | Canonical Signal commands enter the durable inbox exactly once by logical `EmissionId`; duplicate admission replays. The durable driver then restores the exact wait topology and uses the reference interpreter's priority, clause-identity, and emission-identity ordering to select one winner, retain loser/tombstone dispositions, and prevent stale exact wait occurrences from routing to later waits. Inbox admission and activation commit share one CAS revision, closing the registration/commit lost-wakeup cut. | None in the provider-neutral reference profile. Production signal and timer adapters must pass the same arbitration scenario. |
| EK-08 — index rebuild recovery | Pass | A canonical rebuild request freezes complete membership, compiles explicit subject-to-target placement and bounded scheduling evidence, and links one exact placement-bound leaf plan per slice into a fingerprinted plan set with declared promotion semantics. Each leaf plan revalidates and pins the materialization IR, impact plan, exact source/target capability evidence, stable shards and complete feed catalogs, scan requests, hydration physical plans, Control realizations, and finite operating bounds. Canonical coordinator and worker Processes execute bounded baseline work and recurrent activation through durable Requests. Storage allocates an isolated Loading candidate, captures exact change cuts, scans and hydrates bounded canonical Relations pages, writes replay-stable idempotent bulks, and checkpoints independent baseline and change tracks. Incremental synchronization durably prepares impact-derived target intent, applies it, commits application progress, and only then settles capable sources; catalog-complete convergence authorizes persisted seal, validation, and fenced target-local promotion. Generation-scoped adaptive Control changes admission and batch bounds only at safe points. Pause and Continue retain attempt, generation, progress, and Control epoch; RestartAttempt durably excludes the abandoned generation before creating one replacement. The reference router isolates revisions, ownership fences, idempotency receipts, routes, and lifecycle state by exact placement-slice fingerprint. A strict durable independent-promotion request binds the plan set, leaf, slice, active generation, routing cut, fence, command identities, and timestamps before admitting and atomically switching that slice's paired routes. Deterministic Cosmos DB and PostgreSQL pull sources pass one shared conformance suite and compose with the same materialization, Elasticsearch target, canonical row/count readback, retryable rejection feedback, recovery, target promotion, and explicit route swap. | Production durable Process and backend-routing stores, a durable parent scheduler, and coordination interpreters for `AllReadyProgressive` and `AtomicVisibility` must qualify against the same matrix. Real-service deployment evidence remains an optional adapter-qualification layer rather than part of the deterministic reference profile. |
| EK-09 — C# and IR equivalence | Pass | Representative typed C# Transition and Process authoring lowers immediately to the same canonical definitions and fingerprints as direct IR. Process authoring covers the closed node union and nested bindings, edges, branches, clauses, outcomes, bounded work, recurrence, child purposes, and terminal outcomes. Typed selectors become portable paths immediately; no callback survives construction. Strict document round trips preserve identity, type information, semantics, diagnostics, provenance, and source maps, and static compilation consumes only the persisted document plus explicit linking evidence. | None for representative Transition and Process equivalence. Each future authoring frontend remains responsible for the same normalization, round-trip, and source-attribution conformance. |
| EK-10 — atomic Process-store crash matrix | Pass | The copy-on-write reference store injects before-publication and after-publication-before-return crashes across initialization, inbox admission, worker acquisition, worker renewal, and aggregate commit. Before-publication failures expose none of the staged mutation and an exact retry applies once; after-publication failures expose the complete mutation and an exact retry replays its receipt. Aggregate commits publish continuation, lifecycle control, attempt affinity, activation, inbox/outbox, durable Request state, and local mutation as one replacement. Concurrent commits cannot mix state, inbox admission invalidates stale worker commits without losing wakeups, mutation identities reject changed content, and lease reclamation advances a monotonic fence. | None in the provider-neutral reference profile. Every concrete Process store must qualify independently against the same mutation, retry, concurrency, chronology, and fencing matrix. |
| EK-11 — durable polling recurrence | Pass | `RepeatAcrossActivation` evaluates a typed progress value and Boolean continuation condition, admits at most one repeat between durable cuts, retains exact occurrence and unchanged-progress counts, and routes deterministically to Completed, Exhausted, or Stalled. The durable runtime serializes, restores, validates, and resumes recurrence progress without a suspended host frame or free graph cycle. | None in the provider-neutral reference profile. Concrete Relation and Process-store adapters must pass the same recurrence and restore scenarios for their claimed realization. |

The fail-closed executable index lives in `src/Cohesive.Tests/ExecutionKernel/ExecutionKernelConformanceMatrixTests.cs`.
It points to the existing focused scenario tests, requires a non-skipped semantic entry for every EK-01 through
EK-11 scenario, and requires capability provenance for every adapter-qualification entry. New interpreters add
their deterministic scenario tests to that matrix; concrete adapters must retain their exact capability evidence
and may not replace or waive the provider-neutral semantic entry. External-infrastructure qualification remains
opt-in and outside the normal CI gate.

## Alpha closeout evidence

The active specification remains the sole normative Execution Kernel authority. C# authoring, generated contracts,
compiled plans, adapter bindings, and this inventory are attributable producers, projections, or evidence; none is
an alternative semantic model. The ARI-220 audit found no intentional semantic divergence requiring a normative
specification change. It closed an evidence gap by extending the existing executable matrix from EK-01–09 to
EK-01–11 and completed the retained monitoring-contract documentation.

Run the following gates from the repository root. Each command is an independently reproducible part of the alpha
definition of done; the conformance matrix itself rejects referenced tests marked skipped or explicit.

| Closeout boundary | Reproducible evidence |
| --- | --- |
| EK-01–11 normative scenarios and adapter attribution | `dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj --filter FullyQualifiedName~ExecutionKernelConformanceMatrixTests` indexes the existing executable semantic tests and requires exact capability evidence for adapter qualifications. |
| Canonical authority and restricted authoring | `dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj --filter "FullyQualifiedName~ExecutionAuthorityMigrationTests|FullyQualifiedName~ProcessAuthorityRetirementTests|FullyQualifiedName~CanonicalProcessAuthoringTests|FullyQualifiedName~CanonicalTransitionAuthoringTests"` proves retired delegate, flat-definition, and single-cursor authorities are unreachable and that typed C# producers discard executable callbacks after lowering. |
| Durable crash, replay, recurrence, and business-shaped differential behavior | `dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj --filter "FullyQualifiedName~InMemoryProcessDurableStoreCrashTests|FullyQualifiedName~ProcessDurableRuntime|FullyQualifiedName~MotionDq"` exercises the provider-neutral atomic store, runtime recovery, and Motion DQ conformance paths. |
| Deterministic index-sync adapter qualification | `bash eng/test-index-sync-vertical-slices.sh` runs the shared Cosmos DB/PostgreSQL-to-Elasticsearch vertical slices without requiring external services. |
| Retained public contract documentation and package surface | `dotnet build Cohesive.sln --no-restore --target:Rebuild --property:WarningsAsErrors=CS1591` and `bash eng/api-check.sh` fail on missing public XML documentation or invalid package output. |
| Complete .NET regression surface | `dotnet test Cohesive.sln --configuration Release` runs the full solution test graph; real-service integration tests remain explicitly opt-in adapter qualification rather than hidden semantic coverage. |
| Generated frontend contracts and consumers | `corepack pnpm frontend:build` and `corepack pnpm frontend:test` validate generated TypeScript contracts and their consumers against the backend semantic authorities. |

The remaining limitations in the scenario matrix are concrete-realization qualifications or explicitly deferred
features, not weakened provider-neutral semantics. Any future interpreter or adapter must add evidence to this
same matrix and compatibility inventory rather than introducing another closeout catalog.

## Canonical Channel boundary and provider projection matrix

ARI-189 adds a provider-neutral Channel vocabulary; it does **not** declare any provider below conformant or ship
a provider adapter. A provider name, protocol label, or SDK type is never sufficient capability evidence. Each
future adapter must publish a versioned `ChannelCapabilityProfile` whose coherent variants cite exact configuration,
operating limits, and source evidence. `ChannelRealizationCompiler` then selects one complete variant and records
one evidence-backed `ChannelRealizationDecision` for every exact requirement in a fingerprinted
`ChannelRealizationPlan`. It may not assemble a fictitious target by mixing incompatible modes from different
variants.

The semantic and runtime boundaries are:

1. `ChannelDefinition` is canonical IR. It owns the one-way or Request/Reply logical topology and the closed,
   provider-neutral requirement algebra. It contains neither destination handles nor provider names.
2. A capability profile describes what one configured target can prove. The realization plan is the compiled,
   attributable match from requirements to that evidence. A provider-specific binding may use the plan to create
   topics, subscriptions, endpoints, codecs, or client options, but those handles are derived artifacts rather than
   semantic authority.
3. The canonical payload or interaction envelope owns logical identity. `ChannelProviderDeliveryId` is optional
   provider evidence that is stable across redelivery only when the provider proves that property.
   `ChannelDeliveryAttemptId` identifies one physical attempt and changes on redelivery. An opaque lock token,
   receipt handle, acknowledgement subject, or acknowledgement ID is ephemeral `ChannelSettlementAuthority`; it
   must not become logical identity or durable progress.
4. `ChannelReplayCursor` selects retained input. It does not prove that the application applied that input.
   `ChannelDurableProgressEvidence` cites already-durable application progress and can retain replay position,
   cumulative floor, and exact pending or unresolved-gap evidence simultaneously. A provider-managed subscriber
   cursor is not silently interchangeable with an application checkpoint.
5. Settlement changes provider delivery state. It follows the consuming block's durable progress boundary and is
   recorded by a `ChannelSettlementReceipt` that cites that progress. Individual, cumulative, batch, invocation,
   negative, defer, and quarantine operations have different coupling scopes; an acknowledgement is not a generic
   synonym for replay or checkpointing.

The following matrix is projection guidance for building those profiles. “Candidate evidence” names semantics an
adapter might prove in a particular mode; the final column states the boundary that prevents an unsupported
equivalence.

### Retained logs and durable pub/sub

| Provider or archetype | Candidate evidence in a coherent configured variant | Boundary that must remain explicit |
| --- | --- | --- |
| [Apache Kafka](https://kafka.apache.org/documentation/) | A topic partition can evidence retained history, an ordered replay position, partition/key ordering, fan-out across consumer groups, and competing consumers inside one group. A committed group offset may evidence a partition-scoped cumulative floor. | Retention, replication, producer idempotence, transactions, consumer isolation, and offset storage are configuration. Kafka's transactional consume/produce boundary is not a general atomic application-state boundary, and a record offset, committed offset, logical message identity, and physical delivery attempt are distinct. |
| [Azure Event Hubs](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-features) | An Event Hub partition can evidence time-bounded retained history, offset/time replay, partition-key ordering, and independent consumer-group views. | In the AMQP consumer model the application stores checkpoints, commonly in external storage; an Event Hubs offset is a replay cursor, not a native per-event settlement receipt or proof that application state is durable. Kafka-protocol compatibility does not import every Kafka capability. |
| [Apache Pulsar](https://pulsar.apache.org/docs/4.2.x/concepts-messaging/) | Persistent topics and subscriptions can evidence retained delivery, replayable message positions, redelivery, subscription-scoped progress, and individual or cumulative acknowledgement. Partitioning and key-shared modes can supply narrower ordering and distribution variants. | Subscription type constrains acknowledgement and ordering: for example, cumulative acknowledgement is not available for Shared or Key_Shared subscriptions. Message ID, subscription cursor, redelivery attempt, and acknowledgement authority remain different evidence. Transactions and deduplication require separate configured proof. |
| [NATS Core](https://docs.nats.io/nats-concepts/core-nats) | Core subjects can evidence activation-local publication, fan-out or queue-group competing consumers, and connection-scoped Request/Reply via reply subjects. | Core NATS is an at-most-once, active-subscription transport with no retained stream, durable consumer progress, or consumer acknowledgement. Any retry, deduplication, replay, or durable Reply routing is an application composition and must not borrow JetStream evidence. |
| [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream/consumers) | Streams and coherent consumer configurations can evidence retained history, sequence/time start positions, durable consumer state, redelivery, acknowledgement floor and pending counts, explicit individual acknowledgement or AckAll cumulative settlement, negative acknowledgement, and bounded pending flow control. | Ack policy, durable versus ephemeral state, delivery policy, storage, replication, and limits define different variants. The acknowledgement subject is attempt-local settlement authority; stream sequence, consumer sequence, delivery count, and logical identity are not interchangeable. Deduplication plus double acknowledgement is a constrained protocol claim, not general application exactly-once execution. |
| [Google Cloud Pub/Sub](https://cloud.google.com/pubsub/docs/subscriber) | A subscription can evidence durable-until-acknowledged delivery, default at-least-once redelivery, individual acknowledgement, lease extension, and optional ordering-key scopes. [Snapshot or time seek](https://cloud.google.com/pubsub/docs/replay-overview) can evidence bounded snapshot/time replay when retention admits it. | The message ID can be stable provider delivery evidence, while an ack ID belongs to a particular delivery attempt. Subscription acknowledgement state is target-managed progress; seek changes that state in bulk and is not an ordered offset. Exactly-once, ordering, retention, and subscription type are constrained variants rather than defaults. |

### Work queues and broker settlement

| Provider or archetype | Candidate evidence in a coherent configured variant | Boundary that must remain explicit |
| --- | --- | --- |
| [Azure Service Bus over AMQP 1.0](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-transfers-locks-settlement) | Queues and topic subscriptions can evidence durable-until-settled delivery. Peek-Lock can evidence at-least-once delivery with individual complete, abandon, defer, or dead-letter settlement; Receive-and-Delete is a distinct at-most-once variant. Sessions can evidence selective acquisition and session-scoped order. | A volatile lock token is attempt-local settlement authority, not message identity or progress. Duplicate detection, sessions, transactions, tier, and entity configuration need explicit evidence. [AMQP 1.0](https://docs.oasis-open.org/amqp/core/v1.0/os/amqp-core-complete-v1.0-os.html) transfer/settlement semantics alone do not prove Service Bus retention, routing, or atomicity. Settled queues are not replayable logs. |
| [Amazon SQS Standard](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/standard-queues.html) | A Standard queue can evidence durable competing-consumer delivery, at-least-once redelivery, visibility-based temporary acquisition, individual or batch deletion, visibility release/extension, retention limits, and dead-letter redrive configuration. | Standard queues do not prove ordering or replay after deletion. Message ID is provider evidence; each receive's receipt handle is ephemeral settlement authority. A successful delete settles provider state only and must follow a separate durable application-progress record. Batch APIs can partially succeed. |
| [Amazon SQS FIFO](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-fifo-queues.html) | A FIFO queue adds message-group ordering and configured send deduplication to the SQS visibility/delete model; each message group is a separate ordering and acquisition scope. | FIFO marketing terminology must not become a blanket application exactly-once claim. Visibility expiry can produce another attempt, deletion still requires the current receipt handle, and producer deduplication has a bounded identity/configuration scope. There is no general retained-log replay cursor. |
| [Azure Queue Storage](https://learn.microsoft.com/en-us/azure/storage/queues/storage-queues-introduction) | A queue can evidence durable competing-consumer delivery, visibility-based redelivery, explicit delete, visibility update, bounded batch receive, expiry, and application-defined poison-message handling. | Retrieval order is only best effort. Message ID can identify the stored provider delivery, while the pop receipt is attempt-local settlement authority and changes as delivery/visibility state changes. There is no durable replay cursor, cumulative acknowledgement floor, native dead-letter operation, or application checkpoint. |

### Session messaging and invocation transports

| Provider or archetype | Candidate evidence in a coherent configured variant | Boundary that must remain explicit |
| --- | --- | --- |
| [MQTT 5](https://docs.oasis-open.org/mqtt/mqtt/v5.0/mqtt-v5.0.html) | Separate QoS 0, QoS 1, and QoS 2 variants can evidence at-most-once, at-least-once, or multi-step protocol settlement within a client session. Session expiry can evidence configured continuity for subscriptions and in-flight QoS state; retained publications can evidence retained-latest per topic. | QoS 2 prevents duplicate delivery at the MQTT protocol boundary; it does not prove exactly-once application effects. Packet identifiers are session-scoped protocol state, not durable logical identity. Retained-latest, queued session messages, and historical replay are different semantics, and broker persistence/limits require implementation evidence. |
| [ZeroMQ](https://zguide.zeromq.org/docs/chapter4/) | A specific socket pattern can evidence framed messages, connection-local routing, pub/sub, pipelines, or a Request/Reply exchange. High-water marks and a chosen pattern may supply bounded buffering or backpressure behavior. | ZeroMQ patterns do not by themselves provide broker durability, replay, durable progress, application acknowledgement, or failure-safe Request retry. Reliable queues, heartbeats, deduplication, correlation, and disk-backed replay are explicit application compositions with their own evidence; socket names cannot stand in for them. |
| [HTTP](https://www.rfc-editor.org/rfc/rfc9110.html) | One HTTP exchange can evidence unary invocation, operation-endpoint routing, invocation-scoped Reply isolation, byte-stream transport, and terminal response completion; an attributable codec can supply typed framing. | A response status proves protocol completion, not durable application acknowledgement. HTTP supplies no message retention, replay cursor, consumer progress, or settlement after disconnect. Retry safety depends on method semantics and an explicit application idempotency contract, not merely on transport success. |
| [gRPC](https://grpc.io/docs/what-is-grpc/core-concepts/) | One RPC can evidence typed unary, request-stream, response-stream, or bidirectional-stream interaction; message order is scoped to that RPC. A particular runtime can provide attributable stream flow control, deadlines, cancellation, and terminal status evidence. | Transport flow-control acknowledgement is not application settlement. Retries, hedging, codecs, limits, and load-balancer routing are configured behavior. gRPC has no native durable message retention, cross-RPC replay, or application progress checkpoint, and a new RPC attempt is not continuation of the old attempt unless an application protocol proves it. |
| [WebSocket](https://www.rfc-editor.org/rfc/rfc6455.html) | One connection can evidence a bidirectional, connection-ordered, reliable framed-message channel with terminal close behavior. | Frame/message boundaries, ping/pong, and TCP delivery do not prove application acknowledgement. The protocol supplies no reconnect continuity, retained history, replay, durable progress, or settlement; correlation, resubscription, deduplication, and recovery belong to an explicit subprotocol. |
| [Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html) | SSE can evidence a one-way server-to-client response stream, text-event framing, connection ordering, and reconnect intent. An application-assigned event ID plus a server retention contract may compose an ordered replay cursor. | `Last-Event-ID` reports a client observation during reconnect; by itself it proves neither server retention nor durable application progress. SSE has no client-to-server logical direction, consumer settlement, pending-delivery set, or atomic checkpoint boundary. Browser reconnect is not bounded-resume evidence without an explicit retained-history guarantee. |
| [WebTransport](https://www.w3.org/TR/webtransport/) | A reliable-stream variant can evidence multiplexed uni/bidirectional byte streams with order scoped per stream; a distinct datagram variant can evidence message boundaries with unreliable, unordered delivery. | Stream writes do not preserve application message boundaries without a codec. Reliable streams and unreliable datagrams are distinct coherent variants, not capabilities to splice into one fictitious lane. The evolving protocol supplies no application durability, replay, progress, or settlement after session loss. |
| [WebRTC DataChannels](https://www.w3.org/TR/webrtc/) | A negotiated data channel can evidence message framing and connection-scoped delivery with separately configured ordered/unordered and reliable/partially-reliable variants. Retransmission count or lifetime can supply an attributable partial-reliability bound. | The negotiated mode is fixed evidence for that channel; reliable and partial modes cannot be combined opportunistically. SCTP acknowledgement, buffered amount, and peer-connection state do not provide durable retention, replay, application progress, or settlement. Negotiated message-size limits remain explicit. |
| [RSocket](https://github.com/rsocket/rsocket/blob/master/Protocol.md) | RSocket can evidence fire-and-forget, Request/Response, Request/Stream, and bidirectional Channel shapes, framed payloads, per-stream RequestN demand, terminal signals, routing metadata, and configured connection resumption. | RequestN is flow demand, not durable acknowledgement. Resume is bounded connection/stream continuity over retained frames, not a broker history cursor or application checkpoint. Leases, resume windows, transport, fragmentation, and limits require variant evidence; durable replay, logical deduplication, and application settlement must be composed separately. |

This matrix is intentionally asymmetric: two providers that can both realize `AtLeastOnce` or `OrderedPosition`
may expose different identity stability, settlement coupling, replay scope, pending-progress evidence, and atomicity.
Conformance therefore attaches to an exact profile variant and realization plan, never to a provider family name.

## Canonical durable-operation reference protocol

The ARI-160 interaction vocabulary remains authoritative for Request meaning. `RequestResponseObligation` owns
terminal outcomes, retry preconditions, ambiguous and unresolved resolution, late/stale/duplicate policy, timeout
and cancellation support, and retention. `DurableRequestBinding` only supplies the concrete bounded realization
data needed to interpret one exact Request contract: attempt and lease bounds, an optional timeout trigger,
idempotency evidence, exhaustive exact Reply mappings, and definition/node references for required reconciliation
or escalation. Exact contract linking is validated through `InteractionContractCatalog`; handler registration or a
wire discriminator cannot choose the semantics.

`DurableOperationState` is the versioned portable reference state. It keeps the logical Request, binding, explicit
creation time, monotonically fenced claims and renewals, ordered immutable attempt snapshots, append-only fenced
reconciliation evidence, recovery requirement, one acknowledgement, and one target disposition. Acknowledgements
from reconciliation or escalation retain the exact recovery identity that won. The logical operation identity is
not another generated type or provider identifier; it is the Request `EmissionId`. The target-deduplication key
additionally scopes the stable idempotency value by authority and exact Request contract so unrelated Requests
cannot collide.

`IDurableOperationAdapter` receives an immutable `DurableOperationInvocation` and returns typed outcome or failure
evidence. It has no aggregate mutation surface and declares the exact Request contracts and target guarantees it
supports. `IDurableOperationBatchAdapter` returns complete emission/attempt/fence-keyed evidence for one physical
batch, allowing successful items to acknowledge independently while failed items alone remain retryable.
`DurableOperationReferenceExecutor` consumes that evidence through deterministic replacement-state operations and
validates adapter capabilities against the binding. Semantic timeout and cancellation are explicit typed state
transitions; host cancellation is only operational interruption. The split makes the three EK-06 cuts observable:

Only cancellation causally tied to the caller's cancellation token propagates as caller cancellation. A provider
timeout or cancellation exception with a live caller token is post-dispatch evidence: execution exceptions pass
through the configured failure classifier, while reconciliation exceptions retain an unresolved observation and
follow authored recovery. Neither path fabricates a semantic timeout outcome.

1. **Origin committed, dispatch not begun:** initial operation state remains pending and can be claimed; atomically
   creating that state with the origin commit and outbox is a Storage responsibility.
2. **External success possible, acknowledgement absent:** the dispatched attempt is ambiguous. Blind retry is
   forbidden unless stable-identity idempotency evidence admits it; otherwise the authored reconciliation,
   terminal-failure, or escalation path is required.
3. **Acknowledgement durable, target continuation not committed:** replay observes the acknowledgement and skips
   external dispatch. Result admission then accepts once or durably returns the target's prior duplicate, late, or
   stale disposition.

This is a reference protocol and conformance substrate, not a hidden claim that semantic state itself performs
durable I/O. The reference executor does not own a repository and deliberately leaves every durable cut to its
caller. The v2 `DurableOperationState` schema identifies the portable reference value; it is not a second Storage
operation-ledger or checkpoint authority. `ProcessDurableCheckpoint` composes this state into a physical aggregate
without copying
its fields, and `ProcessDurableRuntime` now drives the three cuts above through exact store-mutation retries,
operation fencing, adapter dispatch or reconciliation, acknowledgement, and Reply admission. EK-06 therefore
passes for the provider-neutral reference store and fake-adapter profile; concrete adapters must prove the same
matrix for their own guarantees.

## Canonical durable Process storage

`Cohesive.Storage.Processes.ProcessDurableCheckpoint` is the versioned physical aggregate for one logical Process
instance. It composes, rather than mirrors, the canonical start receipt, complete multi-token continuation,
`ProcessControlState`, committed activation receipts, cached host-operation results, durable inbox, logical
interaction outbox, and `DurableOperationState` ledger. Its outer physical schema, storage revision, worker lease,
and worker fence are persistence coordination evidence; they do not replace the Process definition revision,
semantic control revision, operation fence, or a provider ETag. `ProcessCheckpointCompatibilityValidator` checks
the exact definition identity, revision, fingerprint, restored-continuation and wait topology, inbox-disposition
provenance, and bidirectional trace/host-operation/outbox/Request-operation closure before host execution. Restored
Fork and Join state also proves derived occurrence identities, policy-shaped completion history, canonical winner
selection, and coherent resolved state. Interaction-emission trace evidence includes the canonical envelope content
fingerprint, so matching an `EmissionId` is insufficient to replace the payload, contract, origin, target, or
envelope kind. Cached host-operation results are a closed typed-value-or-error union; failed results cannot retain
emissions. Successful host-operation emissions retain exact Process definition, attempt, activation, token, node,
and operation-kind provenance, and every outbox entry has exactly one producing occurrence. Each attempt's
activation receipts form an exact before/after continuation-fingerprint chain: the first
receipt consumes the canonical clean start or restart and the current attempt's final receipt publishes the
checkpoint continuation. A zero-activation current attempt must itself be that exact clean continuation for the
pinned definition and invocation input.

`IProcessDurableStore` exposes one provider-neutral atomic aggregate boundary. A commit replaces the complete
checkpoint and composes eligible local mutations under an expected physical revision and exact live worker fence.
The commit identity and deterministic content fingerprint make an ambiguous exact retry replay its prior result;
reusing that identity for different content is an identity conflict. Activation receipts, operation receipts,
inbox dispositions, outbox history, publication attempts, acknowledgements, and durable Request states are
append-only or monotonic successor evidence. Physical attempt histories append new attempts, while the latest
attempt snapshot may advance only through its legal claim, dispatch, failure, acknowledgement, or resolution
stages; renewal or stage rollback is rejected. Providers test lease liveness against fresh physical clock evidence
at the commit boundary, so an earlier caller-retained semantic timestamp cannot extend an expired worker. Once an
attempt closes, no new logical activation, host-operation,
inbox-disposition, outbox, or Request-operation evidence may be attributed to it, while already-retained physical
publication and durable-operation attempts may continue their legal monotonic reconciliation progress. Activation
receipts are scoped by Process attempt and use attempt-local contiguous sequences, so restart resets the canonical
continuation count without erasing prior attempt evidence. Wait indexes and dispatch queues are projections of this
authority, not independent semantic state.

Inbox admission does not require a live worker. It deduplicates exact canonical input by logical `EmissionId` and
increments the same aggregate revision used by activation commits. Therefore an input racing wait registration or
consumption makes the worker's stale commit fail CAS and forces a reload; the input cannot disappear between a
separate registration and commit. The physical inbox receipt is an attributable projection of the canonical
semantic receipt. Each decision preserves a closed semantic reason—early, wait candidate, consumed, duplicate,
late, stale, missing target, superseded, identity conflict, terminal unconsumed, invalid envelope, or contract
mismatch—separately from the authored policy action such as observe, reject, dead-letter, or reuse. Pending input
may become Buffered and Buffered may reach one terminal disposition, but terminal evidence cannot be rewritten.
The v3 checkpoint schema makes both reason and action mandatory in continuation, trace, and physical inbox
evidence. Terminal continuations still admit late inputs durably for policy and audit; the
reference interpreter can classify such inputs without reopening a wait, while a general post-terminal durable
classification activation remains outside the current driver surface. The operation driver dispositions late or
stale Replies before admission, and cooperative cancellation atomically classifies every already-pending inbox
entry. `ProcessWaitRegistrationId` identifies one exact token wait occurrence. A null
target registration remains an intentional early-delivery address, while an exact stale or closed registration
cannot route to a later compatible wait on the same token.

`InMemoryProcessDurableStore` is a copy-on-write semantic oracle. Initialization, inbox admission, worker
acquisition, worker renewal, and aggregate commit each expose pre-boundary and post-boundary crash points. A crash
before publication exposes none of a staged mutation; a crash after publication but before return exposes all of
it, and the exact retry replays. Reclaiming an expired lease allocates a greater worker fence and permanently makes
the prior owner stale. A lease is live only from its inclusive claim time to its exclusive expiry; acquisition,
renewal, and commit observations cannot predate retained aggregate or latest-renewal evidence. This reference
contract promises atomic local persistence and logical idempotency. It does not promise physical exactly-once
external publication, and it is not itself a production durability provider.

A Process-invoked Transition first commits entity state and an exact operation receipt under the entity
repository's atomic authority. That receipt retains the typed outcome and canonical envelopes but declares the
Process outbox as their sole publication authority; it never appends those envelopes to the entity/API outbox.
Recovery replays the same receipt until one Process aggregate commit admits the operation result, continuation,
and canonical outbox records together. Domain-event publication advances only through a retained fenced attempt
and stable authority/contract/idempotency identity, while durable Requests use the same logical envelope through
the durable-operation driver. A crash after external publication may repeat physical delivery, but the stable
identity converges to one logical target consequence and one durable acknowledgement. Stale Process ownership is
rejected before a new publication attempt can be committed. Direct API invocation deliberately selects the entity
outbox instead, while sharing the same Transition intent lowering, exact contracts, payloads, and Transition
provenance. A repository that cannot atomically retain entity state and the Process handoff fails with structured
capability evidence before evaluating or mutating the Transition.

## Canonical Process lifecycle control

ARI-162 defines one protocol-neutral lifecycle surface in `Cohesive.Execution`: `Inspect`, `Signal`, `Pause`,
`Continue`, `RestartAttempt`, `Cancel`, and `Terminate`. Every mutating command carries a stable command identity,
logical idempotency key, attributable authorization evidence, provenance, and an expectation for the exact Process
attempt and semantic control revision. `ProcessControlRevision` is the optimistic lifecycle fence; it is distinct
from an external-operation ownership fence and from a Storage record version. Durable receipts for mutating and
Signal-admission commands make exact replay return the original decision before evaluating a now-stale expectation;
read-only Inspect creates no receipt. Conflicting reuse of a command identity or idempotency key and stale
concurrent commands produce structured diagnostics.

`ProcessControlState` is the versioned portable semantic authority for lifecycle mode, attempt lineage, finite
activation position, safe-point evidence, and accepted command receipts; Signal admissions are deterministic
projections of those receipts. Persisted histories and live commands use one pure lifecycle reducer, so state
admission rejects impossible mode, phase, attempt, revision, and chronology combinations. Work already inside an
activation reaches an explicit invariant-preserving safe point before Pause, RestartAttempt, or cooperative Cancel
takes effect. Pause and Continue retain the logical Process instance, current attempt, and every attempt affinity.
While paused, the durable operation driver starts no dispatch, redispatch, or reconciliation; work already admitted
before the pause may record only a legal monotonic successor under its existing physical attempt and fence.
RestartAttempt instead records explicit abandonment and cleanup for the prior attempt, creates one caller-selected
stable replacement attempt under the same Process instance, and does not inherit the old attempt's affinities.
Cancel closes cooperatively at a safe point; Terminate is an immediate, irreversible forced stop with explicit
cleanup. Pending cooperative safe-point actions do not silently replace one another; only Terminate may preempt the
pending action immediately. Recovery of the same attempt, replay of an observation, and explicit attempt restart
are therefore not collapsed into one operation.

The physical checkpoint retains prior-attempt activation receipts, host-operation receipts, inbox evidence,
outbox emissions, publication attempts, and durable operation history under their original attempt provenance.
Restart admits a new current attempt only when Control contains the exact causal abandonment and replacement
receipt; the replacement starts with a clean zero-activation continuation and cannot inherit the abandoned
attempt's waits, buffered inputs, Requests, or affinities.
Every pending or `Buffered` inbox entry present at the restart cut is atomically classified `Stale` under the
abandoned continuation and cannot enter the replacement attempt.

Signal commands wrap an already-canonical `SignalEnvelope`. Exact contract and target validation precede admission;
active attempts admit Signals for arbitration, paused or pausing attempts buffer them, and retiring or terminal
attempts reject them. Emission and scoped contract/idempotency identity prevent a replayed logical Signal from
creating another admission. The control protocol records admission evidence and an external realization intent;
the Storage reference driver now commits that admission to the same inbox and CAS domain consumed by Process
activation. Exact command replay is inert; a distinct command for an already-admitted logical Signal persists a
typed `SignalDuplicate` audit receipt without adding another inbox entry or reopening a consumed wait.
Conflicting content under the same Signal `EmissionId` is a structured identity conflict and leaves the checkpoint
unchanged.

`ProcessControlJsonSerializer` supplies strict canonical command, state, and versioned decision wires. Catalog-aware
reads link Signals and validate named reason details and attempt-affinity values through the catalog's retained shape
graph. First-time decision intents are admissible only at their exact latest receipt or observation cut; a later
state can retain the receipt for replay without being able to present it again as a fresh side-effecting result.

`ProcessAttemptAffinity` is deliberately generic and write-once. The reference index-rebuild Process binds its
current attempt to a deterministic candidate-generation value before physical creation, so every possible late
begin remains addressable by durable Process evidence. Pause and Continue retain that attempt and generation. A
materialization-specific lifecycle driver realizes RestartAttempt in an exact recoverable order: commit or replay
the canonical Process replacement, tombstone or retire the abandoned generation, bind the replacement affinity,
then idempotently begin the replacement candidate. Replaying the command resumes those same steps; it cannot
allocate a second generation. Cohesive.Storage remains the authority for allocating, persisting, excluding,
retiring, and eventually cleaning up or promoting physical generations. `ProcessDurableRuntime` continues to own
only canonical Process durability and does not perform target I/O inside its atomic control commit.

The outer planning chain retains a fingerprinted request, complete frozen membership, explicit subject-to-target
placement with separate physical-capacity evidence, a bounded effective scheduling realization, and one exact leaf
binding per independently promoted placement slice. Its linked plan set preserves the declared `Independent`,
`AllReadyProgressive`, or `AtomicVisibility` guarantee and the progressive failure policy when required. Link and
replay validation reject any substituted membership, pool, target, subject assignment, slice fingerprint, or leaf.

The baseline interpreter retains batch enumeration and incremental Channel progress as independent durable tracks.
Its fingerprinted leaf plan revalidates the complete materialization definition, retains the full exact placement
slice, and pins both canonical scan requests and the exact Relations physical plan used for hydration. The durable
plan reference and active-generation evidence carry the same slice fingerprint. The v1 page interpreter accepts
`OnePerRoot` and `ZeroOrOnePerRoot`; it rejects `Set` and unbounded `ManyPerRoot` outputs until their whole-set or
expansion semantics can be represented without weakening finite execution. For each stable root shard it captures
the initial change boundary, reads bounded source pages, hydrates complete Relations output, applies deterministic
per-item mutations in bounded bulks, and advances only the baseline track. Each checkpoint carries a cumulative one-based page ordinal that
survives activation and process crashes and cannot exceed the persisted shard bound. Exhausted `Partial`, `Failed`,
or `Inconclusive` source evidence stops before hydration or target I/O.

Crashes after scan, hydration, bulk application, or checkpoint replay the same page, mutation, and checkpoint
identities. If a post-bulk re-read produces different canonical target intent for that identity, the worker returns
terminal `RestartRequired`; it does not abandon or replace its generation. The candidate remains Loading and
unreadable until external Control issues `RestartAttempt`, whose lifecycle ordering commits or replays the Process
replacement, durably excludes the old generation, binds replacement affinity, and begins exactly one new candidate.
Once every shard has an exact completed baseline checkpoint and its retained change cut, baseline work returns
`baseline-complete/catch-up-required`; the candidate remains Loading and cannot serve reads. The synchronization
interpreter then reads every exact planned feed from its retained cut, projects changes through the linked impact
plan, durably prepares generation-wide monotonic target intent, applies bounded idempotent batches, commits the
application checkpoint, and only then settles a capable source. Effect-free position advances are checkpointed too.
A catalog-complete, fresh convergence receipt is the sole input to the activation interpreter, which persists and
reconciles seal, validation, and fenced target-local promotion in prefix order.

The rebuild plan also persists generation-scoped Control realizations. Typed adapter evidence can drive durable
AIMD recommendations; source, transform, and target concurrency or batch limits change only at declared safe points,
remain inside physical capability bounds, and retain a realtime-first non-preemptive admission reservation.
Pause/Continue retains that Control epoch, while a new generation starts a fresh epoch.

Target-local promotion does not silently change backend dependency routing. The in-memory reference router owns an
independent state machine for every exact placement-slice fingerprint: inspection, read/write resolution, commands,
proofs, receipts, revisions, ownership fences, routes, and lifecycle evidence all retain that scope. The strict
independent-promotion request binds the linked plan set, exact leaf and slice, active-generation evidence,
pre-admission revision, fence, command identities, and timestamps. Its executor admits the activated candidate and
atomically replaces that slice's paired read/write routes; exact recovery replays the retained request. Rollback still
requires current-slice equivalence evidence. Physical cleanup first reserves the shared generation under each router
authority that can address it, captures that router's placement-retirement claims, and terminally excludes future
admission there; each slice then acknowledges the same reservation-bound physical proof independently. A single pool
router does not claim global deletion authority across other pools or definition versions. Index-sync status schema v3
projects slice ID and fingerprint under the unique
`PlacementStatusPath` rather than a pool-global status key.

These behaviors are covered by provider-neutral planning, routing, Process, and adapter component tests plus the
ARI-180 deterministic, production-shaped Cosmos DB/PostgreSQL-to-Elasticsearch vertical slices. Both providers
execute the same canonical leaf materialization through bounded baseline recovery, incremental update/delete
convergence, adaptive Elasticsearch rejection feedback, target-local activation, canonical active-alias row and
exact-count readback, restart generation isolation, and an explicit route swap. Production durable Process and
backend-routing stores, a durable plan-set scheduler, and coordination interpreters for `AllReadyProgressive` and
`AtomicVisibility` must still pass the corresponding matrices before those concrete realizations can claim
qualification; provider-neutral EK-08 semantic conformance remains **Pass**.

## Canonical finite Process IR

ARI-167 introduces `Cohesive.Processes.IR.ProcessDefinition` as the persisted semantic authority for Process
coordination. It uses the shared execution-definition envelope and fingerprint model rather than defining another
Process document. The normalized graph has stable node and edge identities, one typed invocation input, a typed
terminal result, explicit recovery policy, typed continuation bindings, ordered Choice/Match cases, normalized
Request outcomes, normalized Fork branches, normalized AwaitMatch clauses, exact child protocols, bounded
partition-work limits, and durable recurrence policies. Its closed node union contains Transition invocation,
Relation/Query evaluation, Request, domain-event emission, Signal send, Choice, Match, Fork, Join, AwaitMatch,
Timer, Reply, explicit durable cut, child Process invocation, bounded partition child work, durable recurrence,
Return, and Fail.

Transition, Relation/Query, and child Process nodes carry exact `ExecutionDefinitionReference` values. Linking
supplies derived input/result contract and child-dependency evidence through `ProcessDefinitionValidationContext`;
the referenced definition remains the authority and is not copied into Process IR. Request, child Process, event,
Signal, Reply, and AwaitMatch nodes use exact typed interaction references resolved through
`InteractionContractCatalog`. Expressions can observe only the Process input and definitely available typed
continuation bindings, and Process v2 pins the same explicit pure capability profile as Transition v1. An inbound
Request clause separately binds its application payload and its admitted
logical Request obligation; Reply consumes that definitely visible obligation and must link back to the exact
Request contract. Aggregate state, relation execution artifacts,
interaction definitions, runtime services, delegates, adapters, and compiled plans are outside the canonical
closure.

`ProcessDefinitionValidator` checks portable contracts and expressions, exact link families, interaction payloads
and outcomes, stable construct and edge identity, edge targets, reachability, definite binding flow, Request outcome
coverage, conservative Choice/Match exhaustiveness proof, Fork/Join reciprocity, token-owned ingress and
convergence, AwaitMatch arbitration, Request/Reply obligation continuity, child outcome and dependency closure,
bounded work limits, recurrence progress limits, and deterministic policy validity. An All Join exposes values
guaranteed by every completed branch; partial Joins retain only the pre-Fork value scope until an explicit
aggregation construct is introduced. It also builds a same-activation graph in which Request, InvokeProcess,
ForEachPartition, RepeatAcrossActivation, AwaitMatch, Timer, and explicit durable cut are barriers. Every activation
path must be acyclic and must reach a terminal node or one of those durable barriers. Recurrence is therefore
explicit and valid only across a persisted continuation boundary; a durable boundary on one branch does not hide a
free cycle on another. A Fork branch may contain durable recurrence only when every finite exit remains owned by
and converges on its reciprocal Join and the branch has a structural Join exit.

`ProcessStaticCompiler` admits only a fully validated exact document and produces an indexed plan without copying
semantic authority. `ProcessReferenceInterpreter` is a pure immutable reducer over that plan: it starts with one
stable root token, schedules ready tokens in ordinal token-identity order, executes one node quantum per scheduling
round, and defers Join and AwaitMatch arbitration to deterministic round boundaries. Fork children, wait
registrations, emissions, and idempotency keys use versioned, purpose-separated deterministic identities. Every
activation ends at the first deterministic durable boundary, terminal outcome, or complete quiescent continuation;
it returns interaction intents and a provenance-bearing trace rather than performing I/O.

Join completion sequence is retained only when the canonical policy declares completion order observable;
validation rejects a completion-order tie-break paired with unobservable order. Inbound Request obligations are
linear: an obligation visible before a Fork cannot be consumed by a Reply inside that parallel region, and a Reply
discharges the logical obligation across every token and retained Fork parent so it cannot be duplicated or
resurrected by a later Join.

The reference continuation retains the complete token set, typed token-local bindings and Request obligations,
Fork/Join membership and branch dispositions, replay-stable child invocations, bounded partition work, recurrence
progress, computed timer deadlines, active waits and tombstones with their exact token-step identity basis, early inputs, input-disposition receipts,
outstanding logical Requests, terminal outcome, and exact Process fingerprint. It is
the semantic input to the Storage-owned durable checkpoint, not itself a claim of physical durability. The
synchronous host port supplies explicit
Transition, Relation/Query, and Signal-target evidence, while cancellation is observed only at an activation safe
point; no `CancellationToken`, task, repository, clock, or provider type enters canonical state.

Presented inputs are grouped by logical emission identity before state mutation, so conflicting same-batch evidence
cannot acquire caller-order authority. Every admission receipt and `InputAdmitted` trace separates the closed
semantic classification from the policy disposition it produced; changing a wait policy may change the latter but
must not relabel late, stale, duplicate, or superseded evidence. Cancellation-bearing activations admit their input
evidence before applying cancellation at the entry safe point, and every token-terminal path dispositions remaining
buffered inputs instead of retaining impossible `Buffered` state. A `RestartAttempt` recovery never resumes the abandoned continuation;
`ProcessReferenceInterpreter.RestartAttempt` creates a clean token set under a controller-supplied replacement
attempt identity while retaining the exact Process definition and invocation input.

Process IR v2 still has no canonical authored nested-scope construct. Target-independent compilation can demand
`ProcessAtomicScopeDemand.WholeDefinition`: it derives deterministic effect and resource evidence, rejects durable
waits and external, child, or emission-capable host interactions, and retains the demand for downstream capability
proof. Passing that structural preflight is not target realization evidence; a target compiler must still prove the
requested atomic guarantee. EK-05 whole-definition preflight is therefore explicit, while authored nested scopes
and concrete target realization remain deferred. Interaction targets carry an optional exact
`ProcessWaitRegistrationId`: exact targets cannot cross wait occurrences, while a null occurrence is the explicit
early-delivery form. `ProcessDurableRuntime` realizes finite activation, lifecycle, inbox/outbox, and durable Request
cuts over the canonical checkpoint/store contracts. `GenerateProcessDefinition` is the primary human-facing C#
producer for that same IR: its syntax-only method is never invoked, pure expressions and member access lower to the
portable expression closure, and generated construction state is discarded before the resulting handle is returned.
`ProcessAuthoring.Create` and `ProcessBuilder<TInput,TResult>` remain advanced lowering/import escape hatches rather
than the application-authoring default. Every frontend returns only the canonical execution-definition document and
validation result. Direct IR, generated C#, and advanced builder definitions normalize to the same fingerprint.

## Compatibility and retired surfaces

### Flat transitions

The former `Cohesive.Transitions.Model.TransitionDefinition` was a serialized set of parallel collections: `Inputs`, `Preconditions`, `Updates`, and `Effects`. Its runtime applied preconditions, sequential assignments, computed fields, invariants, and then every declared effect. Conditional expressions existed inside those collections, but there was no structured body containing branch nodes or stable path identity. Static analysis unioned referenced fields and could not report must/may/actual access or branch provenance.

Canonical persisted semantic authority belongs to `Cohesive.Transitions.IR`. `TransitionAuthoring` and its typed canonical builders are producers of that authority and retain no executable callback. The flat definition, two-parameter `Transition` handle, expression builder/compiler, local apply runtime, CLR effect requests and handlers, continuation snapshots, host telemetry wrapper, and their compatibility fixtures have been removed. `EntityDefinition` now owns entity shape and invariant semantics only and cannot expose a competing transition catalog.

Migration disposition: author or import canonical Transition IR, persist its `ExecutionDefinitionDocument`, compile it with `TransitionStaticCompiler`, and interpret the resulting plan. External effects are exact interaction emissions interpreted by a Process or adapter boundary; they are not name-dispatched CLR callbacks. No implicit reader for the retired flat definition is shipped.

### Retired delegate-bearing processes

Canonical persisted Process authority now belongs to `Cohesive.Processes.IR.ProcessDefinition`: a normalized,
typed graph with stable node and edge identities, portable expressions, exact Transition, Relation/Query, and
interaction references, explicit Fork/Join and AwaitMatch policies, and durable cuts. Its validator proves exact
reference and binding compatibility, graph integrity, branch/join structure, and finite same-activation execution.
It carries coordination facts only and does not copy aggregate business state, callbacks, suspended host frames,
runtime services, adapter state, or compiled plans.

The former `Cohesive.Processes.Model.ProcessDefinition` stored executable node objects whose branch predicates,
entity references, transition inputs, Request construction, waits, computations, and terminal results were CLR
`Func` delegates. The former DurableTask orchestration resolved definitions by name from a local registry and
re-evaluated those delegates during replay. Those types, their source generator, local execution engine, and
delegate-consuming adapter entry points have been removed. They are not compatibility inputs and cannot compete
with the canonical definition.

Migration disposition: author or import `Cohesive.Processes.IR.ProcessDefinition`, persist it in an
`ExecutionDefinitionDocument`, compile with `ProcessStaticCompiler`, and execute the resulting plan with a
conforming interpreter. The typed C# frontend does exactly that. Adapter mechanisms may execute explicit compiled
operations, but may not revive runtime delegates or registry-by-name definition authority.

### Single execution cursor

The former `Cohesive.Processes.Runtime.ProcessCheckpoint` persisted one `CurrentNode` plus a locality continuation stack. It had no token set, fork/join state, definition fingerprint, integrated process attempt or activation identity, durable wait inbox, operation ledger, canonical control state, compensation state, or generation-affinity binding. `ProcessDurableCheckpoint` is the physical aggregate and composes the canonical continuation, control, interaction, and durable-operation authorities under one atomic store boundary. The retired checkpoint neither embedded nor atomically committed those authorities. Its `ProcessDefinition` also accepted unrestricted control-flow cycles.

Migration disposition: the old checkpoint and its executor have been removed; no implicit
compatibility reader is provided. Any future offline migration tool must treat the old value as an explicitly
versioned import format and produce a newly validated canonical continuation. New work targets
`ProcessDurableCheckpoint`, `IProcessDurableStore`, and `ProcessDurableRuntime`. Affinity slots and generation
bindings derive from canonical Process IR and owning-block contracts; parallelism and generation recovery are never
inferred from the old cursor.

## Characterized runtime paths

| Area | Current types and runtime paths |
| --- | --- |
| Canonical transition semantics | `Cohesive.Transitions.IR` structured definitions, validation, and shared execution-definition persistence |
| Canonical Transition C# authoring | `TransitionAuthoring.Create` + `TransitionBuilder<TEntity, TInput, TOutcome>` → canonical `ExecutionDefinitionDocument`; strict unsupported-syntax rejection and `ExecutionSourceMap` attribution |
| Canonical transition compilation | `TransitionStaticCompiler` → `CompiledTransitionPlan`, including path-sensitive requirements, computed-field order, and exact `TransitionMachineEdgeLink` slices |
| Reference transition interpretation | `TransitionReferenceInterpreter.Decide`, `DecideFullState`, and `DecideSparse` → `TransitionDecision` plus `TransitionExecutionEvidence` |
| Canonical transition activation | `ExecutionDefinitionDocument` → `TransitionStaticCompiler` → `TransitionReferenceInterpreter`; no producer assembly or authoring callback is required |
| Canonical interaction contracts | `InteractionContractDefinition`, `InteractionContractDocuments`, and `InteractionContractCatalog` → exact typed domain-event, Request, Signal, and Reply contracts with portable schemas and Request obligations |
| Canonical interaction envelopes | `DomainEventEnvelope`, `RequestEnvelope`, `SignalEnvelope`, and `ReplyEnvelope` → `InteractionEnvelopeValidator` and `InteractionEnvelopeJsonSerializer`; strict portable representation plus optional exact `ProcessWaitRegistrationId` targeting exists, and `ProcessDurableCheckpoint` retains envelopes as the inbox/outbox authority |
| Canonical durable Request protocol | `DurableRequestBinding`, `DurableOperationState`, `IDurableOperationAdapter`, `IDurableOperationBatchAdapter`, `DurableOperationReferenceExecutor`, and `ProcessDurableRuntime.AdvanceOperationAsync` → exact Reply binding, scoped logical deduplication, fenced claims and attempts, attempt/failure evidence, typed timeout/cancellation requirements, recovery identities, reconciliation, acknowledgement, and result admission; the Process checkpoint/store atomically compose origin, operation, and Reply cuts |
| Canonical Process lifecycle control | `ProcessControlCommand`, `ProcessControlState`, `ProcessControlDecision`, `ProcessControlJsonSerializer`, `ProcessControlReferenceExecutor`, and `ProcessDurableRuntime` → protocol-neutral Inspect/Signal/Pause/Continue/RestartAttempt/Cancel/Terminate semantics, stable command identity and idempotency, exact attempt/revision fencing, replay and typed no-op receipts, safe-point lifecycle, attempt lineage, durable Signal admission, cancellation terminal composition, and write-once attempt affinity; index-sync work still owns physical generation lifecycle |
| Canonical Process C# authoring | `GenerateProcessDefinition` + syntax-only `ProcessContext` is the primary human-facing frontend: ordinary C# `await`, pure locals, `if`/`else`, exact `switch`, named local branches, typed `AwaitMatch` result families, typed Fork/Join, bounded work, child invocation, recurrence, and terminal flow lower to canonical `ExecutionDefinitionDocument`. A typed wait's exhaustive type switch fuses directly into canonical clause continuations; its CLR base/case family, local value, and patterns are erased, while exact clause contracts, inputs, timer expressions, policies, identities, and provenance remain. Pure locals fuse into the fixed portable expression closure; callbacks and host-language state machines do not become Process nodes. `ProcessAuthoring.Create` + `ProcessBuilder<TInput,TResult>` remains an advanced generator/import/test escape hatch for the same closed node union and fingerprint. |
| Canonical Process semantics | `Cohesive.Processes.IR.ProcessDefinition`, `ProcessStaticCompiler`, `ProcessContinuationState`, `ProcessReferenceInterpreter`, `ProcessContinuationValidator`, `ProcessDurableCheckpoint`, `IProcessDurableStore`, and `ProcessDurableRuntime` → validated exact finite-activation plans, immutable multi-token continuations, deterministic operations/Fork/Join/waits/Requests/interactions, compatibility-first restore, exact activation/host-operation replay, atomic checkpoint/inbox/outbox/operation persistence, ambiguous exact mutation retry, and crash-testable CAS/fencing |
| Target-neutral Process interpreter realization | `ProcessInterpreterRequirementCollector`, `ProcessInterpreterCapabilityProfile`, `ProcessInterpreterRealizationCompiler`, and `ProcessInterpreterRealizationLedger` → construct inventory projected from the closed persisted node union, compiler-derived cross-cutting guarantee demands, target-owned native/composed/constrained/unavailable evidence, structured mismatch diagnostics, and exact one-decision-per-inventory-item coverage. No target adapter is executable merely because these contracts can assess its profile. |
| Durable Task Process realization planning | `DurableTaskProcessTargetProfile`, `DurableTaskProcessRealizationCompiler`, and `DurableTaskProcessRealizationPlan` → versioned Durable Task dispositions for the complete current construct/guarantee catalogs, exact canonical-plan retention, requirement/node/link/evidence attribution, deterministic plan construction, and pre-plan rejection of unavailable semantics. Planning the complete target direction remains distinct from admission to the narrower executable slice. |
| Retired flat Transition authority | Flat `Cohesive.Transitions.Model.TransitionDefinition`, two-parameter `Transition`, local apply runtime, generic CLR effects/continuations, host wrappers, and legacy-only tests have been removed; Git history is the recovery path |
| Canonical durable Process realization | `Cohesive.Storage.Processes.ProcessDurableRuntime` + `IProcessDurableStore` → compiled canonical plans, exact continuation/control/operation authority, atomic persistence, crash recovery, and lifecycle admission |
| Durable Task execution and monitoring | `DurableTaskSequentialProcessPlanCatalog` + `DurableTaskSequentialProcessOrchestrator` → exact-plan execution for Transition, Relation/Query, Request, Signal send to a Process token, Choice, Match, bounded Fork/Join, AwaitMatch, Timer, child Process, bounded partition, bounded recurrence, Durable Cut, Return, and Fail through the standalone SDK. Bounded activities materialize host-operation and Signal-target evidence into the canonical reference interpreter; concurrent branch Requests and child sub-orchestrations retain canonical target admission, branch selection, child lineage, and `Propagate`/`Detach` cancellation policy. Persisted canonical wait and due-instant state remains semantic authority while replayable Durable Task external events and timers provide physical stimuli. The exact canonical Signal envelope is routed unchanged, but recipient target validation, arbitration, and early/late/stale/duplicate/missing-target/consumed dispositions remain reference-interpreter decisions; provider delivery is not canonical admission. Co-ready AwaitMatch input/timer stimuli enter one canonical activation; the reference interpreter retains guard, priority, clause tie-break, winner, and policy authority. Bound Requests reuse `DurableOperationReferenceExecutor` for stable identities, claims, dispatch markers, retry, reconciliation, acknowledgement, and Reply admission; at-least-once activities require target deduplication or natural idempotence and authored recovery that cannot yet run fails closed with its canonical ledger. Partition and recurrence bounds fail closed, while Continue-as-new carries the complete canonical cut state without becoming semantic authority. Exact definition tuples survive replay, unsupported constructs fail catalog admission, and `ProcessExecutionStatusProjector` publishes safe protocol-neutral `ExecutionStatus` custom status from canonical continuation/control state with terminal detail redacted. Scheduler custom status omits commands, receipts, interactions, operation ledgers, wait keys, traces, and input/output payloads. Every newly executed finite activation projects the existing payload-safe `NormalizedExecutionTrace` from its authoritative canonical decision; results retain those traces in activation order across replay, attempt replacement, and Continue-as-new, while pre-retention gaps are not fabricated from provider history. New schedules add a versioned immutable payload-free Scheduler tag projection for logical Process and exact definition discovery; mutable lifecycle/location stays in canonical custom status. `DurableTaskProcessExecutionRepository` queries current executions through the standalone client, returns exact canonical status without projecting fetched Process input/output or provider failure bodies, validates physical/start/status/tag affinity, and retains the Core query-client constructor only for migrated historical wire shapes. Physical task-hub identity and logical Process identity remain explicit and distinct; trusted authority scope plus logical identity deterministically derives one exact current lookup without a page scan. The pinned emulator proves tag round-trip plus live and terminal logical repository retrieval. Trace/explain retrieval, richer dashboard presentation, and history-event normalization remain incomplete. Domain-event/Reply emission, activation-local and non-Process Signal targets, external Signal adapters, and complete observability are not yet promoted. |
| Retired Process authority | `Cohesive.Processes.Model`, its source generator, local runtime/planner/single-cursor checkpoint, delegate ASP.NET start routes, DurableTask execution host, legacy Storage entity adapter, and their legacy-only tests have been removed; Git history is the recovery path |

## Characterization policy

The executable inventory locks normative canonical behavior, not retired implementation accidents. ARI-170 removes
the unsafe Process characterizations for re-evaluated branch delegates, repeatable checkpoint consumption,
duplicate raw-signal buffering, unrestricted cycles, and same-name definition replacement. Their replacements
assert typed C#/direct-IR equivalence, strict persisted-document round trips, source-mapped diagnostics, closed-union
authoring coverage, and absence of the competing authorities from shipped assemblies. Do not restore a retired gap
as a compatibility behavior merely to keep historical source executable.

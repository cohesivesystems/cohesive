# Declarative services and transport-independent invocation

Status: implementation in progress, 2026-09-27.

## Required semantics

A service declares a versioned set of available operations and is a logical deployment and runtime
binding boundary. An operation references its exact semantic authority. Transitions own atomic entity
changes, Processes own multi-step coordination, and relation queries own cross-entity reads. A service
must not introduce another workflow language or an uninspectable preparation callback.

The runtime owns authoritative loading and conditional persistence. Public callers provide subject
identity, typed input and an opaque expected storage token, never a trusted entity snapshot. Identity,
selected scope, permission grants and cancellation remain invocation-scoped. Entity version and storage
concurrency token retain their distinct meanings. HTTP projects the invocation and adds transport
configuration; it must not be the only enforcement boundary.

## Candidates and reuse map

| Existing authority | Fit and disposition |
| --- | --- |
| `Cohesive.Api` operations, result kinds, authorization requirements and semantic references | Extend/project; these already separate semantics from HTTP. Do not duplicate endpoint/result/policy catalogs. Existing CLR type references are authoring/projection artifacts, not a portable service authority. |
| `TransitionDefinition`, compiled plans and reference interpreter | Use unchanged as operation behavior, input/observation contracts and exact definition authority. |
| ASP.NET `TransitionEntityApiOperationBinding` | Extract reusable loading/decision/commit semantics; retain request parsing, HTTP response projection and native host configuration in the adapter. |
| Storage Process-to-Transition operation adapter | Reuse atomicity and capability semantics where applicable. Its occurrence receipt is Process-owned; do not invent a Process occurrence for an ordinary service call or claim replay guarantees from ordinary upsert. |
| `IEntityRepository` and atomic outbox repository | Use existing conditional writes and capability contracts. Fail unsupported emission/commit requirements rather than weakening them. |
| Identity context and API authority metadata | Reuse normalized identity and declared policy requirements; resource authorization consumes the runtime-owned snapshot. |
| Execution telemetry, Transition trace/explain, operation telemetry emitter | Compose at their owning boundaries. Observer failure cannot change domain results. Required durable audit is separate from sampled telemetry. |
| `Cohesive.Infra` definitions, capability requirements, associations, compilation and readiness | Reuse for deployment realization and evidence. Service requirements reference these declarations; no second provider option or binding catalog. |

## Delivery boundary

First qualify a transition-backed direct invocation and HTTP projection with one declaration and shared
runtime, including authorization seams, conditional commits, structured failure evidence and tests.
Use a representative non-Ari entity. Portable declaration validation/fingerprinting must be deterministic;
compiled/runtime bindings may carry native dependencies but portable declarations may not carry closures.

Do not claim Process/query composition, durable retries, exactly-once execution, stronger policy revocation,
or arbitrary computation support before their separate contracts and qualification exist. Subsequent
composition references the existing owning languages. Ari adoption removes its temporary review
coordinator only after parity across protocols, persistence, authorization and observable outcomes.

## Example and qualification target

A shipment exposes its declared Dispatch transition. A direct caller and an HTTP caller both select the
shipment and supply its expected token. The shared runtime authorizes the selected scope, loads exactly
one authoritative snapshot, authorizes that resource, decides the transition and conditionally commits.
If a competing caller changed the shipment, the stale invocation returns a structured concurrency
rejection without committing. Inspection identifies the declared operation and rejection boundary without
exporting shipment contents or credentials. This is a qualification target until executable tests pass.

## Implemented boundary

`Cohesive.Api.Services.ServiceDefinition` is a typed payload in the existing
`ExecutionDefinitionDocument` envelope. The envelope retains identity, revision, provenance and canonical
fingerprinting. Operations and authority requirements normalize to ordinal identity order. Each operation
has a declared semantic family and references its exact Transition, relation/query or Process authority.
Transition operations also name their qualified entity state identity. Input, observation, result and
workflow contracts stay with those referenced definitions. No service-specific serializer, fingerprint algorithm or metadata
envelope is introduced.

`Cohesive.Api.Execution.Services.ServiceRuntime` validates the document and complete binding
set once, checks the observation contract against the bound entity, and retains prepared immutable plans.
Repository factories remain invocation-scoped and are never called during construction or route mapping.
`Project<TInput,TOutcome>` verifies that CLR API views match the Transition's contracts before projecting
existing API endpoint, authority and result metadata. ASP.NET supplies route, typed body and token-header
handling through `MapServiceTransition`; it invokes the same runtime as direct callers.

The initial executor supports present-subject Transitions without interaction emissions or semantic
extensions. Those unsupported requirements fail binding. It does not claim durable idempotency, creation,
atomic outbox, policy-revocation fencing, Process/query composition, or native computation composition.
The existing emitting ASP.NET path remains in place until a later shared-runtime profile proves parity;
it is not silently redirected into this constrained profile.

Authority is mandatory. The reusable normalized-identity binding requires explicit, unexpired grants to
the actor, checks every declared capability, derives physical routing from grants, and compares logical
scope ownership on the exact loaded snapshot. It denies delegation until a separate qualified policy is
provided. Resource authorization is deliberately independent of physical partition equality. Hosts still
own credential verification, normalized grant acquisition and any native HTTP policy associations.

Each completed invocation returns existing `NormalizedExecutionTrace` evidence identifying its exact
service revision, activation, declared operation and related Transition. Stage events distinguish coarse
admission, loading, resource authorization, decision and commit. A rejected load token and a competing
conditional write have different diagnostic locations; neither claims a completed commit. Trace evidence
contains no inputs, entity IDs, owner values or raw credentials. Native Transition evidence is projected into its existing payload-free normalized trace. Internal loaded
snapshots and decision input/observation payloads are excluded from both direct and HTTP invocation results. Optional native execution activities correlate the service trace; observer
failures cannot change a committed result. This is inspection evidence, not a durable audit receipt.

The HTTP convention returns the Transition outcome and places the opaque next token in
`X-Expected-Concurrency-Token`. It does not serialize internal snapshots or traces into HTTP results.
Callers send the token verbatim in the same header. Missing subject/token fails before invocation.
Standard API result conventions own status codes; no service-local status table is maintained.

Qualification currently uses a note-revision Transition: both direct and HTTP calls update its Text field
under the same policy and expected token. Tests cover rejected admission, resource ownership, expired or
unrelated grants, shared physical partitions, untrusted selected placement, cancellation, stale reads,
commit conflict, partial snapshots, canonical order, persisted documents, checked API contracts and
observer failure. The shipment example above remains illustrative; Ari review adoption is still pending.

Native failure diagnostics retain stable code and location but suppress payload-bearing message/evidence
at the invocation boundary. Detailed native decision payloads require a separate explicitly authorized
inspection interpretation; sampled telemetry never exports exception messages. HTTP object naming and
bytes use the checked CLR outcome view and native response serialization.


## Composition

The service operation set distinguishes Transition, query, Process-entry and native Process-control references in portable data.
Common identity and authorization requirements have one owner; operation IDs are unique across all
families. The existing Transition executor still rejects other families before resolving infrastructure.
Round-trip tests establish declaration integrity; execution and recovery qualification are described below.

The composition executor reuses `RelationQueryCompilationRequest` and `IRelationQueryEvaluator`
for actual canonical queries, and existing Process start/control admission plus durable executors for
coordination. A query's compiled semantic snapshot is reusable; its evaluation identity, authorized
scope, parameter evidence and source reads remain invocation-scoped. Caller parameter values must not
replace trusted scope bindings. Tests must count physical reads and prove logical scope isolation,
including shared physical partitions.

Existing `HostedQuery` and `ProcessRelationHandlerCatalog` already provide exact implementation identity,
typed input/output admission and Process integration. The hosted Query boundary now carries an explicit deterministic-computation contract and requires a
matching native registration. Ordinary observation handlers still do not establish purity; wrapping review
orchestration in one would not satisfy that requirement. No new interpreter,
synthetic Process occurrence, or independently maintained workflow order is planned.


### Query invocation qualification

`ServiceRuntime` now admits heterogeneous binding families against one exact service document. The
normalized-identity authority returns a trusted `ScopeRef`; Transition reads derive physical routing
from it, while query operations supply its logical identity to their declared `ScopeParameter`.
That parameter is a native `QueryParameterId`, must be a required string in the pinned query, and
cannot be supplied or replaced by request values. Query declarations own the actual ownership
predicate: parameter binding is not a proof that an arbitrary query enforces authorization. Review
the predicate and all source placements when authoring an exposed query.

`ServiceQueryBinding` retains one immutable native `RelationQueryCompilationRequest` and resolves an
`IRelationQueryEvaluator` only after authorization and parameter validation. It does not reimplement
compilation, realization, acquisition or query interpretation. The native phase outcome is retained
rather than copied into a service-specific row model. Transition and query invocations share one
payload-free evidence collector, projected to existing execution activities and normalized traces.

The synthetic notes test uses two logical tenants sharing one physical partition. The canonical
query filters on the server-bound tenant parameter; repeated invocations reuse the same compilation
snapshot, return only the selected tenant's note and perform one native in-memory source read each.
Denied callers, supplied scope overrides, unknown parameters and pre-cancellation perform no reads.
This establishes the in-memory boundary; it does not claim remote-provider predicate pushdown or
remote page-count qualification. Process invocation and recovery qualification remain in progress.


### Process admission qualification

`ServiceProcessBinding` associates an exact compiled Process with the existing
`ExecutionProcessStartDispatcher`. Service admission preserves the caller's native command,
idempotency and continuation identities, but replaces authority, issuance and provenance using
normalized identity, admitted logical scope and the service document. The native dispatcher retains
ownership of start receipts, replay evidence and scheduling; the service adds no receipt store or
workflow loop. The result pairs the service trace with the native admission continuation and exact definition. A
service admission is not itself a Process activation: it has no durable Process token and must not
populate the normalized trace's owning-Process continuation field.

Focused tests use `InMemoryExecutionControlApiAdapter` to prove accepted admission, exact replay,
forged-authority replacement and rejection of wrong definitions or invalid input before dispatch.
The native public `ProcessStartResult` is retained without exposing receipt payloads. A common
`ServiceOperationResult<TOutcome>` carries native query/Process evidence, structured admission
failures and a service trace. Runtime failures and cancellation remain observable exceptions.
These tests qualify admission composition only. Native computation, durable recovery and lifecycle control
qualification follow below. Transport projection for new operation families remains open.


### Native computation qualification

Extend `HostedQueryDefinition` with explicit `DeterministicComputation` semantics rather than inventing another
execution language. The existing query document owns input/output, configuration, exact implementation and
dependencies; its fingerprint now also covers the non-default computation guarantee. The observation default
retains its established canonical wire shape. `CreateDeterministic` binds that exact capability through the
existing typed handler/catalog/interpreter path and checks the implementation version. It does not receive
infrastructure, time or identity context. Purity remains an audited implementation contract, not a CLR sandbox.

The tested example declares acquisition followed by normalization. The native async interpreter produces
`normalized:ACQUIRED`, invokes each step once and preserves exact query references. Cancellation aborts a
computation without emitting an alternative successful result. This is the boundary intended for native relation
acceptance after the Process has acquired exact graph documents and a draft revision. Ari's acceptance-specific
inputs, retained evidence and commit fence still require adoption qualification.


### Async durable hosting qualification

Extend Storage's existing `ProcessDurableRuntime` to accept `IAsyncProcessReferenceHost`; retain its direct
synchronous path. Both profiles share exact occurrence receipts, checkpoint reduction and telemetry. Cancellation
finalization and child-start prevention use the selected profile too. This closes a reusable integration gap between
typed query/computation catalogs and the durable driver without adding a service-local workflow coordinator.

The tested example awaits a query, loses the acknowledgment of the Process aggregate commit, and recreates the
runtime. The retry uses committed evidence with zero additional query calls. Cancellation after a host await leaves
no partial Process checkpoint. The existing entity handoff conformance scenario now runs through both profiles:
a crash after the entity commit replays its atomic entity receipt, and later Process commit/publication failures
converge without duplicate logical publication. These are in-memory provider conformance tests, not a remote
backend durability claim. A combined service with acquisition, computation, an explicit durable cut and mutations
across multiple entities remains the next qualification gate.


### Combined service and multi-entity recovery example

`ServiceCompositionTests` declares a document-publishing service with `revise`, `search` and `publish`
operations in one service document and binding set. The query filters by the admitted logical tenant in a
shared physical partition. A Process acquires a pair through that canonical query, computes normalized text,
and commits an explicit durable cut before invoking the same entity Transition for each document. The hosted
acquisition declaration pins its query dependency; selecting the pair is domain-specific result projection.
The native Process owns sequencing, intermediate values, operation identities and recovery.

Tested example: an editor changes document `a` to `revised`, then publishes `a` and `b`. The worker crashes
after committing `a` but before committing its Process activation. A new runtime reuses the entity receipt,
updates `b`, and returns `REVISED` / `BETA`. Acquisition and computation ran once; the first document advanced
only one additional version. A foreign tenant's document is excluded and unchanged. Retrying the completed
activation performs no source read or computation.

An acquired revision is an explicit Transition precondition, with repository compare-and-swap protecting the
subsequent write. A newer edit after the cut is retained. Rejection on the first document stops the batch;
rejection on the second leaves the first update committed and returns false. This example deliberately has
no authored compensation or cross-entity transaction. Production workflows needing compensation must declare
it through their Process rather than infer it from service binding. The test's start dispatcher qualifies one
native admission plus initialization; concurrent durable start-registry behavior remains the deployed dispatcher's
responsibility and is not inferred from this fixture.


### Declared Process controls and exact target admission

`ServiceProcessControlOperation` references an exact Process plus an action from the existing native control
vocabulary. `ServiceProcessControlBinding` derives the request CLR type from `ExecutionControlApiCatalog`;
no new action enumeration, request model, receipt store or control reducer is introduced. Start and control
share normalized identity/scope admission. Control returns the existing safe `ExecutionControlResult` and
payload-free service evidence. Wrong command kinds and unauthorized callers do not dispatch.

A reusable gap in native admission was the lack of an exact-definition restriction: a command names an
instance and expected control revision, but a service must also restrict which Process that instance runs.
`ExecutionApiInvocationContext.ExpectedProcessDefinition` supplies trusted binding evidence. The in-memory
adapter checks authoritative state before mutation or retained replay; the shared command-rebinding boundary
also rejects mismatches. Durable Task checks its immutable start index before reading a cached response or
sending a control event. Unrestricted callers retain the existing behavior; the absent field is omitted from
serialization. Diagnostic projections verify the definition before returning evidence, and Control-limit
updates reject a Process-specific restriction because their target is a different authority.

Tested example: `pause` requires `notes.pause`, accepts only the native Pause command and is bound to the
notes publishing Process. A forged actor/scope is replaced by trusted invocation evidence. A wrong Process
revision is hidden as NotFound before mutation and cannot retrieve an earlier successful receipt; the exact
command/definition replays without advancing the control revision. The current lifecycle binding supports
Pause, Continue, RestartAttempt, Cancel and Terminate. Read projections and Signal ingress require their own
qualified bindings; unsupported actions fail binding early. HTTP projection remains follow-up work.


### Process API and HTTP projection

`ProjectProcess<TRequest>` derives request types, native result variants, scope policies and semantic
references from `ExecutionControlApiCatalog`. The service document supplies endpoint identity and required
capabilities. The exact native request type is checked at projection time. No repository or dispatcher is
resolved during mapping. ASP.NET attaches the full service declaration as route metadata and uses the existing
Process request reader and result selector.

`MapServiceProcessStart` and `MapServiceProcessControl<TCommand>` call the same shared runtime as direct
invocation. They retain native start/control result bodies and use the existing `ExecutionApiProblem` for
service admission failure. Start already declares that validation result; lifecycle controls add it alongside
the native validation decision. OpenAPI derives both distinct 400-body alternatives from those result declarations.
No transport-specific command DTO, control-action switch or status-code table is introduced.

Tested example: POST a native Process-start request to `/notes/publish`, then POST the native Pause command to
`/notes/pause`. The second identical pause replays at the same control revision. A forged actor remains replaced
by trusted service identity, and start input/forged authorization do not appear in returned bodies. A denied
start returns 403, while an invalid portable input returns the declared 400 problem without dispatch. Tests
exercise endpoint delegates and generated OpenAPI; they do not claim a deployed authentication middleware or
live provider qualification. Query transport projection remains open.

### Captured concurrency across preparation

A Process may prepare a mutation from an earlier read. The entity adapter's ordinary read-then-CAS
protects races during commit, but does not by itself reject changes between preparation and that read.
`ProcessTransitionOperationBinding.ExpectedConcurrencyTokenField` optionally selects a required,
non-null string field in the exact Transition input. It carries the opaque repository token captured
by acquisition; no competing entity-version field or provider-token interpretation is introduced.
Creation transitions and incompatible input fields fail binding.

The adapter checks exact receipts first, then enforces the captured token through native read options
and a returned-snapshot comparison. A provider cannot silently weaken the fence by ignoring the read
option. A stale subject returns structured `subject.changed` evidence without mutation. If the same
operation commits between receipt lookup and state read, the adapter rechecks its receipt and replays
the committed result. The existing conditional write still protects changes after the checked read.
The token participates in exact request identity through the retained input, so changing it cannot
reuse a receipt. This is a storage binding detail; domain transitions do not interpret opaque tokens.

For example, preparing approval at token `opaque-A` cannot apply after another edit creates
`opaque-B`. Retrying an already committed approval with its original exact occurrence returns its
receipt even though its own commit advanced the token. These cases are tested with in-memory
atomic receipts, including an adapter that ignores read preconditions; remote-provider qualification
remains separate.

## Retained start attribution for observations

`ProcessRelationEvaluation.StartContext` optionally exposes the existing
`ProcessControlCommandContext` retained in the native durable start receipt. The Storage runtime
projects that exact context into first-time synchronous and asynchronous host observations. It
checks the logical Process instance and authority scope, replaces any supplied attribution with
retained evidence, and does not repeat an observation whose result already has a receipt. Direct
reference interpretation has no admitted start receipt and therefore leaves this field absent.

For example, user `editor` starts a publishing Process and `physical-worker` performs acquisition.
The acquisition sees `editor` and the original issuance time from the admitted start receipt. A
replacement worker replays the recorded acquisition without changing attribution or rereading the
source. Tests cover this real service/runtime path, sync/async host projection, supplied-context
replacement, and rejection of an unrelated instance.

This is attribution evidence, not delegated identity, an ambient worker identity replacement, or
a new grant. Current authorization/revocation policy remains the host's responsibility. The
deterministic computation handler still receives only its explicit typed inputs, configuration
and cancellation; it cannot inspect the native observation context. An acquisition can explicitly
project admitted actor/time into a later computation's input.

The reusable ownership is the existing Process host and Storage replay boundary. Adding actor
fields to public Ari input or reusing the worker principal would create a competing or incorrect
authority. Reusing the existing start context also avoids another persisted attribution envelope.

## Exact snapshot tokens in entity query source views

The existing entity-source metadata projection now accepts an optional
`concurrencyTokenSemanticPath`. The in-memory and Cosmos registrations project that field from
the same acquired entity snapshot as its payload, independently of observation version and any
payload field with the same name. Conventional source identity includes the configured token
path. Empty paths, collisions with identity/version metadata, and conflicting identity selectors
are rejected. This extends the existing source registration rather than creating an Ari reader
or persisting a second domain version.

The reserved physical selector `$concurrencyToken` denotes repository metadata, not a payload
property. In memory it uses `EntitySnapshot.ConcurrencyToken`. Cosmos projects the application
`entityConcurrencyToken`, falling back to `_etag` only when the application token is undefined
or null, matching repository reads. Empty or non-string projected tokens produce failed field
evidence. Raw `entityConcurrencyToken` remains an ordinary property selector, so callers cannot
accidentally request resolved metadata by naming that property in another field mapping.

For example, a proposal at observation version 7 may carry opaque token `opaque-a`; even if its
payload contains a forged token, the query returns `opaque-a`. Preparation retains it and the
existing captured-token Transition binding rejects a subsequent commit if that snapshot changed.
Source-reader tests qualify payload shadowing, metadata identity, malformed-token admission, and
emitted Cosmos fallback SQL with a fake feed. Live Cosmos evaluation, physical read/page counts,
and the complete Ari acquisition-to-commit flow remain qualification gates.

## Present payloads in Process branches

Transition and Process pure expression profiles now admit the existing `requireValue` function.
Its semantic authority remains `ExprSemanticsCatalog`; reference execution reuses the shared
concrete-value admission rather than introducing a Process-specific unwrap operation. For example,
a Process that branches on a typed `Result` can require the success payload before invoking the
next declared step. Null or absent payloads fail evaluation; missing, unknown, and failed evidence
retain their existing infrastructure diagnostics and cannot become a successful outcome.

This is a coherent extension of the existing expression profile and evaluator. The concrete path
returns the same observation value without traversal, serialization, or new retained state.
Closure and execution tests cover a present string, absent/null members, and unavailable input
states. Process composition must still supply portable expression result types derived from its
step contracts; declaring a function does not make an opaque runtime type portable.

## Retained attribution in Durable Task observations

The Durable Task suspending host projects the authoritative start receipt into relation/query
activities, including cancellation activations. It uses `ProcessRelationEvaluation.WithRetainedStartContext`,
the same instance/scope validation now used by the storage replay host. This prevents review
acquisition from substituting worker identity for the admitted caller. It remains attribution,
not an authorization grant or permission-revocation fence.

The differential Durable Task host-operation test verifies the exact retained context, native
activity serialization, and identical replay scheduling. Existing storage replay tests qualify
mismatched instance/scope rejection and replacement of supplied context with retained attribution. The focused suite passes 87 tests
with 8 emulator cases skipped; the full core suite passes 3,972 with 33 skipped. These results do
not claim a live Scheduler deployment qualification.

## Exact-definition restrictions survive Scheduler transport

The internal JSON constructor of `ExecutionApiInvocationContext` now retains
`ExpectedProcessDefinition`. Its omission previously widened a transported control invocation
from one exact Process definition to any definition at the admitted logical address. The public
constructor and in-memory checks were correct; the Scheduler serialization boundary lost the
restriction. A direct converter regression now checks preservation and rejection of another
exact definition. The previously failing lifecycle-control emulator scenario passes after this fix.

Emulator qualification also exposed stale test setup: the cancellation fixture helper supplied
provenance from another definition, and the broad restart fixture omitted required external
request bindings/capabilities. Helpers now derive provenance from the actual compiled document;
the restart fixture uses explicitly admitted adapters, including a controlled reply for restart
and a native timer for the slow fork child. The timer makes cancellation propagation deterministic;
it does not imply preemption of an external operation. The rollover fixture waits for the timer
node rather than an intermediate checkpoint. Runtime admission is not weakened.

The final pinned local Scheduler emulator run passes all eight integration tests in 14 seconds
(`/tmp/cohesive-scheduler-native-cancellation.log`), covering restart recovery, lifecycle control,
rollover and cancellation. No Azure deployment qualification is implied. The full core run after
the serialization fix passes 3,973 tests with 33 skipped; subsequent fixture changes are qualified
by the focused interpreter suite and the emulator run.

## Exact committed response evidence

`EntityTransitionOperationReceipt.Entity` remains the authority for the snapshot and opaque token
produced by an invocation. A new repository regression commits approval, performs a later suspended
state write, then resolves the original operation receipt. It proves the original approved state and
token survive while the current repository state is suspended. All eleven non-Cosmos operation
repository tests pass (`/tmp/cohesive-exact-receipt-after-write.log`). This is an in-memory invariant
test, not HTTP integration or a new live-provider claim.

The remaining projection gap is in carrying the reference to that retained evidence across the
Process boundary. `ProcessOperationResult` contains the declared domain value/emissions/failure;
`ProcessOperationReceipt` retains occurrence and definition but not subject or input, which the
exact entity receipt lookup requires. Therefore a generic post-completion lookup cannot safely be
implemented by reconstructing input or choosing the current entity. The service projection must
retain exact commit evidence through an explicit execution contract; it must not widen a declared
boolean transition result inside a storage binding or introduce an Ari-specific receipt store.

## Native receipt reference resolution

`EntityTransitionOperationRequest.Reference` derives an input-free locator from the existing
Process occurrence, logical authority, subject and complete request fingerprint. It is neither a
second receipt nor an authorization grant. `ResolveTransitionOperation` resolves the original
`EntityTransitionOperationReceipt` through the selected authorized repository. It does not read
current entity state, reexecute a transition, or interpret missing evidence as non-commit.

This extends the native entity receipt protocol. Generic `StorageCommitReference` was evaluated
and rejected here: that executor has a different receipt-address and write-token protocol. Replacing
the native protocol would change storage authority instead of projecting existing evidence.

In-memory, SQLite and Cosmos use their existing occurrence addressing and receipt validation. Typed
repository wrappers forward the operation. Other implementations return explicit unsupported
evidence by default. Existing request replay preserves its diagnostics and fingerprint comparison.
The reference retains no business input but still contains protected tenant/entity identifiers.
Authorization and physical placement remain invocation-scoped and must precede response exposure.
No new cache, state scan or provider fan-out is introduced; Cosmos performs one receipt point read.

Full core qualification passes 3,979 tests with 33 skipped
(`/tmp/cohesive-receipt-reference-full.log`). Focused qualification passes fifteen non-Cosmos
operation-repository tests and twenty-one SQLite outbox tests, including serialized reference roundtrip, tampered identity, missing evidence, later
entity writes and reopened SQLite storage. The opt-in Cosmos test now checks reference resolution
but has not been run live. Process evidence transport and service response projection are still
required; this lookup API alone does not complete HTTP adoption.

## Receipt evidence on native operation results

`ProcessOperationResult.ReceiptReference` carries an optional concrete, self-contained portable
locator alongside the domain value. The authoritative host owns the locator contract. Attaching it
does not change the Transition outcome, emissions or graph binding. Different attached evidence
cannot replace an existing locator. Failed outcomes and unresolved locators are rejected.

The entity adapter attaches `EntityTransitionReceiptReferences.Project(receipt.Request.Reference)`
after commit or exact replay. Its portable contract derives from the native reference type, with
one lazy preparation. `Read` requires that exact contract before reconstructing the native locator.
The entity commit itself forbids a receipt locator: its receipt does not exist yet, and including
its own derived identity would create a circular fingerprint. The Process operation receipt retains
the enriched result after the entity boundary has committed.

Absent references are omitted from JSON. A canonical-byte regression compares the old result
shape with the new result and proves that existing receipt fingerprints remain unchanged. Native
Durable Task converter and retained Process-receipt tests preserve the locator through serialization
and replay without invoking the host again. This is protected execution evidence, not a telemetry
label, authorization grant or payload for automatic export.

Focused adapter tests pass 17 cases with one live Cosmos case skipped. Initial wire/replay tests
and repository tests pass 18 cases. The first broader run exposed two conformance assertions
comparing enriched adapter results directly with the deliberately un-enriched entity commit result;
they now compare exact derived evidence while preserving one-effect/one-publication invariants.
All five final transport/recovery tests pass (`/tmp/cohesive-receipt-evidence-recovery.log`).
The corrected full run passes 3,982 tests with 33 skipped in
`/tmp/cohesive-receipt-evidence-full-corrected.log`.
Terminal service projection remains outstanding.

## Canonical activation attribution and disclosure

Successful `OperationCompleted` events retain the same optional typed receipt reference as the
host result. This reuses the existing occurrence, activation, continuation and definition attribution
in canonical execution evidence, rather than adding a second operation ledger. Both durable
runtimes already retain that evidence; Scheduler resume restores the retained activation list.
Absent locators remain omitted from the canonical wire representation.

The normalized telemetry projector deliberately excludes receipt locators. Tests verify exclusion
from normalized traces and public Scheduler status while canonical evidence retains the locator.
The locator may contain protected tenant/entity identities; access to raw execution evidence and
receipt resolution must be authorized. Do not use sampled/exported telemetry as response authority.

Trace admission only accepts concrete valid locators on successful operation-completion events.
Storage checkpoint admission additionally requires exact equality with the corresponding retained
operation receipt. A mutation that attaches different receipt evidence to an operation is rejected.
The reference/runtime differential, crash recovery and integrity suite passes nine tests; full core
qualification passes 3,985 tests with 33 skipped in `/tmp/cohesive-receipt-terminal-full.log`. Full
native Scheduler result serialization is included in that run. This is not live Azure qualification or completed HTTP adoption.

## Protected terminal-value read

The existing `IProcessExecutionValueRepository` now projects exact terminal continuation identity
and retained canonical activation evidence through `ProcessExecutionValues`. These are protected
values, separate from monitoring/status. The Scheduler reader uses the same exact authority-scoped
point read and already-loaded terminal result; it does not scan history or issue another provider read.

The value artifact validates definition, instance and activation affinity. Default evidence means
that evidence is unavailable; a materialized empty array means an empty history. Older readers'
artifacts can omit terminal continuation/evidence, but a receipt response must not infer missing
attempt identity. Evidence may include earlier attempts, so result selection must match the exact
terminal continuation before choosing the declared commit node.

Twenty repository tests pass (`/tmp/cohesive-protected-terminal-values.log`), including exact protected
value projection, foreign authority rejection, in-progress versus missing-terminal-artifact states,
and malformed definition/instance affinity. Service declaration/binding of the commit result source,
resource authorization and HTTP response projection remain outstanding.

## Declared committed-entity result reads

`ServiceProcessEntityResultOperation` declares an independently authorized read of the entity
receipt from one exact Process node. Its declaration owns the Process reference, commit-node
identity, entity state shape and read requirements. The binding verifies the selected node is the
exact supplied Transition and that its observation contract matches the entity authority.
`ServiceEntityBinding` now captures the entity/repository association used by both direct Transition
bindings and Process result reads; repository resolution remains invocation-scoped.

`ReadCommittedEntityAsync` admits access before reading native Process values, selects only the
terminal attempt and declared node, and requires one receipt-bearing completion. It verifies locator
occurrence/authority, exact Transition, entity identity, full snapshot and trusted partition before
resource authorization against the retained snapshot. `IServiceInvocationAuthorization` now accepts
the common `ServiceOperation` for resource checks; policy semantics remain logical ownership plus
explicit operation grants. Host implementations of that interface must adopt the generalized signature.

A running execution returns Accepted; unavailable, ambiguous or missing evidence returns explicit
non-success without reading current entity state. Noncompleted Processes and rejected Transition
decisions do not produce a successful entity result. This read does not start, retry or wait for
execution. Medium adapters still own DTO projection and request/response conventions.

Example: a note commit writes `committed-private`, then a later write stores `later`. The declared
result returns the first snapshot and token. A grant for the result operation is required independently
of start permissions, and a receipt whose logical owner differs from the admitted tenant is denied.
Fifty-one focused service tests pass, including declaration roundtrip, wrong-node binding, exact
older snapshot, denied admission before reads, ownership, missing/ambiguous receipts and earlier
attempt exclusion. Full core qualification passed 3,994 tests with 33 skipped in
`/tmp/cohesive-service-receipt-full.log` before the HTTP projection additions.

`ProjectCommittedEntityResult<TResponse>` projects the declaration into native API metadata.
`MapServiceProcessEntityResult` attaches GET routing and a synchronous snapshot-to-response mapping;
authorization and receipt resolution remain in the shared runtime. Successful responses retain the
original opaque token in the configured header (default `X-Concurrency-Token`). Registration and
contract generation perform no storage reads. The response mapper runs only after authorization.
The host owns the public response view and its serialization conventions.

Eleven result-read tests pass, including authorized/denied HTTP invocation and OpenAPI required-path,
body-free request and outcome projection. Broader focused service coverage passed 53 tests before
the added OpenAPI case. Generated-client and Ari adoption qualification remain outstanding; this is
in-memory qualification, not a live-provider claim.

## Declared terminal business classification

A committed-entity result operation may reference an exact deterministic hosted Query through
`ResultClassifier`. The binding requires that Query's input to equal the Process result contract and
its output to equal `ServiceResultClassification`. The classifier selects standard API disposition
and application-approved diagnostics. Success permits exact receipt resolution; it never proves a
commit on its own. An explicit business rejection returns before repository resolution. Missing or
incompatible classifier values remain infrastructure failures rather than fabricated business errors.

`DeterministicHostedQueryBinding` belongs to Relations: canonical Query declarations already own
implementation affinity, portable contracts and immutable configuration. The existing Process
deterministic registration now delegates to this binding. `HostedQueryValueAdapter` shares input
admission, conversion and output validation across both invocation paths; historical diagnostic codes
remain stable. No synthetic Process node/occurrence is created for a service read, and no invocation
result is cached. The host still attests purity; native code is not sandboxed.

Example (tested): a terminal classifier rejects a note with `notes.rejected` and ValidationFailed
when no commit receipt exists. The read returns the business diagnostic and HTTP exposes its field
issue. The same absent receipt with a Success classification remains an infrastructure error. Denied
callers trigger neither classification nor storage reads. Ari policy classification adoption and
bounded HTTP completion waiting remain separate integration gates.

## Bounded completion waiting (qualification in progress)

`IProcessExecutionCompletionWaiter` is an optional capability of the existing Process value provider.
The Durable Task repository maps the authorized logical identity through its existing physical identity
function and uses the native completion wait without requesting payloads. Linked cancellation bounds
the wait; cancellation never sends a terminate/cancel command to the execution. Timeout returns false,
caller cancellation propagates, and provider exceptions remain observable.

`ReadCommittedEntityAsync` accepts an optional wait bound. It admits access and reads exact Process
affinity before waiting, rechecks admission afterward, and rereads canonical values only if completion
was observed. It waits at most once; expiry returns Accepted. Final receipt and classifier checks remain
unchanged. Nineteen result-read tests pass, including denied/wrong-instance exclusion, completion versus
expiry, and read/repository-resolution counts. Native adapter timeout/caller-cancellation and service authorization-revocation tests now pass: 42
focused result/repository tests in `/tmp/cohesive-wait-boundary-tests.log`. These use a native-client
fake; remote Scheduler behavior and Ari HTTP adoption remain unqualified.

## Local durable result values

`ProcessDurableExecutionValueRepository` projects protected values from `IProcessDurableStore`
checkpoints. It checks retained admission authority before exact-plan resolution, validates checkpoint
compatibility, and returns native in-progress or terminal values with activation evidence. Store
resolution is invocation-scoped and prepared plans remain host-owned. There is no second result
store, entity reread, background polling or implicit completion driver.

Five composition tests pass, including a real multi-entity Process progressing from admitted to
completed, foreign-scope exclusion and unavailable exact-plan failure. This closes the local result
reader gap only; local execution driving and Ari host/HTTP integration remain outstanding.

## Bounded local advancement

`ProcessDurableRuntime.AdvanceAsync` drives an already admitted local Process through at most a
configured number of immediately runnable durable cuts. It returns the native result at completion,
quiescence, lifecycle/storage conflict or the activation limit. A final DurableCut still needs another
call. It does not promise background scheduling, inject external input or implement a second workflow.

Activation identity uses the retained activation count; logical time comes from the preceding
checkpoint. The trusted activation context must remain stable across recovery. The existing activation
method continues to own leases, compatibility admission, operation replay and atomic checkpoint commit.
The convenience driver loads the checkpoint to choose its next identity; `ActivateAsync` performs its
own guarded load and validation. This is a bounded extra read per attempted activation, not a new cache.

Seven composition tests pass. The local driver stops after one cut, reconstructs after a post-commit
crash, preserves one acquisition/computation and one version increment per entity, and adds no
activation after terminal completion. Ari local hosting/HTTP integration remains outstanding.

## Local start admission binding

`InMemoryExecutionControlApiAdapter.CreateLocalProcessStartDispatcher` connects the existing
command/idempotency/instance registry to exact prepared plans and native local runtime bindings.
Admission uses the same evaluator and retained trusted start receipt. Interrupted execution can be
retried through that receipt; successful replay does not repeat entity effects. Unknown plans fail
before admission. The adapter's independent lifecycle endpoints must not be exposed as controls over
the supplied durable runtime.

The registry remains in memory, and the runtime's store determines checkpoint durability. There is no
background scheduler: a quiescent or bounded-cut Process needs later driving. This local profile is
not a claim of remote durability across host loss. Nine composition tests pass, including local
admission, changed-content conflicts, completed replay and post-commit interruption/recovery.

### Start intent at application boundaries

`ServiceRuntime.StartAsync` accepts native command/idempotency identities, the initial continuation,
and a materialized `ObservationValue`. The declared Process supplies its exact definition and input
contract; admission supplies authority, issuance time and provenance. An HTTP application therefore
need not construct a nominally trusted `ProcessStartRequest` with placeholder authority just to have
it replaced. The canonical-request overload remains available for already materialized requests,
including its exact-definition check. Both overloads share authorization, input validation, evidence
and dispatch. Tests cover cross-overload replay, authorization rejection, invalid input and recovery
following an interrupted local commit.

### Successful HTTP response alternatives

TypeScript client return types include every distinct body type whose resolved HTTP status is 2xx.
The native API HTTP projection supplies default statuses; explicit status bindings retain authority.
For example, review may return a committed resource with 200 or a Process admission with 202. A
primary-only return type incorrectly allowed callers to treat an admission as a committed resource.
The generator now emits both alternatives in its signature/cast and imports their types. Error
responses remain the HTTP client's rejection responsibility. This is a body union, not a substitute
for a status-discriminated transport response where callers need headers or status distinctions.

### Medium-owned standard problem projection

`ServiceEndpointRouteBuilderExtensions.ProjectServiceProblem` resolves the HTTP status and standard
problem body from the declared endpoint alternative. It preserves validation issues and rejects
undeclared/non-problem result contracts. The committed-entity result endpoint uses the same projector;
application HTTP seams can reuse it with lazy runtime resolution instead of maintaining a second
result-kind/status switch. Execution, admission and classification remain service/runtime concerns.

### Declaration-only result endpoint projection

`ServiceApiProjection.ProjectCommittedEntityResult` projects result identity, authorization requirements
and response alternatives from a validated service document without constructing runtime bindings.
The runtime's existing projection shares that implementation and adds its known entity/Transition
metadata. Declaration-only projection intentionally does not invent those physical binding details.
Runtime construction still checks the exact Process, commit node, Transition and entity association;
HTTP invocation must resolve and use the qualified runtime. This separates metadata preparation from
repository/dispatcher resolution without another operation catalog. Invalid documents, unsupported
extensions, wrong operation families and request bodies on result reads are rejected.

ASP.NET result mapping also accepts the canonical service document plus a runtime resolver. Endpoint
registration validates/projects metadata without invoking that resolver. At request time the resolved
runtime must match the registered service identity, revision and fingerprint before any protected
result read. Both eager and lazy overloads share request parsing, result authorization, retained token
projection and problem formatting. A mismatch is a host binding error, not a fallback to another
service. Hosts still own resolver lifetime and must avoid rebuilding runtimes on every request.

Declaration-only committed-result projection and its lazy ASP.NET mapper accept existing
`ApiScopePolicy` values at the medium boundary. This preserves host scope selection metadata
(e.g. Ari's tenant header) without inventing a service-local tenant model or weakening the
service's declared authorization requirements. Tests assert policy identity in the portable
endpoint and native endpoint metadata; all 26 committed-result tests pass. Host scope enforcement
still belongs to the existing scope adapter; metadata preservation alone is not an end-to-end
multi-tenant authorization qualification.

The Services.Infra adapter adds exact service-to-workload association over existing consumer binding
identities. Ownership stays separate: it depends on API declarations and Infra, not execution runtime;
Infra core gains no API dependency. Four focused tests cover exact references, immutable selections,
coverage and consumer ownership. Capability closure, requirement completeness, persisted association
admission and Ari adoption remain unfinished; this initial association is not a readiness proof.


### Canonical terminal results for non-entity workflows

Ari protocol compilation exposed a semantic gap in committed-entity result reads: its canonical output
contains compilation status, diagnostics and provenance, and graph creation/revision are alternative
commit nodes. Selecting an arbitrary entity receipt would lose the workflow result or require
application-owned branching. Extend the existing service runtime with `ServiceProcessResultOperation`
and `ServiceProcessResultBinding`; do not reconstruct compilation results from current entity state.
The exact Process remains output-contract and terminal-value authority. The operation declares only
its exact Process reference and independent read requirements, and round-trips in the service document.

`ReadProcessResultAsync` returns the native `PortableValue` after checking the exact Process result
contract against its compiled validation context. `Success` means the Process completed and its
canonical value is available, not that a domain-specific result inside it reports success. Pending
execution is `Accepted`; non-completed terminal outcomes are `DomainError`; missing or incompatible
terminal evidence is an infrastructure error. No current entity read, workflow restart, or fabricated
result is a fallback. The service does not introduce another result envelope or output schema.

Terminal and committed-entity readers share a single admission/read/wait lifecycle: capability and
logical-scope admission precede provider access; exact definition/instance matching precedes result
use; bounded provider-native waits are followed by reauthorization; payload-free events describe
progress and failure. Providers retain responsibility for scoped storage access and exact execution
evidence. Terminal reads authorize the whole Process output within the admitted scope. Applications
must declare sufficiently restricted read capabilities for sensitive output; this operation does not
pretend that an entity resource-authorization callback applies to arbitrary workflow values.
Committed-entity reads additionally retain their receipt validation and resource authorization.

For example, a compiler returns `{ status: InvalidSpec, diagnostics: [...] }` without creating a graph.
The terminal-result operation can preserve that typed output without finding a graph receipt. A
successful compiler may instead return graph identity and compiler provenance. The API projection
`ProjectProcessResult<TResponse>` derives identity, requirements and standard alternatives from the
same declaration, attaching medium-owned response and scope-selection metadata without resolving a
runtime. ASP.NET terminal mapping and Ari's public compilation-service adoption remain separate work.

Regression coverage includes portable declaration round-trip, canonical value identity, no entity
repository resolution, denial before protected reads, revocation during waiting, pending results,
missing or wrong output contracts, wrong instance and declaration-only projection. The existing
committed-entity tests protect the shared lifecycle during extraction. No provider persistence,
retention, polling, or local-runtime lifetime guarantee changes in this extension.

Qualification: all 91 service tests pass, followed by the complete core suite at this change:
4,031 passed and 33 integration skips (`/tmp/cohesive-terminal-result-full.log`). This is local runtime
qualification; it does not establish deployed exporter, Scheduler or Cosmos behavior.


### Reusable local execution assembly

`InMemoryExecutionControlApiAdapter.CreateInMemoryProcessBindings` composes its existing admission
registry, exact prepared plans, native operation host and per-authority in-memory checkpoint runtimes
with the native protected value reader. It returns native start/value bindings rather than a second
service runtime or workflow coordinator. This closes repeated assembly observed when Ari adopted
review and compilation. Plans are indexed once; successful scope initialization is shared across
concurrent invocations. Unknown-scope result reads use one empty read-only store and do not create
scope entries. Product policy, operation handlers and repository associations remain host-owned.

Retain this pair for the host lifetime. The profile has no restart durability, background scheduling,
or automatic eviction; retention limits remain an explicit operational follow-up. Do not expose the
registry's unrelated lifecycle endpoints as controls over these checkpoint runtimes. The existing
advanced factory accepting an external runtime resolver remains available for qualified durable stores.
The composition regression executes a multi-entity Process, reads its canonical terminal value, and
replays admission without repeating acquisition, computation or writes. All 92 service tests pass.

Full core validation after local assembly extraction passes 4,032 tests with 33 integration skips
(`/tmp/cohesive-local-bindings-full.log`). Local packages for dependency-ordered Ari adoption are
`0.1.0-service-review.local-bindings`; this is not a published release.


### Checkpoint authoring exposed by compilation adoption

Ari's compilation workflow reads a source token then updates that source after writing a graph. A
crash after the source update but before terminal checkpoint persistence must replay retained
preparation, not re-read the now-changed source token. The native DurableCut construct already models
this boundary, but async Process source authoring lacked its syntax projection. Extend ProcessContext
and its generator with DurableCut; the generated definition/fingerprint matches the native builder.
This is an authoring extension of existing semantics, not a service-owned journal or recovery loop.
All 98 Process authoring/generator tests pass; Ari's compilation root uses the cut before any mutation.

### Declaration-only native Process HTTP mapping

Start/control projection now consumes the service document directly and resolves its runtime only
when invoked. Native command and outcome contracts remain owned by the execution API catalog;
service operation identity and grants remain owned by the service declaration. Existing runtime-based
projection delegates to this same path. HTTP start/control and result routes share exact runtime
identity/revision/fingerprint validation. Lifecycle action eligibility is also shared with binding
validation, so a declaration cannot project inspection or Signal as a lifecycle mutation.

Example: registering a Process endpoint can attach a tenant header policy while its dispatcher is
unavailable or expensive to construct. Registration validates its semantic contract without constructing
the dispatcher. A later request resolves the exact runtime, enforces native input and authorization
admission, and preserves the normal start/control replay decisions. This native-envelope profile does
not yet remove Ari's domain-specific start request shaping or imply a typed domain-input start mapper.

### Declared query HTTP boundary

The service query HTTP adapter reuses ServiceRuntime.EvaluateAsync and the native relation-query
HTTP evaluation-identity convention. It does not construct a second evaluator or copy parameter/scope
admission. Host-owned typed request/response projections are synchronous and contain no reads or
writes. The service document remains operation/grant authority; the referenced query remains parameter,
output-demand and execution authority. A caller-supplied tenant parameter is rejected before any read.

Admission failures and native failed evaluations have separate declared response alternatives even
when both use HTTP 400: the former returns a standard problem, while the latter reaches the response
projection with the full native phase outcome and diagnostics. This avoids either silently discarding
query evidence or inventing a parallel diagnostic model. Response redaction remains a medium policy.

### Process command response composition

Bounded HTTP responses compose an existing Process start and independently authorized entity-result
read. This is transport response policy, not another canonical workflow: the Process remains the
sequencing and recovery authority. The declaration-derived command projection validates exact Process
association and derives terminal alternatives from the result operation, with a medium-owned pending
response. A start capability does not grant result disclosure. The shared ASP.NET mapper now executes this composition through native runtime calls; Ari adoption
remains required before retiring the current review HTTP coordinator. Projection tests cover exact revision
mismatch, invalid operation family/body, response identity and no infrastructure resolution.

The HTTP mapper is qualified with native start admission/replay and a retained receipt fixture. Tests
cover completed output and opaque token, bounded pending response and escaped Location under PathBase,
start denial without dispatch, independent read denial after dispatch, and classified terminal rejection.
These are adapter tests, not a new end-to-end worker durability claim.

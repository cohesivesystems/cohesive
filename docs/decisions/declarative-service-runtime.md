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

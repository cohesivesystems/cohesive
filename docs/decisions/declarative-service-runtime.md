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
references one exact Transition and its qualified entity state identity; input, observation and outcome
contracts stay with that Transition. No service-specific serializer, fingerprint algorithm or metadata
envelope is introduced.

`Cohesive.Api.Execution.Services.ServiceTransitionRuntime` validates the document and complete binding
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

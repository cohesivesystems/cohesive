# Cohesive.Api.Execution

Transport-neutral execution-control API composition for canonical Cohesive execution semantics.

## Install

```bash
dotnet add package Cohesive.Api.Execution
```

## Use When

- You need the canonical Process start, inspection, explanation, retained-trace, lifecycle-control, or limit-update
  API catalog.
- You want an in-memory reference binding for the same declared operation and result contracts.
- You are building an HTTP, CLI, generated-client, test, or presentation interpretation of execution control.

Applications that only declare general semantic APIs should reference `Cohesive.Api` instead.

## Ownership Boundary

`Cohesive.Api.Execution` is a composition package, not another execution semantic authority:

- `Cohesive.Api` owns generic operation, endpoint, authorization, result, and semantic-reference declarations.
- `Cohesive`, `Cohesive.Processes`, and `Cohesive.Storage` own execution identity, commands, status, trace,
  explanation, Control, and Process runtime semantics.
- This package binds those authorities into one stable execution-control catalog and safe reference result
  projections.
- Concrete transports such as ASP.NET remain in `Cohesive.Adapters.*`.

The dependency direction is acyclic: `Cohesive.Api.Execution` depends on `Cohesive.Api`, `Cohesive.Processes`,
`Cohesive.Storage`, and the `Cohesive` foundation; the complete generic `Cohesive.Api` project-reference closure
does not acquire `Cohesive.Processes`.

## Entry Points

`ExecutionControlApiCatalog.Create()` returns the complete immutable route-neutral operation inventory in stable
order. `InMemoryExecutionControlApiAdapter` is a linearizable reference integration for tests and local composition;
canonical reducers remain responsible for lifecycle, replay, fencing, and admission semantics.

```csharp
using Cohesive.Api.Execution;

var catalog = ExecutionControlApiCatalog.Create();
var traces = catalog.Definition.GetOperation(catalog.Traces);
```

Production runtimes can supply authoritative asynchronous dispatchers for Process start, the five lifecycle
mutations, and Control-limit updates. When one is supplied, the adapter authorizes against the canonical endpoint,
does not consult its local registry for that operation, and projects only the authoritative result. A lifecycle
dispatcher receives the untrusted portable command separately from `ExecutionApiInvocationContext`; it resolves the
Process using the trusted authority scope, rebinds first-occurrence evidence, restores retained occurrence evidence
for exact replay, and returns only after the safe canonical result is recoverable. Provider message admission alone
is not lifecycle success. `ExecutionProcessControlCommandAdmission` provides the shared trusted rebinding rule.

The `explain` and `traces` queries return `ExecutionExplainArtifact` and `ProcessExecutionTraceArtifact` directly.
Adapters must not translate them into parallel response models. Opaque `ExecutionApiProblem` values intentionally
exclude physical identities, authorization evidence, payloads, and provider history.

## Invariants and Failure Boundaries

- Endpoint handles, operation order, authorization requirements, result variants, and semantic references come only
  from `ExecutionControlApiCatalog`.
- Trusted authorization, issuance, provenance, and tenant evidence is supplied by an adapter, never deserialized
  from an API caller.
- Authoritative lifecycle lookup uses trusted authority scope plus canonical logical Process identity. Missing and
  unauthorized targets remain opaque, and exact command or idempotency replay restores the retained occurrence.
- Repository availability and lifecycle dispositions map to declared result variants; unspecified or incoherent
  states fail closed.
- The in-memory adapter accepts only handles owned by its exact catalog and checks returned evidence affinity before
  projection.
- The complete `Cohesive.Api` project-reference closure must remain free of `Cohesive.Processes`.

## Transport Interpretation

`Cohesive.Adapters.AspNet` maps the catalog's Process observation handles through
`MapProcessExecutionInspectApi`, `MapProcessExecutionExplainApi`, and `MapProcessExecutionTracesApi`. OpenAPI,
GraphQL, TypeScript, CLI, and future hosts consume the same generic `ApiDefinition` without acquiring transport
behavior from this package.

## Migration from Cohesive.Api

Execution-control types moved from the `Cohesive.Api` assembly into this package. Add a package or project reference
to `Cohesive.Api.Execution` and import `Cohesive.Api.Execution`. Wire authorities, schema version v4, operation order,
endpoint identities, contracts, and canonical JSON are unchanged.

## Testing

Catalog, result, in-memory integration, generated-client, and ASP.NET conformance tests live under
`src/Cohesive.Tests/Api`. `ExecutionApiPackageBoundaryTests` guards the assembly dependency direction and ownership
of the execution-specific public surface.

## Related Packages

- `Cohesive.Api` for generic semantic API declarations.
- `Cohesive.Processes` for canonical Process semantics and execution evidence.
- `Cohesive.Storage` for canonical Control contracts used by the execution-control surface.
- `Cohesive.Adapters.AspNet` for ASP.NET endpoint projection.
- `Cohesive.Adapters.OpenApi`, `Cohesive.Adapters.GraphQL`, and `Cohesive.Adapters.TypeScript` for derived API artifacts.

## Declared service invocation

`Services.ServiceRuntime` binds a validated `Cohesive.Api.Services.ServiceDefinition` document
to exact Transition/entity/repository, canonical query/evaluator and Process/start-or-control-dispatcher associations. Direct calls and
`Cohesive.Adapters.AspNet.Services.MapServiceTransition` share authoritative loading, mandatory authority,
decision and conditional commit. API CLR types are checked projections of the Transition contracts.

`IdentityServiceInvocationAuthorization` interprets declared requirements as explicit capabilities in
normalized identity grants and checks logical entity ownership independently of physical placement.
It adds the existing `Cohesive.Identity` dependency to this optional composition package. Generic API
consumers still do not acquire storage, identity or Process runtime dependencies through `Cohesive.Api`.

Transition invocation supports existing subjects without emissions; unsupported creation, emission and
extension requirements fail binding. `EvaluateAsync` binds the declared query scope parameter from
trusted identity, and `StartAsync` delegates admission/replay to the existing Process dispatcher.
`ControlAsync` derives its request type and result semantics from the native control API catalog; it binds
trusted authority and an exact Process-definition restriction before native admission or receipt replay.
The lifecycle profile supports Pause, Continue, RestartAttempt, Cancel and Terminate; inspection and Signal
ingress require distinct qualified bindings. The
service itself adds no durable receipt store or stronger permission-revocation guarantee. Returned service and Transition traces are payload-free inspection evidence; internal snapshots and
decision inputs/observations are not invocation results. See [the design and qualification boundary](../../docs/decisions/declarative-service-runtime.md)
and `ServiceRuntimeTests`, `ServiceQueryRuntimeTests` and `ServiceProcessRuntimeTests` for executable
examples. `ServiceCompositionTests` qualifies acquisition/computation followed by durable multi-entity
recovery. Query source qualification currently covers native in-memory acquisition; remote-provider
filtering and query HTTP projection remain in progress. `ProjectProcess<TRequest>` derives Process entry/control
API contracts from the native catalog. ASP.NET `MapServiceProcessStart` and `MapServiceProcessControl<TCommand>`
use those projections and the shared runtime, retaining native results and existing admission problems.


`ServiceProcessResultOperation` exposes the exact Process terminal value when the result is not an
entity receipt (for example compilation diagnostics and provenance). Bind it with
`ServiceProcessResultBinding` and use `ReadProcessResultAsync`; the existing native `PortableValue`
retains the Process-owned contract and value states. Result requirements authorize the complete output
within the admitted logical scope. Terminal and entity result readers share admission, bounded waiting,
reauthorization and payload-free evidence; entity reads additionally check their retained receipt and
resource. `ServiceApiProjection.ProjectProcessResult<TResponse>` derives medium metadata without
resolving the provider. See the design note for failure behavior and qualification boundaries.

`ServiceApiProjection.ProjectProcessEntityCommand<TRequest, TResponse, TPending>` projects a medium
command combining a declared Process start with a committed-entity result operation. Both operations
must reference the same exact Process definition, revision and fingerprint. For example, `approve`
may return a committed proposal or a pending execution response; it cannot accidentally read a result
from a different approval revision. Endpoint admission uses the start requirements; result disclosure
still requires the independently declared read authorization. Projection resolves no runtime and does
not itself execute the start, bounded wait or result read. `MapServiceProcessEntityCommand` executes this composition using native admission and result reading.
Its pure medium delegates bind retry identities/input and project committed or pending responses.
The optional positive bounded wait belongs to the runtime result reader. A pending response includes
a path-base-aware Location with the escaped instance identity and Retry-After: 1. The result route
must be local with exactly one `{instanceId}` parameter. Admission denial performs no start; result
denial after admission discloses no receipt or repository snapshot. Cancellation and unexpected failures
propagate without adapter retries. Ari adoption remains follow-up work in the same change.


### Binding preparation lifetime

The ordinary `ServiceRuntime` constructor validates all supplied bindings immediately.
`ServiceRuntime.CreateDeferred` is an explicit alternative for services combining independently used
capabilities. It validates the declaration and complete operation/factory coverage immediately, then
prepares and validates each exact binding once, thread-safely, on first use. Factory and validation
failures are retained for that runtime's lifetime. Factories perform immutable preparation only; they
must not capture invocation scope or perform backend I/O. Repository/evaluator resolution, authorization
and results retain their invocation lifetime. Binding preparation can precede caller authorization.

For example, reading a compilation source can prepare only its query binding while leaving an unused
write Process uncompiled. This does not establish readiness for the write operation. A host that needs
all bindings admitted before readiness calls `ValidateBindings`; this reuses preparation and reports
retained failures without dispatching operations or proving backend availability. Declaration-only API
projection stays independent of runtime preparation; typed runtime projections may resolve their binding.
`ServiceQueryBinding.GetReference` projects the exact canonical query identity/revision/fingerprint for
service declarations without constructing a dummy evaluator or copying fingerprint conversion logic.

Tests in `ServiceQueryRuntimeTests` cover concurrent one-time preparation, retained factory/admission
failures, immediate coverage checks, tenant isolation, and physical reads. These are deterministic
work-count guarantees, not startup or end-to-end latency measurements.

### Fluent Process-backed operations (implementation in progress)

Service authoring now lowers an existing typed or compiled Process to the same canonical service IR:

```csharp
var service = Service.Define(new("notes"), new("1"), provenance)
    .Require(new("notes.read"))
    .Operation("echo")
        .Run(echoProcess)
        .ExecuteEphemerally(TimeSpan.FromSeconds(2))
    .Build();
```

`Run` references the exact Process revision/fingerprint; its input and output contracts remain authoritative.
`ServiceEphemeralProcessBinding` associates a prepared Process and an invocation-scoped host factory. The
factory runs after service authorization and exact input validation; its Transition/Query bindings remain
responsible for resource authorization and persistence. Execution uses the canonical interpreter and existing
service trace/metrics. The current budget covers execution after admission and host construction, not total
HTTP request latency. Cancellation may follow a committed write and preserves canonical host outcomes for
reconciliation; it does not imply rollback or retry safety.

For a durable Process start, choose `ReturnAfterDurableAdmission()` instead. Lifetime and completion are
separate canonical policy fields; neither confers ACID. Existing omitted policies retain native durable-start
behavior. Bindings reject unsupported combinations. Native start HTTP projection rejects ephemeral policies
rather than exposing an admission contract for an operation promising completion.

This authoring slice does not yet provide durable terminal waiting or Ari migration. Those remain in the declarative service refinement.
Do not present the existing start adapter as a terminal-completion adapter.


A single-entity mutation can now be authored as a typed composition and supplied to `Run`:

```csharp
var approval = ServiceMutation.HydrateWith(ApprovalFacts)
    .Apply(Proposal.Approve, facts => facts.Id)
    .EnrichWith(ReviewDetails)
    .Build(new(new("proposal/approval"), new("1"),
        ProcessRecoveryPolicy.ContinueAttempt, provenance));
```

The subject selector becomes a portable field path. The Transition's exact output becomes the enrichment
input; the last step supplies the public Process output. Entry, edges and bindings are derived deterministically.
`ServiceMutation.Apply(...)` omits hydration when caller input already contains the required facts. No runtime
callback survives lowering. Reference hosts still own resource authorization, fact freshness and actual commits.
A second mutation is rejected by this shorthand; use an explicitly authored Process for multi-entity work.
There is no automatic compensation or interpretation of a domain outcome as rejection: domain branching belongs
in an explicit Process when an outcome requires it. Interpreter/host failure stops subsequent steps.

The executable synthetic example in `ServiceMutationTests` proves exact canonical-byte equivalence for a direct
mutation, inferred contracts, ordered hydration/mutation/enrichment, pre-write failure, post-write enrichment
failure and rejection of a second mutation. Its host records writes in memory; it is not deployed storage or
cross-entity transaction qualification. Exact definition links and shape evidence remain required at compilation.


`MapServiceEphemeralProcess` exposes a typed Process-backed operation without resolving the runtime during
route registration. It derives request/result types from the exact typed Process and returns its public
terminal value on success. Domain failure and execution interruption expose a standard problem explaining
possible prior effects, not private receipt locators, host values or backend exception messages. Request abort
propagates; a cooperative execution deadline returns an infrastructure problem after the host stops. It never
returns an asynchronous admission receipt or schedules background work. Internal callers retain the full
`EphemeralProcessResult` evidence for authorized reconciliation. The HTTP tests cover typed success, deferred
resolution, one execution and redaction; deployed qualification still needs the refinement's remaining work.

Explicit `ReturnAfterDurableAdmission()` operations now return `ApiResultKind.Accepted`, projected as HTTP
202 with the native `ProcessStartResult` receipt. Replaying the same admission returns 202 with its retained
receipt as well; this is not a terminal-success claim. Native lifecycle commands retain their own outcomes.
A legacy omitted execution policy preserves the earlier start result contract until consumers adopt explicit
policies. An explicit admission-only operation cannot be attached to the legacy bounded entity-completion
adapter; that would contradict its declared response promise. Tests exercise direct admission and actual
HTTP replay against the in-memory native admission adapter. This is not real-provider durability evidence.

Ephemeral query calls receive the same native start-attribution contract used by durable handlers, populated
only after service admission from the normalized actor, scope, time and provenance. Invocation-scoped command
and idempotency identities identify this call; they do not create a durable receipt or deduplication guarantee.
`WithInvocationStartContext` checks instance/authority affinity before attaching that attribution. The retained
start helper remains for durable replay. This lets existing query handlers enforce scope and attribute generated
artifacts without accepting caller-supplied actor/time fields or introducing an Ari-specific identity model.

### Classifying public Process results

`ReadResultOf(process, classifier)` declares an optional exact deterministic hosted query from the
Process output to `ServiceResultClassification`. The runtime validates the reference and both contracts
when binding, then evaluates the classifier only after independent result-read authorization and
terminal-value validation. A rejection exposes its selected diagnostics without the raw output; success
returns the unchanged canonical value. No entity repository or internal commit-node selector is required.
The existing committed-entity result path shares the same classification implementation.

For example, a review Process can return `Result<CommittedProposal, ReviewRejection>`. Its classifier
selects success or conflict from that value; HTTP then projects the successful proposal into its resource
view. This preserves a Process-owned output while keeping transport status and domain rejection distinct.
A classifier grants no authorization and provides no execution, persistence or commit guarantee.

Data-authored workflows can use `Run(document)` and `ReadResultOf(document, classifier)` directly.
These overloads validate the original canonical Process document without inventing CLR input records
or compiling its dependency closure. The typed classifier must exactly match the public output.
Document validation is local to declaration construction; callers should retain the resulting service
declaration at host lifetime, as with other immutable authoring results.

The ASP.NET domain-input projection parses and binds the medium request before resolving the service
runtime. Missing retry headers or other binder-rejected fields therefore cannot initialize a Process
host or dispatch work. Authorized semantic input validation remains owned by the runtime.

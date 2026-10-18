# Connected application composition

## Intent and authority

Compose an inspectable graph from API contracts, queries, transitions, processes, domain entities,
physical storage mappings and native infrastructure associations. Authoring objects are producers and
references, not a new portable semantic IR. Existing DomainModelDefinition, query documents, API definitions,
Process documents and infrastructure realization remain their respective authorities.

Plain C# types optionally organize declarations. Inline builders remain equally supported. Stable semantic
IDs and revisions are explicit; a class or property rename must not silently retarget a durable contract.
Partial contributions must ultimately resolve into one validated model rather than independently maintained catalogs.

## Implemented foundation

- `DomainModel` wraps DomainModelBuilder and supplies an instance-owned, once-built Definition. Entity handles
  are the exact objects passed to mappings and queries. Relationship handles still use the existing Relations
  documents imported by queries; DomainModelDefinition does not newly claim to serialize those relationships.
- `QueryModel` collects the actual compilation requests while returning the original typed handles. Definition
  is an immutable request collection, not a second query IR or a provider execution plan.
- `ApiModel` supplies a frozen Definition from ApiBuilder. Existing ApiDefinition.Combine composes separately
  declared contracts and rejects duplicate endpoint IDs. Native route collisions and partial implementation registration belong to the separate
  ServiceApiBindingsBuilder/MapServices boundary.
- PostgreSQL's existing offline bindings gain inline mapping callbacks and a frozen Define recipe. Native table,
  column, key and partition choices stay with the adapter. Bind retains native data-source ownership at the host.
- `[Process]` on `ProcessModel<TInput, TResult>` generates metadata, provenance and cached definition lowering
  from Execute. The same generator still supports the standalone GenerateProcessDefinition factory. Model
  factories are private so callers cannot override the type's declared ID/revision through a second entry point.
- The running Aspire-first example creates one FulfillmentApplication per program. AppHost associations and
  worker runtime bindings consume the same construction recipe. Independent instances have equal portable
  query/process identities without sharing mutable catalogs or credentials.

`ModelActivator.Create<T>()` discovers the concrete constructor dependency graph and uses an isolated
singleton DI provider to construct it. Each dependency completes `IAuthoringModel` before its consumer's
constructor runs. Constructors assign get-only handles through existing builders; no `partial`, annotations,
virtual constructor callbacks, property injection or `null!` are needed. Interface dependencies and custom
factories use ordinary `IServiceCollection` registration in the activator callback. Models require one public
constructor unless explicitly supplied by a factory. Cycles, missing interfaces, duplicate registrations,
non-singleton registrations and disposable runtime resources fail during activation.

`ApplicationModelBuilder` remains the explicit, builder-only entry point. Its `Build()` returns
`PreparedApplicationModel`. The optional `ApplicationModel` base exposes that validated composition only
after activation returns. Canonical execution documents pass through `ExecutionDefinitionDocumentCatalog`,
which rejects duplicate identity/revision pairs, including different fingerprints. DI handles shared instances;
the small lifecycle layer completes declarations and validates their semantic evidence. Factories must not
retain the temporary provider; activation owns no runtime connection, worker or scope.

`ApiBindingModel` wraps `ServiceApiBindingsBuilder` and validates/finalizes offline endpoint recipes before
consumers inspect query selections. `Build(services, persistence)` still explicitly prepares the live host.
`StorageModel` groups typed native binding contributions through `StorageModelBuilder`. Each recipe runs
once during constructor authoring; activation freezes collection membership. One model can own several
databases or providers without introducing a provider-neutral mapping DSL. `InfrastructureModel` records one `Infrastructure.Define` recipe
and caches its canonical result. Native Aspire configuration remains in `OrdersApp.Configure(builder)`.
The AppHost activates `OrdersApp`, whose constructor consumes the same `FulfillmentApplication` graph used
by the worker. The worker never references AppHost or Aspire hosting packages.

There is no global semantic identity registry. Independent compositions still govern their own revision changes.

`AuthoringState<T>` owns the shared freeze-once protocol: edits serialize with preparation, freezing starts before
validation, and both successful results and failures are cached. Query order uses an explicit insertion list with a
separate duplicate-ID set. PostgreSQL's constructor is private; Define is the sole contribution boundary, including
failure cleanup of captured builders.

Construction happens before publication to other threads. Definition reads finalize catalogs. Process definition
creation uses Lazy execution-and-publication semantics and caches failures; no virtual authoring runs in its base
constructor. Query/result execution and request context are not cached by these classes. No connection is opened
by offline construction. PostgreSQL validation and preparation retain the existing compiler paths.

## Remaining dependency-ordered composition work

1. Implemented: the ASP.NET ServiceApiBindingsBuilder composes typed API selectors and optional modules,
   preserves separately declared APIs/services, and resolves native storage through IServicePersistence.
   Public query roots belong to API bindings and project into infrastructure selections; they remain outside
   the process dependency closure. Coverage is checked after contributions, before route publication.
2. Implemented: MapServices stages existing entity/query/process HTTP bindings, validates complete mapping
   and exact method/template collisions, then publishes routes. Build integrates opt-in protected failure logging
   using the existing host-owned subscription mechanism. Runtime completion still comes from Service IR.
3. Implemented: [accepted execution and ResultOf-linked polling](accepted-process-api.md) compose the native
   durable admission and protected result-read paths. Production worker selection/qualification remains explicit.
   Observation streams and callbacks remain separate; SignalR/SSE imply no durable recovery guarantee.
4. Attach registered storage/execution bindings to native Aspire resources and derive required infrastructure
   capabilities from prepared definitions. Preserve native-only resources and target configuration. Keep declared,
   validated and observed evidence distinct. Do not introduce a second infrastructure inventory.

The example remains ephemeral, with atomic entity commits and receipts, explicit cross-entity compensation,
and native/composed queries. This is not an event-authoritative state model or proof of durable recovery.

## Decisions

Use/extend existing builders and generator rather than introduce a universal semantic registry, reflection scanning
of static properties, provider-option wrappers, or a competing graph format. Constructor arguments make ownership
visible; optional base types supply actual freeze/cache contracts rather than empty marker types. Storage configuration
uses an ordinary method on a C# class; a new configuration interface is unnecessary until a concrete resolver consumes it.

Validation covers late additions, duplicate semantic identities, mapping isolation, independent equivalent program
models, generated instance dependencies and compile-time errors. Existing PostgreSQL/HTTP/process tests exercise
native behavior; deterministic warm-access allocation checks protect the once-prepared process definition. No
startup-speed or end-to-end performance improvement is claimed.

The optional host composition resolves only explicitly registered types via existing .NET constructor activation.
It neither scans members nor guesses semantic dependencies. It removes hand-ordered construction and forced
Definition reads. Generated process attachments now remove the repeated transition/lookup registration list;
the HTTP implementation registry and unified endpoint mapping now consume those generated attachments.

## Generated process attachments

The existing generator records typed transition and EntityPointRead handles that Execute actually invokes.
ProcessModel.Dependencies prepares those attachments once. ServiceDependencies.Process(model) checks each
attachment against the exact canonical graph reference before using the existing transition and lookup binding
paths. Repeated aliases to one declaration are idempotent; competing recipes for the same exact reference fail
with structured diagnostics. Raw references and arbitrary hosted queries still need explicit registrations;
registration order is independent. Final host binding reports all missing exact dependencies before repository IO. Separately declared services remain supported.

EntityPointRead retains an existing observational HostedQuery, the exact entity, a key selector and a result
projection. The query document remains portable authority; these delegates are explicit native host attachments,
not serialized semantics or a proof of portable keyed-read lowering. They must be thread-safe and their implementation
contract must be revised when their behavior changes. The repository supplies the trusted partition. No read runs
while composing or generating dependencies. Typed Process transitions accept POCOs, matching Transition itself.

Independent public SQL query selections remain explicit, with revision/bounds/consistency, and are not inferred
as dependencies of Fulfill. Generated attachment inference does not complete API-to-service composition, durable
execution, or capability evidence for arbitrary native callbacks.

Service dependency requirements are projected once per immutable ProcessDefinition instance into a weakly keyed, thread-safe cache. Generated-attachment admission and final binding reuse that set; coverage is checked only at final binding, after all contributions are available. The cache retains no repositories or invocation data.

Required dependency preparation uses the shared WeakPreparationCache/PreparationSlot mechanism. Successful projections are retained for the immutable definition lifetime; failed preparation remains retryable. Transition and query references are sorted once by canonical identity, revision and complete fingerprint, and the same arrays drive diagnostics and host attachment.

## HTTP implementation boundary

ServiceApiBindingsBuilder lives in the ASP.NET adapter because its recipes select HTTP input, response and native
route configuration. It is not another portable service IR: ApiDefinition and ServiceDefinition remain authority.
The generic IServicePersistence contract only resolves canonical observations and bounded query preparations;
PostgreSQL implements it using existing mappings, repository constructors and native query compilation. Provider
configuration, schema lifecycle and source disposal remain native. Multi-provider/composed placement must be
supplied explicitly by a resolver; this batch does not infer federated query plans.

Build is explicit eager host preparation, matching the example's previous startup lifetime. Request inputs,
authorization and query results remain invocation-scoped. Repeated Build on the same builder, service collection and persistence scope returns the same prepared
surface, including logging registrations; using a different scope is a coded setup error. Route staging prevents partial publication on a failing contribution; native callbacks must be pure
registration code. Exact template collisions are rejected, while general route-pattern overlap is not solved
by a competing router. Protected failure logging remains opt-in and uses the existing generic host lifetime.


`ServiceImplementationPlan` in `Cohesive.Api.Execution` owns endpoint authority, unique coverage and
bounded query selections. `Plan.Definition` validates coverage without resolving persistence. Query and
process endpoints are admitted at `Implement`; entity callbacks collect their endpoint bindings before
repository attachment. The ASP.NET builder retains only native attachment recipes and delegates portable
admission to that plan. AppHost consumes `Plan.QueryDependencies` through the portable contract.

Process endpoints use `Endpoints.Process(service, "fulfill", process).Route("POST", "/fulfillment")`.
The typed process argument supplies input/output types that an untyped, separately declared service document
cannot provide through C# inference. The projected handle retains exact service and process references;
implementation checks those references before storage access, without projecting a second contract or
maintaining a parallel field comparison. Generic HTTP attachment preserves the retained proof.

The PostgreSQL service resolver owns a thread-safe query cache keyed by query instance and row/byte bounds,
for the resolver lifetime. It holds no input, authorization or result state. Repeated endpoint selections share
the prepared reader; a different bound produces a different preparation. `Ok()` supports value and collection
results; `OkOrNotFound(predicate)` requires explicit absence semantics for value types.


Process projection returns `ServiceProcessEndpoint<TInput,TResult>`, and process `Bind` requires that
handle. All `WithHttp` dispatch, including through either base endpoint type, preserves the exact service,
process and operation ID. The current finite host admits one service operation; a multi-operation service
is rejected during contribution, before persistence, rather than selecting an operation implicitly.
`ExecutionDefinitionDocument.Reference` projects declared metadata and is excluded from serialization;
it does not substitute for fingerprint validation.

Each `Implement` contribution is atomic: if any registration fails, its endpoint coverage, query selections,
API declaration and host recipes are restored. A callback cannot freeze an in-progress contribution.
Contract admission uses `ServiceBindingValidationException`; route state failures use coded
`ServiceMappingException : InvalidOperationException`, preserving the prior catch boundary.


### Tracked extension: multi-operation finite hosting

The fluent `Endpoints.Process(...)` helper is currently a **single-operation finite-host profile**.
It rejects multi-operation declarations at authoring, even if the chosen ID exists. General service
IR still supports multiple operations, and `ServiceRuntime` deliberately requires complete binding coverage.
Do not implement selection by silently dropping other operations or rewriting the service document.
The follow-up is a shared host composition that binds every selected operation to one complete runtime,
retains the original service fingerprint, and tests two operations over the same process with different
authorization/timeout policies. Remove this authoring restriction when that complete host path is available.

Mapping and admission failures both expose `DocumentValidationResult Validation`; mapping failures retain
`InvalidOperationException` as their base because repeated/incomplete attachment is a host state error.


Mapping diagnostics locate missing, repeated or foreign operations at `/api/endpoints/{id}` and duplicate
native routes at `/api/routes/{method}/{template}` (JSON Pointer escaping). Missing API metadata identifies
the staged endpoint index; repeated mapping identifies `/api/mapping/state`. Coverage failures retain all
identified endpoint diagnostics instead of a single generic location. Process endpoint authoring retains the
one service document built during selection and reuses it for HTTP projection.

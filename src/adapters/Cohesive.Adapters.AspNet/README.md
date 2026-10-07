# Cohesive.Adapters.AspNet

ASP.NET Core endpoint and request-binding adapters for Cohesive APIs, entities, relations, processes, and identity context.

## Install

```bash
dotnet add package Cohesive.Adapters.AspNet
```

## Use When

- You want to expose Cohesive API declarations through ASP.NET Core endpoints.
- You need route builders for entity operations, relation queries, process execution, or process status.
- You want ASP.NET request identity and scope policy enforcement to flow into Cohesive operation context.

## Example

```csharp
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Microsoft.AspNetCore.Http;

var api = Api.Define("Notes");
var getNote = api.Entity<NoteResource>()
    .Query("Get")
    .Route("GET", "/notes/{id}")
    .RouteParameter<string>("id")
    .Returns<NoteResource>()
    .Build();

app.MapEntityApiDefinition(api.Build(), new EntityApiEndpointOptions
{
    Entity = NoteEntity.Instance.Definition
}
    .Bind(getNote.Get(static (_, snapshot) =>
        Results.Ok(ToResource(snapshot)))));
```

## Generic semantic API projection

The root `Cohesive.Adapters.AspNet` namespace owns the direct Minimal API interpretation of `ApiDefinition` and
`ApiEndpoint`. Mapping preserves each original `ApiOperation`, `HttpBinding`, authorization requirement, scope
policy, and semantic reference as endpoint metadata. Definitions are mapped in stable declaration order, and the
optional callback can attach additional ASP.NET metadata without changing semantic authority.

```csharp
using Cohesive.Adapters.AspNet;
using Cohesive.Api;

var definition = Api.Define("Shipping")
    .Query("Health")
        .Route("GET", "/health")
        .Returns<HealthResponse>()
        .Done()
    .Build();

app.MapApiDefinition(
    definition,
    operation => operation.Name switch
    {
        "Health" => () => Results.Ok(new HealthResponse("ready")),
        _ => throw new InvalidOperationException($"No handler is bound for '{operation.Id}'.")
    });
```

For secured operations, supply an `AspNetAuthorizationPolicyResolver`. Mapping fails closed before registering any
route when a definition contains an authorization requirement without a valid ASP.NET policy projection.

This surface previously lived in the `Cohesive.Api` assembly and namespace. Existing ASP.NET hosts must reference
this adapter and add `using Cohesive.Adapters.AspNet;`; no compatibility shim remains in the host-neutral package.

## Canonical relation/query evaluation

Relation/query endpoints author a new `RelationQueryEvaluation` for each HTTP request and delegate the complete
compile-realize-plan-execute pipeline to `IRelationQueryEvaluator`. The request context supplies the evaluation
identity so runtime evidence, diagnostics, and traces remain correlated with the HTTP request. The result mapper is
required: the in-process evaluation outcome deliberately is not treated as a default wire contract.

```csharp
using Cohesive.Adapters.AspNet.Relations;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;

// Configure an evaluator with the application's placement policy and source readers.
builder.Services.AddSingleton<IRelationQueryEvaluator>(relationQueryEvaluator);

var api = Api.Define("Transportation");
var loads = api.Action("SearchLoads")
    .Route("GET", "/loads")
    .Query<SearchLoadsRequest>()
    .Returns<SearchLoadsResponse>()
    .Build();

app.MapRelationQueryApiDefinition(api.Build(), new RelationQueryApiEndpointOptions()
    .Bind(loads.RelationQuery(
        (context, request) =>
        {
            var search = (SearchLoadsRequest)request!;
            return loadsByCustomerDocument
                .Evaluate(context.EvaluationId, loadShapeDocuments, relationshipCatalog)
                .Set(customerNameParameterId, ObservationValue.FromString(search.CustomerName))
                .Select(loadRowsId)
                .Build();
        },
        (_, outcome) =>
        {
            if (outcome.Result is not { IsSuccessful: true } result)
                return Results.UnprocessableEntity(outcome.Compilation.Diagnostics);

            var rows = result.QueryResults.Single(branch => branch.Result == loadRowsId).Rows;
            return Results.Ok(new SearchLoadsResponse(rows));
        })));
```

`EvaluationIdSelector` can override the default `aspnet/request/.../operation/...` convention when an application
already has a stable correlation identity. The endpoint verifies that the per-request factory and evaluator preserve
the selected identity and passes one effective token, linking operation cancellation with
`HttpContext.RequestAborted`, through request binding, evaluation authoring, execution, and result mapping.

Entity-declared query endpoints use this same canonical binding rather than a repository-specific Entity query path.
Map point reads and writes with the Entity adapter, then map the query endpoint from the same API definition with the
Relations adapter. Each mapper emits only its bound endpoints, so the route is created exactly once:

```csharp
var definition = api.Build();

app.MapEntityApiDefinition(definition, new EntityApiEndpointOptions
{
    Entity = NoteEntity.Instance.Definition
}.Bind(noteGet.Get(static (_, snapshot) => Results.Ok(ToResource(snapshot)))));

app.MapRelationQueryApiDefinition(definition, new RelationQueryApiEndpointOptions()
    .Bind(noteSearch.RelationQuery(
        (context, request) => NoteQueries.Search(
            context.EvaluationId,
            (SearchNotesRequest)request!),
        static (_, outcome) => MapSearchResponse(outcome))));
```

The required result mapper receives the complete canonical outcome, including rows, aggregations, requirement gaps,
diagnostics, and provenance, and remains responsible for the endpoint's HTTP status policy.

## Canonical Process observation reads

`MapProcessExecutionInspectApi`, `MapProcessExecutionExplainApi`, and `MapProcessExecutionTracesApi` project the
existing route-neutral `ExecutionControlApiCatalog` handles as HTTP GETs without adding status, explanation, or trace
DTOs. Each route carries only the logical Process identity. Their shared `ProcessExecutionAuthorityScopeResolver`
must derive authority and tenant from authenticated server-side identity and scope evidence; it must not copy them
from caller data. The required authorization-policy resolver maps each catalog semantic authorization requirement to
ASP.NET authorization metadata.

```csharp
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api.Execution;

var executionControl = ExecutionControlApiCatalog.Create();

app.MapProcessExecutionInspectApi(
    executionControl.Inspect,
    "/api/processes/{processInstanceId}",
    (operationContext, httpContext, processInstanceId) =>
        ResolveAuthorizedProcessScope(operationContext, httpContext, processInstanceId),
    (operation, requirement) => ResolveAuthorizationPolicy(requirement));

app.MapProcessExecutionExplainApi(
    executionControl.Explain,
    "/api/processes/{processInstanceId}/explain",
    (operationContext, httpContext, processInstanceId) =>
        ResolveAuthorizedProcessScope(operationContext, httpContext, processInstanceId),
    (operation, requirement) => ResolveAuthorizationPolicy(requirement));

app.MapProcessExecutionTracesApi(
    executionControl.Traces,
    "/api/processes/{processInstanceId}/traces",
    (operationContext, httpContext, processInstanceId) =>
        ResolveAuthorizedProcessScope(operationContext, httpContext, processInstanceId),
    (operation, requirement) => ResolveAuthorizationPolicy(requirement));
```

The inspect binding resolves `IProcessExecutionRepository`, performs its provider-neutral logical read, and returns
only a retained canonical `ExecutionStatus` inside the catalog's existing `ExecutionControlResult` with exact
`Inspected` disposition. Missing executions and pending admissions without canonical status produce the same opaque
not-found problem; provider lifecycle values are never promoted into semantic status. The explain binding resolves
`IProcessExecutionExplainRepository` and writes the successful `ExecutionExplainArtifact` as exact canonical bytes
from `ExecutionExplainJsonSerializer`. Missing or malformed explanation targets use the catalog's opaque problem
variants. Conflicting status, runtime, or trace affinity fails closed. The original catalog remains route-neutral and
unchanged. The trace binding resolves `IProcessExecutionTraceRepository`; available artifacts are written as exact
canonical bytes from `ProcessExecutionTraceJsonSerializer`, while missing, active, and terminal-without-artifact
states map to the catalog's opaque not-found, conflict, and precondition-failed results.

## Related Packages

- `Cohesive.Api` for semantic API declarations.
- `Cohesive.Api.Execution` for the canonical execution-control catalog and safe result projections.
- `Cohesive.Identity` for identity context and scope resolution.
- `Cohesive.Processes`, `Cohesive.Relations`, and `Cohesive.Storage` for the runtime surfaces exposed by endpoints.

## Declared service operations

For an admitted `ServiceRuntime`, `MapServiceTransition<TInput,TOutcome>` projects the operation
without per-route repository loading or commit callbacks:

```csharp
using Cohesive.Adapters.AspNet.Services;

app.MapServiceTransition<ReviseNote, bool>(
    runtime, "revise", "/notes/{id}/revise",
    authorizationPolicyResolver: (_, requirement) => requirement.Id);
```

The CLR types must match the referenced Transition contracts. The caller sends the opaque reviewed token
in `X-Expected-Concurrency-Token` and receives the next token in the same response header. The response
body is the Transition outcome. Routes retain standard API metadata and native ASP.NET authorization
policy associations, while direct and HTTP calls both pass the runtime's mandatory authority binding.
Repository factories are not resolved during mapping. This initial profile supports existing subjects
without emissions; see [its guarantees and qualification](../../../docs/decisions/declarative-service-runtime.md).

Process entries and lifecycle controls project their native command contracts through the same service runtime:

```csharp
app.MapServiceProcessStart(runtime, "publish", "/notes/publish",
    authorizationPolicyResolver: (_, requirement) => requirement.Id);
app.MapServiceProcessControl<PauseProcessCommand>(runtime, "pause", "/notes/pause",
    authorizationPolicyResolver: (_, requirement) => requirement.Id);
```

`PauseProcessCommand` is the native `Cohesive.Execution` contract. A mismatched command CLR type fails mapping.
Native API result definitions determine statuses and response bodies. Service admission failures use the existing
`ExecutionApiProblem`; native Process decisions retain their own result. Start/control authority and exact-target
admission run in the shared service runtime, while ASP.NET policy metadata remains an additional host integration.

Declared terminal results use `MapServiceProcessResult<TResponse>` with the portable service document,
a lazy runtime resolver, operation identity, GET route and a pure `PortableValue` response projection.
The exact Process remains the output-contract authority. Declaration-derived metadata preserves result
alternatives, capability requirements and supplied scope policies without constructing repositories.
Invocation checks that the resolved service has the registered identity, revision and fingerprint, then
uses the runtime's protected terminal reader. Success emits the selected response view; pending and
rejected reads use the shared declared problem/status projection. No entity concurrency token is emitted.

For example, a compiler may return diagnostics without creating an entity. Its result endpoint can
return that retained output directly rather than selecting a nonexistent entity receipt. Tests exercise
successful serialization, forbidden reads without protected storage access, pending HTTP 202, lazy
construction and rejection of a mismatched runtime. These are mapped endpoint tests, not deployed
middleware or remote-provider qualification.

Process start and lifecycle-control endpoints also accept a portable service declaration plus a lazy
runtime resolver. `ServiceApiProjection.ProjectProcess<TRequest>` derives their native command types,
result alternatives and service authorization requirements without creating execution bindings.
Both eager and lazy overloads use the same request reader and invocation path. The lazy resolver must
return the registered service identity, revision and fingerprint; mismatch fails before dispatch.
Host scope policies can be attached explicitly. Inspection and Signal ingress are rejected by the same
lifecycle admission rule at projection and runtime binding, rather than generating unusable endpoints.

For a domain-specific request body, use `ServiceApiProjection.ProjectProcessInput<TRequest>` and
`MapServiceProcessInput<TRequest>`. A synchronous medium binder returns native command/idempotency/
continuation identities and an `ObservationValue` input; it must perform no reads or writes and must
preserve all retry values. The service runtime chooses the exact Process and trusted authority,
validates input against its Process-owned portable contract, and dispatches native admission. The
response retains native admission/conflict outcomes; it does not imply workflow completion. The
medium request is a projection, not a competing semantic input contract. This profile does not add
bounded completion waiting or custom result-read orchestration to starts.

Declared queries use `ServiceApiProjection.ProjectQuery<TRequest,TResponse>` and
`MapServiceQuery<TRequest,TResponse>`. Registration validates the service/query operation and attaches
host scope policies without resolving runtime/evaluator dependencies. The pure request projection
supplies caller parameters; ServiceRuntime injects the trusted scope parameter and rejects attempted
scope overrides before evaluator resolution. Evaluation identity reuses the existing relation-query
HTTP convention, and the native runtime retains compilation, output-demand and provider semantics.

The pure response projection receives the complete native outcome, including failed evaluations,
so it can preserve/redact native phase diagnostics intentionally. A successful outcome uses the primary
response; failed evaluation uses the typed `queryEvaluationFailed` alternative. Admission failures
use the separate standard `admissionValidationFailed` problem. Both validation alternatives are
represented in generated API contracts. Provider exceptions and cancellation propagate normally;
there is no hidden retry or result cache. The mapper adds no query execution algorithm.

### Authored entity transitions

`EntityApiOperationBinding.Transition(operationName, authoredTransition, createTransitionInput, createResult)`
accepts a `Transition<TEntity, TInput, TOutcome>` directly. It compiles once at binding construction,
fails registration with `TransitionApiPreparationException.Compilation` retaining canonical diagnostics on invalid declarations, and reuses the prepared plan
for request execution. Declare the API operation with `authoredTransition.Reference` to retain exact
identity/revision/fingerprint checks. Preparation lifetime is the binding, not a global cache.
The compiled-plan overload remains available for explicit preparation or external shape graphs.

### Typed entity endpoint bindings

`TypedEntityApiBindings<T>` offers combined or separate declaration/binding over the same existing
Create/Get/Transition handlers. The registration session prepares one entity materializer, and each
completed transition binding prepares one plan. It does not implement another persistence or execution path.

Separate declarations retain ordinary portable `ApiEndpoint` handles:

```csharp
var get = Api.Define().Entity<Order>().Query("Get")
    .Route("GET", "/orders/{id}").RouteParameter<string>("id")
    .Returns<OrderSummary>().Result(ApiResultKind.NotFound).Build();

app.MapEntityApi<Order>(entity, repository, "local", endpoints => endpoints
    .Get(get, order => TypedResults.Ok(new OrderSummary(order.Id, order.Status))));
```

The equivalent combined form creates that same endpoint representation:

```csharp
app.MapEntityApi<Order>(entity, repository, "local", endpoints => endpoints
    .Get("Get", "/orders/{id}", order => TypedResults.Ok(new OrderSummary(order.Id, order.Status))));
```

Creation accepts a typed initializer receiving the effective binding partition, an identity selector and `Created<T>` response. Its declaration uses
`.Returns<T>(ApiResultKind.Created)` (201); combined authoring supplies this automatically. Combined
lookup and transition declarations include NotFound (404); separately declared handles should include it. Transition bindings use
`.Transition(endpoint, authored).Input(request => command).OnApplied((state, outcome) => TypedResults.Ok(response))`
followed by `.OnRejected(outcome => TypedResults.Conflict(response))` or an explicit 409
`TypedResults.Problem(...)`; the latter declares `ProblemDetails` and aligns the body shape with concurrency
conflicts. The input callback receives the existing `EntityApiRequestContext`, including `RequiredEntityId`,
operation and HTTP context. Combined authoring accepts name/route
instead of an endpoint. Response generic types are inferred from native TypedResults. Existing endpoint
handles are checked at registration for entity, operation kind, request and primary response types; transition
handles must reference the exact authored revision/fingerprint and declare the typed Conflict result.
Combined transition authoring adds that result declaration automatically. Only admission/domain rejection
reaches OnRejected; unexpected execution failures are not converted into domain rejection.

The current convenience surface covers bodyless create/get commands and route-derived transition inputs,
with an explicit fixed point-read partition and conventional `id` route key. It does not infer authentication
or tenant policy. Use existing lower-level bindings for request bodies, custom route/partition policies,
asynchronous result projections or emitting transitions. Native endpoint handles carry no ASP.NET delegates;
CLR callback types are checked at compile time, while agreement with a supplied declaration is validated at
registration, including the primary success kind. This is not compile-time certification of arbitrary serialized definitions.

Sessions are registration-scoped and non-thread-safe, reject duplicates and unfinished transitions, and freeze
after Map. Entity observations use a compiled materializer; structured transition outcomes use the existing
ObservationValue conversion contract. Keep custom serialization conventions aligned with canonical authoring.

### Exception-to-HTTP integration

```csharp
builder.Services.AddCohesiveExceptionHandling();
// After Build, before request middleware and endpoints:
app.UseExceptionHandler();
```

The transition binding already turns a lost conditional commit into a sanitized 409 result without any
global handler. It and `ServiceRuntime` share `ApiProblemCodes.ConcurrencyConflict`; the HTTP projection
uses Problem Details, while the service result retains its semantic diagnostics contract. The optional
fallback above covers storage exceptions from other endpoints.

This registers a native `IExceptionHandler` and Problem Details services. An
`ObservationConcurrencyConflictException` becomes HTTP 409 with `application/problem+json`, a stable
`code` of `services.concurrency.conflict`, and a `traceId`. The response contains a safe reload
instruction, never the exception message or backend identity/token. No request or decision is retried.
A sanitized JSON fallback is used if no configured Problem Details writer accepts the response.

Unknown exceptions and responses that have already started are not handled. Other application handlers
and ASP.NET's normal fallback retain responsibility for them; registration order follows native ASP.NET
conventions. Domain rejection is an ordinary `.OnRejected(...)` result and does not enter this mapping.
The mapping does not change native server-side logging or application-supplied Problem Details customizers.

Applications choosing `Conflict<TDomain>` for domain rejection will have two 409 body shapes: their
explicit domain body and Problem Details for concurrency. Use the Problem Details `OnRejected` overload
(as the order example does) for one HTTP error shape; distinguish conditions by their stable `code`.

## Bind a typed read operation

Use `app.MapApiQuery(endpoint, preparedRead).FromRoute<Guid>("id", id => id.ToString("D")).OkOrNotFound()`
when a separately declared, bodyless `ApiEndpoint<TResult>` returns a nullable typed result.
Use `Build<TResult>()` on the declaration to retain the response type at the binding boundary. `preparedRead` is a
`IRelationQueryReader<TInput, TResult?>` retaining its canonical `Definition`; pass the object, not
its `ReadAsync` method group. No database adapter dependency is required. Registration
checks the endpoint's query kind and response type, route declaration and 404 policy. Invalid input returns
400 before invocation, request cancellation propagates, and null returns the declared empty-body 404.
Other results use 200 JSON. The native route builder remains available for further configuration, and
semantic authorization still requires the existing policy resolver. The binder owns no query compilation,
result assembly or retry policy.

The typed reader is a complete-result convenience contract. For composed/cross-source evaluation with
phase artifacts, requirement gaps and source-read traces, retain the existing `MapRelationQueryApiDefinition`
and `IRelationQueryEvaluator` path. Do not turn a failed or partial evaluation into an ordinary typed success.

The `OkOrNotFound` convenience requires a reference response type: absence is represented by null,
not the default value of a struct. Request-typed `ApiEndpoint<TInput, TResult>` handles built with
`BuildQuery<TInput, TResult>()` can be passed directly to `MapApiQuery`; their query DTO is bound by
the existing HTTP query binder. Body requests remain outside this query convenience.
`BuildBody` and `BuildQuery` validate a prospective request/response/HTTP declaration before publishing it;
failed preparation leaves the builder unchanged without rollback.

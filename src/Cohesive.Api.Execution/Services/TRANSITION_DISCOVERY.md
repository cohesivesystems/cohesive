# Actor-specific entity transition discovery

`ServiceRuntime.InspectTransitionsAsync` inspects the registered `ServiceTransitionOperation` catalog for
an exact entity authority. It uses the same compiled Transition plans, repository bindings and invocation
authorization policy as execution. There is no second lifecycle or role catalog.

```csharp
var options = await runtime.InspectTransitionsAsync(
    context, loadEntity.StateShape.QualifiedId, loadId);

var refined = await runtime.InspectTransitionsAsync(
    context, loadEntity.StateShape.QualifiedId, loadId,
    new Dictionary<string, PortableValue>
    {
        ["release"] = PortableValue.Concrete(
            runtime.InputContract("release"),
            ObservationValue.FromObject(releaseCommand))
    });
```

Missing nonempty commands conservatively produce `RequiresInput`. The API does not invent command values
or claim symbolic partial-input evaluation. Empty object commands can be evaluated automatically. With
input supplied, the full canonical Transition runs without committing: machine admission, transition
Requires, patches in an isolated candidate, and final invariants all participate. Candidate state,
patches, command values and authored outcome payloads are not returned. Accepted decisions that require
a commit also pass through the same noncommitting entity preparation as invocation. Preparation failures
(such as version overflow) report `Indeterminate` with the shared sanitized diagnostic; `Decision` still
records the native interpreter result. Decisions requiring no commit skip preparation, matching invocation.

Each result contains the operation identity, exact Transition reference, input contract, snapshot concurrency
token, availability kind, native decision classification and payload-free structured diagnostics. Operations
are sorted by identity. `Permitted` means accepted for this actor, command and snapshot; `Unavailable` means
ordinary rejection; `Invalid` covers invalid input/model/invariants including machine ambiguity;
`Indeterminate` covers evaluation failure. These are advisory results, not execution capabilities.

## Permissions and roles

Declare domain capabilities on operations, independently of machine lifecycle guards:

```csharp
new ServiceTransitionOperation("release", loadEntity.StateShape.QualifiedId,
    releasePlan.DefinitionReference, [new("loads.release")]);
new ServiceTransitionOperation("cancel", loadEntity.StateShape.QualifiedId,
    cancelPlan.DefinitionReference, [new("loads.cancel")]);

var authorization = new IdentityServiceInvocationAuthorization(
    scopeKind: "organization", ownershipField: new("OrganizationId"));
```

The host's identity boundary resolves roles, delegation rules and organizational assignments to explicit
`IdentityScopeGrant` capabilities. For example, a dispatcher role may grant `loads.release`, while a supervisor
role also grants `loads.cancel`. Role names are not embedded in the state machine. The existing identity policy
requires live grants for the actor and selected scope, verifies logical entity ownership after loading, and
does not treat scope selection as permission. Unsupported delegation is denied. More complex relationship
or separation-of-duties rules use the existing `IServiceInvocationAuthorization` policy boundary.

Discovery defaults to invocation authorization: unauthorized operations are hidden, and denied scope admission
happens before binding/repository resolution. Inaccessible and missing subjects both yield no entries. Hosts
may provide a separate `discoveryAuthorization` policy when constructing the runtime; this policy governs
visibility and resource access. An operation visible under that policy but denied by invocation policy is
returned as `Forbidden`, without evaluating its Transition. Both policies must authorize the same logical
scope and physical placement for an operation to be evaluated. Visibility policy is host configuration,
never a request-provided override. Input-dependent authorization is not added to the existing policy interface;
model distinct elevated operations or provide an appropriate future policy extension where required.

## Execution and consistency

Call `InvokeAsync` using the operation and concurrency token from inspection. The ordinary invocation path
reauthorizes the actor and resource, reloads current state, verifies the token, evaluates the Transition again,
and conditionally commits. Permission revocation and concurrent entity changes can invalidate a previous
Permitted result. Inspection performs no writes, emits no events, and dispatches no Process work.

Each entry carries its own authoritative snapshot token. Inspection may read multiple repository bindings and
is not an atomic catalog snapshot. Within one call, operations with the same repository instance and
structurally equal authorized scope share one validated full snapshot and observation projection.
Different repository instances or scopes do not share a read, even when their entity IDs match.
Scope admission and resource authorization still run independently for every operation. When discovery
and invocation use the same policy instance, successful checks are reused only for that operation;
separate policies each run once. No command, snapshot or grant evidence is cached across calls. Repository/authorization
infrastructure errors propagate to the host. This initial API covers declared direct Transition operations;
Process and query availability are not inferred, and no transport endpoint is exposed automatically.

Tests in `LogisticsTransitionDiscoveryTests` exercise release/cancel capability grants alongside machine guards.
Tests in `ServiceRuntimeTests` cover no-write evaluation, required input, visibility versus invocation,
resource denial, explicit capability grants, expiration, permission revocation, stale tokens and additional
Transition preconditions.

A runnable [load-actions consumer](../../../../eng/examples/load-actions/README.md) demonstrates
role-filtered release/cancel choices, command refinement and conditional execution. Discovery benchmarks
cover 1/8/32 operations both with and without complete commands; see
[performance evidence](../../../../docs/performance/transition-discovery/README.md).

## Typed HTTP projection

Hosts can explicitly expose discovery for an entity whose direct transitions share an exact input contract:

```csharp
var endpoint = runtime.ProjectTransitionInspection<Command>(loadEntity.StateShape.QualifiedId,
    new HttpBinding("POST", "/loads/{id}/actions",
        [new("id", HttpParameterSource.Route, typeof(string))],
        new(typeof(ServiceTransitionInspection<Command>))));

// ASP.NET mapping performs the same projection and preserves the execution document as metadata.
app.MapServiceTransitionInspection<Command>(runtime, loadEntity.StateShape.QualifiedId,
    "/loads/{id}/actions");
```

`ServiceTransitionInspection<TInput>.Inputs` contains complete commands keyed by service operation ID.
An omitted entry retains the runtime's `RequiresInput` behavior. The adapter rejects unknown operation
keys. The host selects the entity at registration; callers cannot select another entity or override
visibility or authorization. Normal request identity enrichment applies before inspection.

`ServiceTransitionAction` is a payload-free transport projection: operation ID, generated invocation API ID,
native availability kind, opaque concurrency token, and sanitized diagnostics. It intentionally omits
runtime contracts, candidate state and authored outcomes. The API ID uses the same identity projection as
`ServiceRuntime.Project<TInput, TOutcome>`, allowing generated clients to match actions to invocation
functions without maintaining another operation table. The declared request/response CLR contracts feed
the existing shape and TypeScript API generators; unsupported or mixed input contracts fail registration.

The [logistics browser example](../../../../eng/examples/load-actions/README.md) uses this endpoint and
committed generated clients, validates complete input before enabling submission, and refreshes after
conflicts. Discovery remains advisory: invocation always reauthorizes and checks concurrency.

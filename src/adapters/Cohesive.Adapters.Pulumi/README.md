# Native Pulumi graph projection

`PulumiGraphProjection` consumes a complete `InfrastructureTargetDeploymentPlan` and drives native
factories from its selected declarations. It owns coverage, construction ordering and association
accounting. Pulumi owns resources, arguments, options, outputs, state and provider execution.

```csharp
var projection = new PulumiGraphProjection(plan);
projection.Map(databaseNode, context =>
{
    var database = new NativeDatabase("existing-logical-name", new NativeDatabaseArgs
    {
        // Ordinary provider arguments and native options remain here.
    });
    context.Associate(databaseNode, database);
    return database;
});
projection.Map(workerNode, context =>
{
    var database = context.Get<NativeDatabase>(databaseNode);
    var worker = new NativeWorker("existing-worker-name", new NativeWorkerArgs
    {
        DatabaseId = database.Id
    });
    context.Associate(workerNode, worker);
    return worker;
});
var native = projection.Execute();
```

The provider types above are illustrative; the executable tests use native `CustomResource`
construction, provider mocks and secret outputs. The database relationship must exist in the
compiled graph, or be an explicit `After(node, reason)` native construction refinement.

## Contract

- Every selected workload and resource must have exactly one factory. Unknown, duplicate and
  excluded registrations fail. Missing coverage and cycles fail before any factory is invoked.
- Binding targets and readiness dependencies are constructed before their subjects. This is an
  ordering interpretation, **not** a live readiness check, automatic IAM grant or automatic native
  `DependsOn`. Native output references and options retain Pulumi's execution dependencies.
- An indivisible native facility can handle several related declarations in one invocation. Each
  declaration still requires an explicit association; returning a successful object is insufficient.
- Factories return their existing native resource or adapter result. Typed retrieval rejects wrong
  result types and undeclared dependency access; it never awaits outputs or strips secret metadata.
- External lifecycle declarations can record an explicit metadata-only reference. Persistent
  resources cannot be excused as external. Non-participation comes from the compiled manifest,
  not an interpreter-side skip list.
- Native ordering refinements carry a reason and participate in the same preflight cycle check.
  Semantic relationship cycles that cannot be constructed as one native facility are unsupported;
  they fail rather than silently losing ordering. A general late-binding interpreter is not provided.
- A projection is invocation-scoped, sequential and single-use. Callback exceptions propagate;
  partial native registrations are possible and there is no automatic retry or rollback.

## Ownership and limits

This is a provider-independent Pulumi adapter, not portable IR and not a provider-options wrapper.
Existing Azure attachment adapters continue to validate physical names, scopes, classified outputs
and authorization policy. Grouping validates the declared ownership set. Ordinary Pulumi-only resources and provider children
may coexist without mandatory Cohesive registration.

The result records primary logical/native associations and explicit references. It does not intercept
arbitrary Pulumi constructors elsewhere in the program, prove provider output identity by itself,
or establish runtime readiness. Consumers should test their actual stack registrations and retain
explicit ownership for bootstrap resources outside the application definition.

The existing Azure construction/binding helpers and Aspire handoff were evaluated: they validate
individual associations and exact plans but do not drive whole-plan construction. This component
adds that responsibility while reusing their plan and native result types. Canonical IR contains no
factory delegates, native objects or second dependency catalog.

Preflight indexes selected nodes and dependencies once. Stable topological traversal is bounded by
the graph and factory count; each factory runs once regardless of consumer fan-out. There is no
global cache or backend call in the traversal itself.

## Validation

`Cohesive.Adapters.Pulumi.Tests` covers native construction and secret preservation, missing/duplicate
coverage, excluded nodes, cycles, external-reference policy, missing associations, dependency access
and no-retry behavior. Consumer tests must additionally exercise real provider-specific attachments
and the full stack against the authoritative manifest.

## Coauthor placement and native realization

`InfrastructureTargetImplementation` pairs an implementation family with attributable leaf capability
assertions. The inline overload of `InfrastructureTargetDeployments.Define` collects the selected
implementations into the existing facility/profile IR. A separate facility manifest remains supported
for reusable target catalogs. Both paths use the same compilers and canonical wire formats.

`PulumiDeploymentProjection<TContext>` joins each physical placement to its native factory. Its
`Define` method takes the canonical manifest producer and a projection callback. For example, inside
that callback (names below are illustrative):

```csharp
projection.Group()
    .Resource(stateNode, cosmosImplementation, databaseIdentity, stackAuthority, sources)
    .Create((native, context) =>
    {
        var database = new NativeDatabase("state", new NativeDatabaseArgs
        {
            AccountName = native.ExistingAccount.Name
        });
        context.Associate(stateNode, database);
        return database;
    });

// In an existing native composition, associate the original object without recreating it:
projection.Group()
    .Resource(archiveNode, blobImplementation, archiveIdentity, stackAuthority, sources)
    .UseExisting(native => native.Archive);
```

`TContext` supplies invocation-owned native configuration/resources. Define and compile can run
without a Pulumi deployment or provider calls. Execute requires the exact compiled manifest and
reuses `PulumiGraphProjection` for coverage, ordering, native dependency access and association checks.
A group can co-locate multiple placements with a single factory. Factory code owns ordinary SDK
arguments and options; existing Azure attachments still validate resolved physical identity.

The immutable projection can describe multiple invocations; each Execute creates a fresh single-use
graph interpreter. Callers must not re-execute against the same native deployment after a partial
failure. There is no retry, resource import, rollback or lifecycle transfer. UseExisting resources have
already been registered by their native owner, so preflight cannot undo their registration.

Partial adoption is intentional. Additional resources may be constructed with ordinary Pulumi APIs,
including prerequisites supplied through TContext. They need no synthetic Cohesive declarations.
Coverage checks concern selected Cohesive nodes only; no claim is made about undeclared resources
or capabilities. Capability assertions are target evidence, not observations of runtime readiness.
The corresponding Aspire-first convenience experience is a subsequent adapter deliverable.

Executable tests cover canonical fingerprint equivalence between separate/inline authoring, deferred
native execution, existing resources alongside unmodeled resources, conflicting implementation
claims and missing factories. No callback or native object is serialized into the canonical manifests.

### Named single-node authoring

Single-node factories can use named placement operations. Import
`Cohesive.Infra.InfrastructureTargetImplementation` with `using static` to declare
`ResourceImplementation(id, evidence...)` or `WorkloadImplementation(id, evidence...)`
without repeating the implementation type or node-kind enum. Evidence remains explicit,
nonempty native or constrained evidence; requirements never supply evidence automatically.

```csharp
var store = ResourceImplementation(new("example/store"), storageEvidence);
projection.Resource(storeNode)
    .Using(store)
    .At(new("example/native/store"))
    .OwnedBy(new("pulumi/example"))
    .SourcedFrom(stackSource)
    .Create((native, context) =>
    {
        // Use ordinary Pulumi constructors and record the association through context.
        return CreateStore(native, context);
    });
```

Use `.UseExisting(native => native.Store)` for an existing native resource. A workload
uses `projection.Workload(node)` and omits `OwnedBy`. `Group()` remains appropriate
when one factory realizes multiple placements. All paths lower into the same canonical
placement methods and native graph interpreter; no new provider model or traversal exists.

Placement configuration is synchronous and invocation-scoped. Named fields may be refined
until `Create` or `UseExisting`; the last explicitly supplied value wins. Finalization rejects
unfinished placements before manifest construction, and retained builders cannot mutate a
completed projection. Missing implementation, identity or resource ownership fails before
native construction. These helpers change authoring only: canonical fingerprints and native
resource ownership are unchanged, and capability evidence is still validated by the compiler.

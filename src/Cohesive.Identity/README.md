# Cohesive.Identity

Identity context, scope resolution, and canonical entity-query directory helpers for Cohesive applications.

## Install

```bash
dotnet add package Cohesive.Identity
```

## Use When

- You need to resolve operation context, principal identity, and scope references in a Cohesive application.
- You want a canonical, backend-independent identity directory over registered entity sources.
- You want a small in-memory identity directory for local development, tests, or bootstrap flows.
- You need identity-aware API or storage adapters to share the same operation context model.

## Example

```csharp
using Cohesive.Identity;

var directory = InMemoryIdentityDomainRepositoryFactory
    .Create(new InMemoryIdentityDirectoryBuilder()
        .AddScope(new("tenant-a", "tenant", "Tenant A"))
        .AddPrincipal(new("user:alice", PrincipalKind.User, Email: "alice@example.com"))
        .AddScopeGrant("user:alice", "tenant-a", "tenant", ["orders.read"], isDefaultScope: true)
        .Build())
    .CreateDirectory();

var principal = await directory.FindPrincipalAsync(new(Email: "alice@example.com"));
```

`CreateDirectory()` registers the three graph-qualified Identity entity shapes with
`EntityRelationQuerySourceCatalog`, creates a canonical evaluator, and passes that evaluator to
`EntityRepositoryIdentityDirectory`. The same directory can run over another placement by constructing it with any
appropriately configured `IRelationQueryEvaluator`:

```csharp
IIdentityDirectory directory = new EntityRepositoryIdentityDirectory(evaluator);
```

## Canonical Directory Queries

`IdentityDirectoryQueries` exposes the stable persisted query documents used for principal, membership, default
scope, and active-scope lookups. The definitions are authored once against
`IdentityDomainModel.ShapeGraphDocument`; invocation values are supplied separately as required typed parameters.
This keeps query identity and fingerprints stable while allowing each evaluator to compile, place, and execute the
same semantics against its registered sources.

The directory preserves the security-relevant lookup contract:

- Principal keys are considered in order: local principal id, normalized email, subject, then client id.
- Active status checks are canonical query predicates, rather than host-side filtering.
- Single-result lookups request at most two rows so ambiguous principal, scope, or visible default-membership data is
  detected instead of selecting an arbitrary row.
- Default membership lookup receives visible candidate scope ids as a typed array parameter and performs canonical
  collection membership before its uniqueness check.
- Returned canonical observations are materialized into the existing Identity record types through the immutable
  core `ObservationMaterializer<T>` convention.

## Fail-Closed Evaluation

Identity directory results are accepted only when canonical evaluation succeeds conclusively and returns the exact
demanded `rows` branch, expected graph-qualified shape, complete rows, string observation identities, and object
payloads that can be mapped to the requested record type. Failed or incomplete evaluation, a foreign outcome,
unexpected result structure, ambiguous single-row data, or mapping failure throws
`IdentityDirectoryEvaluationException`. Cancellation remains `OperationCanceledException` and is propagated without
being converted into an identity failure.

This boundary deliberately does not treat partial evidence as authorization data. The exception retains the exact
`RelationQueryEvaluation` and any returned `RelationQueryEvaluationOutcome`, including attributable compiler,
realization, planning, and execution diagnostics.

## Related Packages

- `Cohesive.Adapters.AspNet` for request identity and API scope policy enforcement.
- `Cohesive.Api` for scope-aware API declarations.
- `Cohesive.Relations` for canonical query authoring, compilation, evaluation, and diagnostics.
- `Cohesive.Storage` for entity-backed source registration and evaluator construction.

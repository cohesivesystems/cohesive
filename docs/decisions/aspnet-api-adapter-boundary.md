---
kind: decision
status: implemented
authority: cohesive.api.aspnet-adapter-boundary
owners: [cohesive-core]
applies_to: [cohesive-api, cohesive-adapters-aspnet]
last_verified: 2026-08-11
supersedes: []
---

# Interpret semantic API declarations through the ASP.NET adapter

## Context

`Cohesive.Api` owns the portable API declaration model used by HTTP, OpenAPI, GraphQL, TypeScript, and future host
interpretations. It nevertheless referenced the ASP.NET Core shared framework and exposed Minimal API route builders
and authorization-policy projection from its public assembly. A consumer that only needed the semantic model
therefore acquired one concrete host runtime, and infrastructure types crossed the semantic package boundary.

The repository already has `Cohesive.Adapters.AspNet`, which owns ASP.NET request binding, identity context, entity,
relation, and Process endpoint realization. The generic Minimal API mapper is part of that same interpretation.

## Decision

Keep `ApiDefinition`, `ApiEndpoint`, `ApiOperation`, `HttpBinding`, result declarations, authorization requirements,
scope policies, and semantic references in `Cohesive.Api`. Move `ApiEndpointRouteBuilderExtensions` and
`AspNetAuthorizationPolicyResolver` into the `Cohesive.Adapters.AspNet` assembly and namespace.

The dependency direction is:

```text
Cohesive.Adapters.AspNet -> Cohesive.Api
Cohesive.Api -/-> Microsoft.AspNetCore.App
```

The mapper remains a direct ASP.NET interpretation. It attaches the original `ApiOperation`, `HttpBinding`, scope,
authorization, and semantic-reference objects as endpoint metadata so runtime endpoints retain provenance to the
canonical declaration. Mapping order, route and method selection, names, summaries, descriptions, tags, body and
result metadata, authorization failure behavior, and configuration callbacks are unchanged.

No target-neutral host facade is introduced. One concrete interpreter is insufficient evidence for a stable shared
host abstraction, and `Cohesive.Api` already supplies the portable semantic contract that other interpreters consume.

## Alternatives considered

### Leave the mapper in Cohesive.Api

Rejected because every semantic API consumer would continue to acquire ASP.NET and core public APIs would continue
to leak infrastructure types.

### Keep the Cohesive.Api namespace from the adapter assembly

Rejected because the namespace would conceal the new ownership boundary and allow call sites to use the ASP.NET
projection without explicitly selecting the adapter.

### Add forwarding types in Cohesive.Api

Rejected because a forwarding shim would preserve the ASP.NET framework reference and the dependency edge this
change removes. The packages are pre-1.0, so the clean assembly and namespace move is preferred.

### Introduce a generic endpoint-host interface

Rejected until another executable host interpretation demonstrates shared lifecycle, routing, metadata, and failure
semantics beyond the existing portable API declaration model.

## Consequences

- Semantic API consumers no longer acquire the ASP.NET shared framework through `Cohesive.Api`.
- ASP.NET hosts add `Cohesive.Adapters.AspNet` and import `Cohesive.Adapters.AspNet` for `MapApiDefinition`,
  `MapApiEndpoint`, and `AspNetAuthorizationPolicyResolver`.
- Existing ASP.NET-specific entity, relation, and Process mappers compose the generic mapper within their adapter.
- Project, assembly, adapter-ownership, package-consumer, and endpoint-provenance tests guard the boundary.
- OpenAPI, GraphQL, TypeScript, and future interpreters continue consuming the same canonical API declarations.

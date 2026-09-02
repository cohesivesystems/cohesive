# Cohesive.Infra

`Cohesive.Infra` describes portable infrastructure requirements, bindings, capability-qualified realizations, and
lifecycle ownership without making a provider SDK or deployment tool authoritative for application meaning.

## Install

```bash
dotnet add package Cohesive.Infra
```

## Define requirements

The common authoring path declares workloads, logical resources, and the contracts that connect them. Resource and
binding identities are durable topology boundaries; compiler-internal nodes are derived:

```csharp
var document = Infrastructure.Define(
    id: new("shipping"),
    revision: new("1"),
    configure: infra =>
    {
        var api = infra.Workload(new("workload/api"));
        var state = infra.Resource(new("resource/state"))
            .Persistent()
            .Requires(ShippingCapabilities.DocumentAuthority)
            .Requires(ShippingCapabilities.ChangeFeed);

        infra.Bind(new("binding/api-state"), api)
            .To(state)
            .As(ShippingContracts.RepositoryReadWrite);
    });
```

`Infrastructure.Define` returns an immutable, normalized, fingerprinted document. Compilers match its requirements
against one coherent target variant, elaborate binding obligations, assign lifecycle ownership, and retain the exact
capability evidence used by the realization.

## What this package provides

- Portable workload, resource, binding, requirement, and lifecycle semantics.
- Deterministic convention resolution with attributable effective configuration.
- Capability closure and boundary-acceptance diagnostics.
- Exact physical placement and evidence-witness documents.
- Lifecycle ownership rules for managed, shared, and externally managed resources.
- Local construction realization used by Docker Compose and Aspire projections.

## Current boundary

The package is a zero-third-party-dependency semantic core. It does not reference Terraform, Pulumi, Aspire, Azure,
AWS, GCP, Kubernetes, or their SDKs. Provider emitters, deployment runners, observation importers, and drift readers
belong in dedicated adapters.

A successful realization proves semantic coverage for the selected target. It is not by itself a deployment receipt,
readiness signal, cost estimate, or observation of current backend state.

## Continue

- [Internals](INTERNALS.md) covers authority boundaries, capability proofs, binding elaboration, local construction,
  lifecycle ownership, the ARI acceptance case, and planned adapters.
- [Language-family architecture](../../docs/architecture/language-family.md) explains capability-driven compilation.
- [`Cohesive.Adapters.DockerCompose`](../adapters/Cohesive.Adapters.DockerCompose/README.md) projects the local
  construction document to Compose.
- [`Cohesive.Adapters.Aspire`](../adapters/Cohesive.Adapters.Aspire/README.md) consumes the same definition and
  realization for local orchestration.

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

`Infrastructure.Define` returns an immutable, normalized, fingerprinted document. A target adapter declares its
physical construction families as an `InfrastructureTargetFacilityManifest`: facilities group exact leaf capability
evidence, while the capability profile remains the sole authority for guarantees, composition rules, and operating
boundaries. `InfrastructureTargetCompiler` then selects one facility per logical node, resolves attributable
conventions, elaborates binding obligations, and proves capability closure without an application-specific compiler.

```csharp
var target = InfrastructureTargetFacilities.Define(
    id: new("azure-pulumi/facilities/v1"),
    profileId: new("azure-pulumi/capabilities/v1"),
    target: new("pulumi-azure-native/3.16.0"),
    variant: new("development"),
    supportedDefinitionSchemaVersions: [InfrastructureDefinitionDocument.CurrentSchemaVersion],
    configure: facilities =>
    {
        facilities.Workload(new("azure/app-service")).Provides(AppServiceEvidence.Https);
        facilities.Resource(new("azure/blob-storage")).Provides(BlobEvidence.ObjectStorage);
    });

var plan = InfrastructureTargetCompiler.Compile(document, target, ShippingBindings.Profile);
```

The fluent builder is only an authoring projection. It materializes the same immutable, serializable, fingerprinted
manifest that direct IR, imported documents, generated catalogs, and agent tooling can produce.

## What this package provides

- Portable workload, resource, binding, requirement, and lifecycle semantics.
- Declarative target-facility manifests and generic facility selection.
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

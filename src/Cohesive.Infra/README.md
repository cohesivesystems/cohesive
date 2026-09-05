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

        api.RequiresReady(state);

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

A lifecycle adapter can additionally declare the exact physical deployment without implementing an application-side
compiler. `InfrastructureTargetDeployments.Define` materializes a portable deployment manifest; the shared compiler
then selects facilities, derives lifecycle dispositions from canonical resource intent, places workloads, constructs
demand-scoped capability witnesses, and reports mismatches:

```csharp
InfrastructureLifecycleAuthorityId pulumiState = new("pulumi/shipping/development");
var targetEvidence = InfrastructureSourceReferences.Target(target.Profile.Target);
var deployment = InfrastructureTargetDeployments.Define(
    id: new("shipping/azure-pulumi/development/v1"),
    definition: document,
    targetFacilities: target,
    configure: physical =>
    {
        physical.Workload(
            ShippingNodes.Api,
            AzureFacilities.AppService,
            AzureResources.AppService("shipping-api"),
            [targetEvidence]);
        physical.Resource(
            ShippingNodes.State,
            AzureFacilities.BlobStorage,
            AzureResources.Container("shipping", "state"),
            pulumiState,
            [InfrastructureSourceReferences.LifecycleAuthority(pulumiState)]);
    });

var realization = InfrastructureTargetDeploymentCompiler.Compile(ShippingInfrastructure.Current, deployment);
```

Application declarations contain no requirement-discharge or witness-construction algorithm. Provider naming and
artifact discovery belong to the adapter; all callbacks are discarded after materializing canonical IR.
Fluent facility and deployment authoring also captures C# call sites in a portable source map. Source maps support
diagnostics and inspection but are excluded from semantic fingerprints, so moving equivalent declarations between
files does not change their canonical identity. Explicit source references remain semantic evidence and should be
projected from typed target, facility, lifecycle, or artifact identities instead of handwritten repository paths.

Canonical `RequiresReady(...)` declarations are lowered by `InfrastructureRealizationCompiler` from logical nodes to
their exact physical placements. Local compilation projects those obligations into the existing topology consumed by
Docker Compose and Aspire, so application code does not repeat physical dependency strings. Aspire continues to own
local orchestration and realizes the relationship as `WaitFor` plus adapter health checks.

Adoption note: topology-level `DependsOn(...)` and direct `ReadyDependencies` remain honored as explicit local
compatibility overrides. When no matching canonical obligation exists, local compilation emits the warning
`infra.local.readiness.notCanonical` without invalidating the local realization. Migrate an edge to semantic
`RequiresReady(...)` only when the application's readiness actually depends on it. A local startup-order preference is
not the same guarantee and should not be promoted into the canonical definition merely to silence the warning.

Adapters can normalize current backend evidence into `InfrastructureResourceObservation` values. The pure
`InfrastructureReadinessEvaluator` compares those observations with the exact realization and returns a fingerprinted
assessment with one decision per physicalized logical node. Missing and unknown evidence fail closed, an unhealthy
dependency blocks its subject even when that subject exposes an endpoint, and an incomplete capability realization
cannot be made ready by favorable runtime health.

## What this package provides

- Portable workload, resource, binding, requirement, and lifecycle semantics.
- Declarative target-facility manifests and generic facility selection.
- Declarative target-deployment manifests and shared physical-realization compilation.
- Deterministic convention resolution with attributable effective configuration.
- Capability closure and boundary-acceptance diagnostics.
- Exact physical placement and evidence-witness documents.
- Exact physical readiness obligations and attributable observation assessment.
- Lifecycle ownership rules for managed, shared, and externally managed resources.
- Local construction realization used by Docker Compose and Aspire projections.

## Current boundary

The package is a zero-third-party-dependency semantic core. It does not reference Terraform, Pulumi, Aspire, Azure,
AWS, GCP, Kubernetes, or their SDKs. Provider emitters, deployment runners, observation importers, and drift readers
belong in dedicated adapters.

A successful realization proves semantic coverage and compiles readiness obligations for the selected target. It is
not by itself a deployment receipt, readiness signal, cost estimate, or observation of current backend state; an
assessment requires separately attributable observations from an adapter.

## Continue

- [Internals](INTERNALS.md) covers authority boundaries, capability proofs, binding elaboration, local construction,
  lifecycle ownership, the ARI acceptance case, and planned adapters.
- [Language-family architecture](../../docs/architecture/language-family.md) explains capability-driven compilation.
- [`Cohesive.Adapters.DockerCompose`](../adapters/Cohesive.Adapters.DockerCompose/README.md) projects the local
  construction document to Compose.
- [`Cohesive.Adapters.Aspire`](../adapters/Cohesive.Adapters.Aspire/README.md) consumes the same definition and
  realization for local orchestration.

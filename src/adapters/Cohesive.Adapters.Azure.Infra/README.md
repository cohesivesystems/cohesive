# Cohesive.Adapters.Azure.Infra

The first COH-112 slice admits exact, attributable Azure evidence into the existing Infra readiness evaluator. It performs no Azure I/O and does not authenticate evidence producers. Native collection, service-specific runtime normalization, bounded request scheduling, cancellation and live qualification remain follow-ups before COH-112 is complete. No Ari-specific concepts or new external dependencies are introduced.

## Authority and data flow

`InfrastructureRealization` owns resources and readiness edges. A trusted caller supplies the expected environment, tenant/subscription, exact realization reference and deployment handoff source reference. Expected `AzureInfrastructureObservationBinding` values associate canonical physical identities with full subscription-scoped ARM resource IDs and native-output provenance. Neither symbolic identities nor ambient credentials are used to invent ARM IDs.

A trusted producer supplies `AzureInfrastructureEvidence` and its observed scope. `Normalize` validates all associations and scope, enforces an explicit UTC assessment instant/maximum age/clock tolerance, and returns existing `InfrastructureResourceObservation` values. Feed these directly into `InfrastructureReadinessEvaluator.Assess(realization, observations)`. No second graph or health/readiness status enum exists.

The three new record boundaries describe distinct information: deployment scope, native association, and classified evidence. They support JSON round-trip but are not a separately versioned durable wire format. Construction alone is not admission: `Normalize` revalidates persisted/external inputs before use. Trust/authentication of an external artifact is the caller's responsibility; copying the expected scope onto untrusted values does not prove provenance.

## Evidence semantics

- `Runtime` preserves canonical health/readiness from a trusted service-specific producer. This package does not define an Azure App Service/Cosmos/Durable Task health protocol and cannot establish that a producer's runtime claim is true.
- `Provisioning` always projects Unknown health/readiness, even if supplied statuses say Healthy/Ready. Resource creation or an endpoint is insufficient operational evidence.
- `CollectionFailed` projects Unknown, not Unhealthy; unavailable collection is not proof of resource failure.
- Stale and future evidence project Unknown with stable diagnostics and retain the original source timestamp. Exact age/tolerance boundaries are admitted.
- Missing evidence remains absent so the existing evaluator produces its missing-dependency result. Partial coverage cannot silently remove readiness edges, including dependencies such as Blob artifacts outside the initial named Azure services.

Normalize rejects wrong scope/realization/native association, duplicate bindings or evidence, unsupported kinds, malformed ARM IDs and provider diagnostics. It never copies arbitrary exception messages or provider payloads into returned diagnostics. Producer-provided source references are trusted non-secret provenance and must be sanitized before calling; this boundary is not a general-purpose secret scanner. Native ID comparison is deliberately exact, including casing, against the reviewed binding; it does not infer equivalence or rewrite Azure IDs.

The initial ARM binding subset is subscription/resource-group-scoped resources with explicit provider and type/name pairs. Tenant-level identities and extension-resource conventions are not inferred. Provider type and supported health protocol must be verified by the future native producer against the reviewed binding. This normalization slice is not proof of provider collection coverage.

## Usage and lifetime

For a capture, retain the expected/observed scope, reviewed bindings, original evidence, assessment time and freshness policy in the caller's redacted artifact. Pass immutable arrays to `Normalize`, then assess using the same realization. The returned canonical observations retain binding, ARM, scope, realization and handoff references; the caller retains the collection policy because the core observation has no policy field.

One validation/normalization call per inspection, not per dependency edge. Identity sets are built once per call; output is sorted by physical identity. There is no global cache, background work or backend fan-out. Multiple logical nodes referencing one physical resource share one observation. More than one evidence item for a physical resource must be reconciled by the native producer under an explicit service policy; last-writer-wins is not used here.

## Validation

```bash
dotnet test src/Cohesive.Adapters.Azure.Infra.Tests/Cohesive.Adapters.Azure.Infra.Tests.csproj -c Release
```

The tests reuse the real compiler/evaluator to prove dependency blocking, exact scope, freshness boundaries, missing/provisioning/failed evidence, canonical status preservation, native identity rejection, deterministic provenance and JSON round-trip revalidation. CI also runs the same tests through `eng/package-smoke/Cohesive.Adapters.Azure.Infra.Consumer` with package references only, after packing the solution. These synthetic fixtures make no claims about Azure wire payloads or live readiness.

## Dependency-ordered follow-up

COH-112 must next provide native collection and normalization for explicit service protocols with pinned source fixtures, request deadlines/bounds, cancellation and resource-scoped collection failures. Preserve provisioning/runtime separation and obtain supported Blob evidence or explicitly report it unknown. Publish and qualify the independently consumable package before ARI-536 adoption; Ari must not copy this source or add a parallel provider-state mapper. No release or live Azure qualification is performed by this slice.

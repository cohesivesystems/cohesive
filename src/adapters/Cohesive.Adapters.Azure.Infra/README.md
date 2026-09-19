# Cohesive.Adapters.Azure.Infra

This COH-112 slice admits exact, attributable Azure evidence into the existing Infra readiness evaluator and collects bounded read-only ARM management evidence. Service-specific runtime normalization and live qualification remain follow-ups before COH-112 is complete. No Ari-specific concepts or new external dependencies are introduced.

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

## Native collection

`AzureInfrastructureCollector.CollectAsync` validates the reviewed scope/bindings with the existing admission boundary before issuing requests. The caller owns a tenant-authenticated HttpClient for the public Azure ARM audience, with redirects disabled and cancellation-aware transport. The collector uses only `https://management.azure.com`, performs no authentication discovery, and never mutates client configuration. Caller credentials and handlers remain invocation-trust boundaries; a scope record alone cannot authenticate a tenant. Sovereign endpoints are outside this initial contract.

Supported GETs: App Service sites (2025-03-01), Cosmos database accounts/SQL databases (2025-10-15), and Durable Task schedulers/task hubs (2025-11-01). API versions and response identity/type fields are pinned against [documented contracts and synthetic fixtures](../../Cohesive.Adapters.Azure.Infra.Tests/Fixtures/README.md). Root ARM identity and resource type must match the exact requested association; native response IDs and equivalent reads use ARM case-insensitive identity comparison. Reviewed binding equality at normalization remains exact. Unsupported types, including Blob containers, get explicit Unknown collection-failure evidence without network calls.

The capture retains exact scope, start/end times, per-resource request-completion timestamps, allowlisted provisioning/site tokens and redacted per-resource diagnostics. Successful GETs always produce `Provisioning` evidence with Unknown health/readiness—even `Succeeded`, `Running`, an endpoint or a stopped site is not projected into a claim about application admission. The caller feeds capture evidence into `Normalize`, retaining capture diagnostics alongside the canonical assessment. There is no competing runtime health protocol or inferred Ready state.

Unique ARM reads are deduplicated case-insensitively within one capture using its shared caller identity and fixed GET API. Each canonical binding retains its own provenance. No data crosses captures. The explicit concurrency limit is 1–32, each scheduled read receives a positive timeout of at most ten minutes, and response bodies are capped at 256 KiB even without Content-Length. No automatic retries or list/fan-out queries occur. Reconciliation of multiple runtime sources is still a separate, explicit producer responsibility.

Access denial, missing resource, other HTTP failures, timeout, malformed/oversized responses, response identity mismatch and transport failure become stable collector-owned diagnostics; no headers, bodies or exception text is retained. Unsupported native state tokens are omitted. Other resources can complete after one fails. Caller cancellation propagates and returns no completed capture. Requests, responses and streams are disposed; the injected client is never disposed. The transport must honor cancellation and disable redirects; redirected responses are rejected defensively but that check does not replace safe client configuration.

Tests cover request count, concurrency bounds, timeout, cancellation during send/body reads, disposal, bounded response handling, native identity, partial failures and redaction. The same fixtures run in the package-only consumer. No live Azure qualification has occurred.

## Dependency-ordered follow-up

COH-112 must next define and normalize operational health/admission evidence for explicit service protocols; management collection alone cannot satisfy readiness. Preserve provisioning/runtime separation and obtain supported Blob evidence or explicitly report it unknown. Publish and qualify the independently consumable package before ARI-536 adoption; Ari must not copy this source or add a parallel provider-state mapper. No release or live Azure qualification is performed by this slice.

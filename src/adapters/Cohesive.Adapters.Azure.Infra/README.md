# Cohesive.Adapters.Azure.Infra

This COH-112 slice admits exact, attributable Azure evidence into the existing Infra readiness evaluator and collects bounded read-only ARM management and Resource Health evidence. Application admission normalization and live qualification remain follow-ups before COH-112 is complete. No Ari-specific concepts or new external dependencies are introduced.

## Authority and data flow

`InfrastructureRealization` owns resources and readiness edges. A trusted caller supplies the expected environment, tenant/subscription, exact realization reference and deployment handoff source reference. Expected `AzureInfrastructureObservationBinding` values associate canonical physical identities with full subscription-scoped ARM resource IDs and native-output provenance. Neither symbolic identities nor ambient credentials are used to invent ARM IDs.

A trusted producer supplies `AzureInfrastructureEvidence` and its observed scope. `Normalize` validates all associations and scope, enforces an explicit UTC assessment instant/maximum age/clock tolerance, and returns existing `InfrastructureResourceObservation` values. Feed these directly into `InfrastructureReadinessEvaluator.Assess(realization, observations)`. No second graph or health/readiness status enum exists.

The three new record boundaries describe distinct information: deployment scope, native association, and classified evidence. They support JSON round-trip but are not a separately versioned durable wire format. Construction alone is not admission: `Normalize` revalidates persisted/external inputs before use. Trust/authentication of an external artifact is the caller's responsibility; copying the expected scope onto untrusted values does not prove provenance.

## Evidence semantics

- `Runtime` preserves canonical health/readiness from a trusted service-specific producer. This package does not define an Azure App Service/Cosmos/Durable Task health protocol and cannot establish that a producer's runtime claim is true.
- `PlatformHealth` retains fresh platform health but always projects Unknown readiness with `platformHealthOnly`. Provider availability cannot attest to application admission; stale/future evidence still loses both health and readiness.
- `Provisioning` always projects Unknown health/readiness, even if supplied statuses say Healthy/Ready. Resource creation or an endpoint is insufficient operational evidence.
- `CollectionFailed` projects Unknown, not Unhealthy; unavailable collection is not proof of resource failure.
- Stale and future evidence project Unknown with stable diagnostics and retain the original source timestamp. Exact age/tolerance boundaries are admitted.
- Missing evidence remains absent so the existing evaluator produces its missing-dependency result. Partial coverage cannot silently remove readiness edges, including dependencies such as Blob artifacts outside the initial named Azure services.

Normalize rejects wrong scope/realization/native association, duplicate bindings or evidence, unsupported kinds, malformed ARM IDs and provider diagnostics. It never copies arbitrary exception messages or provider payloads into returned diagnostics. Producer-provided source references are trusted non-secret provenance and must be sanitized before calling; this boundary is not a general-purpose secret scanner. Native ID comparison is deliberately exact, including casing, against the reviewed binding; it does not infer equivalence or rewrite Azure IDs.

The initial ARM binding subset is subscription/resource-group-scoped resources with explicit provider and type/name pairs. Tenant-level identities and extension-resource conventions are not inferred. Native collectors verify response resource type and identity against the reviewed binding; application runtime producers must separately verify their protocol and authority. This normalization slice is not proof of provider collection coverage.

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

## Platform health collection

`CollectPlatformHealthAsync` uses the same validated, deduplicated, bounded GET pipeline as provisioning collection. It supports `Microsoft.Web/sites` and `Microsoft.DocumentDB/databaseAccounts` through the Resource Health `availabilityStatuses/current` extension resource (API 2025-05-01). Response identity must match the full requested extension resource, and response type must be `Microsoft.ResourceHealth/availabilityStatuses`. The [pinned contract and coverage sources](../../Cohesive.Adapters.Azure.Infra.Tests/Fixtures/README.md) define the evidence boundary.

| Native availability | Canonical health | Canonical readiness |
| --- | --- | --- |
| Available | Healthy | Unknown |
| Unavailable | Unhealthy | Unknown |
| Degraded | Degraded | Unknown |
| Unknown | Unknown | Unknown |

The native `reportedTime` is the observation time, never the request completion time or last state-change time. It must be an explicit UTC timestamp; missing/invalid timestamps or unrecognized/missing state tokens fail collection with stable diagnostics. Feed results through `Normalize` with the capture's explicit freshness policy before assessment. Stale health is not admitted merely because the GET just succeeded. Summaries, actions, incident details and arbitrary provider strings are discarded.

The new `PlatformHealth` authority is distinct from both provisioning and application runtime evidence. It reuses canonical health/readiness enums and the existing evaluator; there is no second graph or mutable status authority. The shared transport was extended rather than copied, preserving request limits, cancellation, redaction and response disposal for both operations. No external dependency was added.

Cosmos SQL databases, Durable Task schedulers/task hubs and Blob containers are explicitly unsupported by this platform collector. It does not replace a child binding with a parent account, infer another resource's health, list resource groups, or query application endpoints. An account-level Cosmos report does not prove database authorization; site platform health does not prove worker admission. Unsupported coverage produces Unknown without a request.

Keep provisioning and platform captures as separate evidence sources in the inspection artifact. They are different GETs and are never deduplicated together. Select one evidence source per physical resource for a given call to `Normalize`; passing conflicting or duplicate evidence still fails. Do not silently overwrite runtime failures with platform health. A multi-source reconciliation policy remains a separate caller responsibility.

## Dependency-ordered follow-up

COH-112 must next qualify and publish the package, retaining explicit coverage gaps. Application admission requires a reviewed runtime producer and product health contract; management collection alone cannot satisfy readiness. Preserve provisioning/runtime separation and obtain supported Blob evidence or explicitly report it unknown. Publish and qualify the independently consumable package before ARI-536 adoption; Ari must not copy this source or add a parallel provider-state mapper. No release or live Azure qualification is performed by this slice. Platform health collection does not complete COH-112 or establish a promotion gate.

## Declarative native binding and inspection

`AzureInfrastructureReadiness.Bind(plan, scope, nativeResourceIds, source)` projects caller-supplied canonical-node/native-ID associations into a deterministic `AzureInfrastructureReadinessBindings` artifact. The compiled deployment owns physical placement; the caller supplies actual native outputs and nonsecret provenance. Unknown nodes, contradictory physical associations, invalid IDs and wrong scope fail before collection. Omitted nodes remain missing; no native names are invented. The operation runs once at the native-output seam, without a global cache or provider reads.

The shared binding wire contract is `cohesive.azure-readiness-bindings/1`. Construction or JSON deserialization does not admit an artifact: call `Validate` against independently trusted scope before acquiring credentials. `InspectPlatformHealthAsync` repeats admission before any I/O, retains separate provisioning and platform captures, applies source-time freshness, and assesses the full realization. Its name explicitly selects platform evidence as the sole assessment input; provisioning is companion evidence. No runtime evidence is reconciled or overwritten. Cancellation returns no completed inspection; per-resource collection failures remain redacted non-ready results. Existing collector limits, ownership and disposal contracts apply unchanged.

`AzureInfrastructureReadinessInspection` owns the reusable capture/assessment contract. Applications supply declarations, native associations and explicit operating policy, and retain their operator/authentication/report-publication entry point. They do not duplicate the binding traversal or inspection coordinator. This extends the existing adapter without adding a Pulumi/Aspire dependency, a parallel graph, or a policy framework. Native libraries keep their native output semantics at the call site. The synthetic compiler-backed tests run both from source and through the package-only consumer.

## Incomplete native identity evidence

A matching ARM ID with an omitted root `type` is classified as `infra.azure.collection.missingResourceType`. It remains failed collection with unknown readiness and no retained native success/state tokens. An absent or conflicting ID, or a present null/non-string/conflicting type, remains `identityMismatch`. The collector does not infer response fields from the requested URL or substitute parent evidence.

This distinction makes incomplete provider responses actionable without relaxing admission. The pinned Task Hubs GET 2025-11-01 contract documents root ID/type; an omitted type is not silently repaired. A future provider-specific admission change requires explicit contract evidence and conformance coverage. Diagnostics retain no response body, add no retries or reads, and apply to provisioning and Resource Health through the same admission path.

## Exact runtime producer admission

`AzureInfrastructureRuntimeObservations.Normalize` extends the existing observation boundary with a trusted `AzureInfrastructureRuntimeContract` per physical resource and producer-reported `AzureInfrastructureRuntimeEvidence`. A contract pins the checked native binding, producer identity, versioned check contract and application deployment identity. The application deployment can differ from the infrastructure source revision: the caller must independently establish its association with the selected realization/handoff. Never construct expectations by copying the received payload.

A check contract defines the complete resource readiness claim it can establish. HTTP 200, aggregate Healthy, startup completion, platform availability and a successful parent check are not interchangeable with that claim. Declare a contract only when its reviewed semantics support the resource's readiness requirements; otherwise leave the resource uncovered. This slice supports one complete admission result per resource, not aggregation of partial checks or multi-source reconciliation. Producers own check execution and product semantics; this package contains no application-specific routes, names, JSON parsing or status mapping.

The pure operation compares producer/check/deployment/native attribution exactly, then delegates scope validation, diagnostics rejection, freshness, deterministic ordering and duplicate rejection to `AzureInfrastructureObservations.Normalize`. Runtime identity references are retained with canonical provenance. Missing results stay absent for the existing evaluator; stale/future results become Unknown without changing their source timestamps. Conflicting or malformed inputs throw sanitized argument exceptions instead of choosing a winner. Parent resources never fill child coverage.

This is attribution, not authentication or proof that a producer told the truth. The caller must authenticate the producer and authorize its contract before invoking this boundary. All references must be nonsecret. Construction and JSON round-trip do not admit evidence; the operation revalidates it. Raw diagnostic payloads are rejected, and callers remain responsible for sanitized source references.

The added layer is invocation-local, linear in contracts/results before the existing deterministic sort, with no I/O, retries, background work or cache. It traverses the realization once through the existing boundary. Two attributed records preserve distinctions absent from ordinary Azure evidence without introducing another status enum, dependency graph or evaluator. Existing trusted-runtime callers of the lower-level `Normalize` remain supported; this method is the stricter entry point for externally retained runtime attribution.

Tests use synthetic identities and the existing compiler-backed realization, including exact attribution, JSON revalidation, missing children, source time, failures and conflicting input. Shared tests are also linked into the package-only consumer. Publishing and Ari adoption follow review; authenticated collection and live runtime capability qualification remain outstanding. This change does not repair Task Hub provider responses or establish complete runtime readiness.

## Runtime declaration → collection → assessment

`AzureInfrastructureRuntimeBindings` is the independently reviewed `cohesive.azure-runtime-bindings/1` artifact. `Parse` rejects documents larger than four MiB, duplicate fields and unmapped fields. `Validate` fences its version, scope, unique explicit HTTPS endpoints and contracts against the exact native binding artifact. Declarations may cover a subset; omitted dependencies remain in the canonical realization. Supply them from trusted deployment policy, never by copying an observed response. This version supports one unique endpoint per physical-resource contract, not a batch endpoint requiring response fan-out.

`AzureInfrastructureRuntimeCollector` reads the exact `AzureInfrastructureRuntimeResponse` wire contract (`cohesive.azure-runtime-evidence/1`, default System.Text.Json property naming). Producers retain their original UTC source time, exact scope, contract and canonical result. Ordinary health JSON, missing/unknown versions, nested duplicate fields, wrong attribution and raw diagnostic payloads are not repaired or inferred. HTTP response source references are limited to the independently declared binding source, producer, check contract, application deployment and handoff, preventing arbitrary payload text from entering provenance. The caller authenticates and authorizes producers; typed attribution alone is not authentication.

The collector and native ARM collector reuse one internal bounded JSON transport. Each response is capped at 256 KiB even without Content-Length, response bodies and headers are disposed, redirects cannot supply admitted evidence, and limits are checked before I/O. Runtime URLs require HTTPS and reject user info, query and fragment. The caller must disable redirects and use a dedicated runtime client; ARM bearer credentials must never be attached to it. A producer's access requirements are not permission to grant access or weaken endpoint security.

Runtime concurrency is explicit (1–32), each scheduled read has a positive deadline of at most ten minutes, and each declared endpoint is requested once. There is no retry, cache or background polling. Per-resource HTTP/transport/timeout/contract failures produce sanitized diagnostics and absent observations. Caller cancellation throws without a completed partial inspection. Successful but stale/future observations become Unknown at the final assessment time, not at request start. Declaration validation and final canonical normalization each traverse the realization once; per-response admission does not traverse the graph.

`AzureInfrastructureReadiness.InspectRuntimeAsync` validates the runtime declarations before ARM or runtime I/O, retains provisioning/platform captures as companions, then uses runtime observations exclusively in the existing evaluator. Its report carries the optional Runtime capture and a top-level runtime assessment. It never overwrites runtime failure with Available platform evidence. `InspectPlatformHealthAsync` remains available with its existing platform-only semantics. These explicit methods avoid a caller-owned coordinator or implicit evidence-reconciliation policy.

The synthetic collector tests demonstrate serializable declarations/responses, full-graph assessment, incorrect deployment identities, missing children, legacy health rejection, failure redaction, deadlines/cancellation, bounded concurrency, and one request per endpoint. The same suite runs through the package-only consumer. No application-specific producer, authentication policy, live deployment association or completed task/Blob probe is invented by this API. Ari adoption can declare and select this path independently; actual producers and their complete check semantics must still be qualified before runtime readiness is claimed.

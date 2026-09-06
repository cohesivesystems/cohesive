# Cohesive.Infra internals

This document contains the capability, realization, evidence, and lifecycle architecture behind the
[package overview](README.md).

Portable infrastructure requirements, binding-first topology, capability-qualified physical realizations, and
lifecycle ownership for Cohesive systems.

`Cohesive.Infra` is the semantic boundary between application meaning and infrastructure lifecycle mechanisms. It
describes what an application requires, proves how a coherent target can satisfy those requirements, and records the
selected physical realization. Terraform, Pulumi, Aspire, cloud control planes, and local emulators are replaceable
interpretations of that realization; none is the source of infrastructure meaning.

## First-slice status

This package is the zero-third-party-dependency semantic core. The first slice establishes the portable definition and
realization model, stable identities, binding and lifecycle invariants, deterministic conventions, declarative target
facilities, coherent target variants, exactly fingerprinted binding elaboration, capability evidence, and fail-closed
compilation diagnostics.

It intentionally does **not** reference Terraform, Pulumi, Aspire, Azure, AWS, GCP, Kubernetes, or their SDKs. Actual
third-party emitters, CLI integrations, deployment runners, and observation importers are deferred to dedicated
adapter packages. This boundary keeps provider types and backend state out of canonical Infra IR.

## Authority boundary

Infra keeps four related authorities distinct:

| Artifact | Authority | Not authority for |
| --- | --- | --- |
| Application IR | Application behavior and the storage, execution, identity, API, and process guarantees it induces | Vendor resources or deployment mechanics |
| `InfrastructureDefinition` | Portable workloads, logical resources, capability requirements, directed contract bindings, readiness dependencies, and resource lifecycle intent | A selected provider topology |
| `InfrastructureTargetFacilityManifest` | A target adapter's selectable physical construction families and the exact capability evidence each family owns | Application topology, environment policy, or deployment execution |
| `InfrastructureTargetFacilityPlan` | One exact definition's facility selections, attributable effective configuration, and capability closure | Provider naming, emitted artifacts, backend state, or deployment receipts |
| `InfrastructureRealization` | One exact definition's capability closure, physical placements, lifecycle partition, demand-scoped evidence witnesses, and physically lowered readiness obligations | Current runtime readiness, backend execution state, or the original application semantics |
| `InfrastructureReadinessAssessment` | Deterministic readiness decisions from one exact realization and attributable observations | Observation collection, lifecycle mutation, or capability proof |
| Backend state and observations | Backend-native identities, receipts, outputs, drift, and time-indexed operational evidence | An implicit rewrite of the definition or realization |

The normal flow is:

```text
application definitions and explicit Infra authoring
    -> InfrastructureDefinitionDocument
    -> deterministic convention resolution
    -> facility selection from one exactly fingerprinted target manifest
    -> exact binding elaboration and attributable obligation derivation
    -> capability proof against one coherent target variant
    -> exact workload placements and demand-scoped physical evidence witnesses
    -> exact physical readiness obligations
    -> InfrastructureRealization
    -> lifecycle-backend projection
    -> backend state, receipts, outputs, and observations
    -> InfrastructureReadinessAssessment
```

Backends may be replaced, state may be imported, and a deployed estate may drift without changing canonical meaning.
Changing meaning requires an explicit accepted definition revision and a newly attributable realization.

## Definition and realization

An `InfrastructureDefinition` is the first provider-neutral requirement-system core. It contains stable identities for
workloads, logical resource roles, capability requirements, directed contract bindings, and resource lifecycle. The
local target extension adds lifecycle-bearing environment profiles, typed local endpoints and surfaces, and an exact
construction topology without changing this authority boundary. Requirements should normally reference or be
derived from their owning Cohesive definitions. Infra must not copy a Process retry contract, Storage durability
guarantee, or Identity authorization rule into a second independently maintained model.

An `InfrastructureRealization` joins an `InfrastructureCapabilityClosureReport` to an
`InfrastructureLifecyclePlan`, workload placements, and capability-evidence witnesses for the same exact fingerprinted
definition. Canonical readiness dependencies are lowered through those same placements into exact physical
`InfrastructureReadinessObligation` values; they are not re-authored by applications or adapters. The closure report
identifies the selected exactly fingerprinted profile, target, and coherent variant and
retains one evidence-backed, unavailable, or unknown planning decision for every declared node requirement and every
successfully elaborated binding obligation. Its exact `InfrastructureBindingElaborationReport` is both compiler state
and a machine-readable explanation path from binding to selected rule, induced requirements, capability decisions, or
residual diagnostics. The lifecycle plan associates logical resources with physical identities and one lifecycle
authority; workload placements do the corresponding job for executable nodes.

This is a realization candidate, not deployment authority. Target-profile evidence describes reusable planning
strategies. An adapter or other attributable interpreter must supply an
`InfrastructureCapabilityEvidenceWitness` for every selected transitive evidence identity and exact requirement, and
those witnesses must cover the physical workload and resource identities owned by the demand. Binding-derived demands
cover both endpoints. Auxiliary physical identities may also be retained for composed strategies. Missing, stale,
unexpected, unavailable, or incorrectly scoped evidence fails closed with structured diagnostics.

`InfrastructureTargetDeploymentManifest` is the portable declaration boundary between target facilities and exact
physical identities. It fences one definition and facility manifest, then attributes every logical workload and
resource to a target facility, physical identity, source evidence, and—for resources—lifecycle authority. A canonical
workload absent from a particular environment requires an explicit `InfrastructureWorkloadNonParticipation` decision
with rationale and sources. Placements plus those decisions form an exact workload partition; neither silent omission
nor placement/non-participation conflict is accepted. The manifest may also declare
`InfrastructureTargetBoundaryAcceptance` values: environment policy that accepts a named operating boundary with an
exact rationale and sources, without copying definition-local requirement identities. Its fluent builder is only a
producer of immutable, serializable, fingerprinted IR.

`InfrastructureTargetDeploymentCompiler` owns the corresponding computation. Exact deployment declarations become
explicit facility-selection policy; canonical resource lifecycle intent determines managed versus referenced
disposition; workload and resource declarations become placements and lifecycle bindings; and selected facility
evidence becomes demand-scoped physical witnesses. When a composed proof uses an auxiliary facility outside the
demand's logical endpoints, the compiler attributes that evidence to the declared physical resources for the owning
facility rather than falsely claiming that every proof component applies to every subject. Missing declarations,
unknown nodes, incompatible facilities, unavailable capabilities, and incomplete physical coverage remain structured
diagnostics. Applications and lifecycle stacks consume this result; they do not reimplement the assessment.

Target-boundary acceptance is compiled in two internal passes. The first facility plan establishes the selected proof
and its exact demand identities. The deployment compiler joins each selected proof boundary to the corresponding
target declaration, materializes a demand-scoped `InfrastructureBoundaryAcceptancePolicy`, and recompiles the final
facility plan with that policy. Only the final facility diagnostics are reported. Unknown declarations fail closed;
known declarations unused by any selected proof are warnings. The exact compiled policy is retained on
`InfrastructureTargetDeploymentPlan` for inspection and provenance. This orchestration belongs to Cohesive.Infra so
applications never traverse provisional capability decisions or synthesize acceptance records themselves.

Workload non-participation does not modify the canonical definition and does not narrow target capability closure.
Physical witness decisions omit demands owned solely by non-participating workloads, but resource demands and demands
owned by participating nodes remain exact. A binding or readiness dependency from a participating node to a
non-participating workload is an invalid physical participation boundary and produces a structured diagnostic.

Physical witnesses still are not construction recipes or execution receipts. Backend adapters must extend the exact
definition/profile/realization fence with compiler version, emitted-artifact fingerprint, preview, and backend receipts
before claiming deployment authority. A stale, partially matched, or merely capability-closed plan is not deployment
authority.

## Readiness obligations and observations

Readiness intent belongs to `InfrastructureDefinition`, separately from a binding contract or a capability proof. A
directed `InfrastructureReadinessDependency` means that its subject cannot be admitted as ready until the named
dependency is ready. The fluent `RequiresReady(...)` methods are typed authoring projections of that relation. The
definition normalizes dependency identities, rejects unknown nodes and duplicate semantic slots, and rejects cycles.

`InfrastructureRealizationCompiler` uses the existing workload placements and resource lifecycle bindings to lower
each logical dependency whose subject participates to one exact physical obligation. An explicit non-participation
decision suppresses obligations owned by that absent subject; it cannot suppress a dependency demanded by a
participating subject. The local compiler projects those obligations into its
construction topology, where existing Docker Compose and Aspire interpretations realize them using their own health
and wait mechanisms. A local-only ready dependency absent from canonical intent is a structured diagnostic, preventing
an AppHost or Compose file from becoming a second application topology.

Current state remains separate, attributable evidence. Adapters produce `InfrastructureResourceObservation` values
using the shared execution health and readiness statuses, exact physical identities, UTC observation time, typed
source references, and optional adapter diagnostics. `InfrastructureReadinessEvaluator` performs no I/O: it derives
one effective node decision, propagates not-ready and unknown dependency state, and emits normalized diagnostics with
the exact semantic dependency, physical resources, expected and observed states, sources, and repair options. Missing
evidence is unknown and fails closed. An incomplete capability or physical realization is diagnosed independently and
cannot become ready merely because a runtime endpoint is exposed or a provider reports favorable health.

The assessment is fingerprinted for deterministic serialization and comparison, but it is time-indexed evidence—not
new infrastructure intent. Live Aspire, Pulumi, Terraform, or provider observation collection remains adapter work;
future `cohesive status` tooling can consume the common assessment without reimplementing readiness meaning.

## Bindings are primary semantics

Infra is binding-first. A resource declaration says that a role exists; a binding says how a consumer may rely on a
provider and what that path must preserve.

`InfrastructureBindingDefinition` durably identifies source, target, and provider-neutral contract. An exactly
fingerprinted `InfrastructureBindingElaborationProfile` contains versioned, attributable rules mapping exact contracts
to capability and assurance obligations. Zero matching rules is unavailable; several matching rules are ambiguous;
neither state is guessed through. One selected rule produces stable binding-derived requirement identities and an
exactly fingerprinted report. A workload-to-database, worker-to-scheduler, workload-to-secret, service-to-service, or
DNS-to-hostname binding is therefore the authority from which compilers derive and prove:

Elaboration rules are pure mappings from exact versioned contract identities. If two semantic contract variants induce
different obligations, they require distinct contract identities; rules do not inspect ambient services or retain
host-language callbacks.

- service discovery, endpoint selection, and configuration injection;
- identity selection and least-privilege grants;
- network policy, firewall rules, private endpoints, and egress;
- secret transport, sensitivity, rotation, and export policy;
- dependency, readiness, replacement, drain, and cutover order; and
- traces, metrics, health checks, dashboards, and diagnostics.

These projections must come from the same binding. Creating a vault does not prove a workload can retrieve a required
secret. Creating a scheduler does not prove authenticated client and worker access. Placing a database beside a broker
does not prove atomic commit-to-publication.

## Coherent target variants

A capability profile may expose several complete realization variants, such as `local-aspire`, `azure-terraform`, or
`azure-pulumi`. Compilation selects evidence from one coherent configured variant for a realization fragment. It must
not cherry-pick individually convenient claims from mutually incompatible variants and synthesize a target that does
not exist.

Variants are useful when the same provider family has materially different configurations, lifecycle backends,
guarantees, emulator behavior, or operating envelopes. In the first slice, a variant is more than a label: its direct
evidence, composition rules, and named operating boundaries form one internally consistent alternative. The report
retains an exact profile schema, identity, and SHA-256 fingerprint; available evidence also requires attributable
source references. Backend adapters must add their own provider-version and artifact fingerprints.

## Declarative target facilities

`InfrastructureTargetFacilityManifest` is the provider-neutral boundary between capability evidence and a target's
physical construction choices. It owns one complete capability profile and groups exact native or constrained leaf
evidence into stable workload or resource facilities such as an application service, container runtime, object store,
or database family. Composition remains in `InfrastructureCapabilityRule`; a facility does not create a second
capability vocabulary or duplicate evidence records.

`InfrastructureTargetCompiler` is the shared planning mechanism. For each logical workload and resource it finds the
facilities whose owned leaf evidence and capability rules can prove every requirement declared directly on that node.
One candidate is selected automatically; zero candidates and unresolved alternatives produce structured diagnostics.
The standard `target/facility` setting, scoped to the exact logical-node identity, lets the normal convention
precedence choose among semantically compatible facilities while preserving the selected value's origin and authority.

After facility selection, the compiler restricts capability resolution to the evidence owned by selected facilities,
plus unowned target-wide evidence and required auxiliary evidence. It then delegates binding elaboration, capability
composition, constrained-boundary acceptance, and residual diagnostics to `InfrastructureCapabilityCompiler`. The
closure remains fenced to the manifest's exact capability profile; the facility decisions in the outer plan retain
the evidence-selection policy. This avoids synthetic profiles and lets an acceptance policy be authored against the
stable target manifest rather than against a compiler-generated identity.

Bindings may induce end-to-end capabilities that no single facility directly supplies. Those obligations are proved
after physical families are selected because their rules may compose evidence across the selected workload and
resource facilities. A target-specific projection may translate successful decisions into Azure, AWS, GCP,
Kubernetes, or local resource descriptions and names, but it does not reimplement selection, capability closure,
convention precedence, diagnostics, or plan identity. Provider emission and execution remain adapter concerns.

## Capability proof rules

Requirements and supplied capabilities use the same requirement-shaped vocabulary. A provider manifest must not
introduce a parallel feature enum whose cases can drift from the semantic requirements it claims to satisfy.

Each requirement receives exactly one attributable disposition:

- **Native** — one facility directly preserves the capability; it has no auxiliary proof or constrained boundary.
- **Composed** — cited facilities or protocols jointly preserve it; every intermediate witness remains inspectable.
- **Constrained** — support is claimed only inside explicit operating boundaries and remains residual until exact
  environment policy accepts those boundaries.
- **Override** — an explicit demand-scoped decision may eventually accept a residual; overrides are not target
  evidence, and the first-slice compiler does not yet accept them.
- **Unavailable** — no supplied, policy-permitted construction preserves the requirement.
- **Unknown** — the evidence or classification is incomplete; unknown must not be normalized to false or unavailable.

Composition is governed by versioned proof rules. All prerequisites of a rule are an AND; alternative rules are OR
branches. Recursive resolution constructs a canonical, cycle-free proof and retains auxiliary evidence and
intermediate evidence. Equally preferred valid proofs are ambiguous unless explicit policy selects one. A capability
profile supplies evidence; conventions and compiler configuration supply policy.

`InfrastructureCapabilityCompiler` resolves every declared node requirement and successfully elaborated binding
obligation in one exact document against one coherent variant. It retains the exact elaboration-profile and report
fingerprints beside the target-profile fence. A constrained proof closes only when an
`InfrastructureBoundaryAcceptancePolicy` accepts every transitive boundary for the exact requirement. The policy is
separate governance authority, fingerprinted and fenced to the exact definition, capability profile, binding profile,
target, and variant. Acceptance for one demand cannot leak to another demand with the same capability. Each decision
retains accepted and missing boundary sets, and the closure retains the exact policy reference.

Unaccepted constrained proofs and unavailable or ambiguous contracts remain structured residuals, so full closure
must cover the whole binding path, not only one resource. A durable Process requirement may depend on a worker,
scheduler, identity, secret, network path, state store, and lifecycle backend. Local support at one edge does not
establish end-to-end assurance.

## Explainable conventions

Conventions make common definitions concise while remaining deterministic compiler policy. Effective values follow
the repository-wide precedence order:

1. explicit local declarations and overrides;
2. scoped application, subsystem, or environment profiles;
3. adapter and compiler conventions; and
4. framework-wide defaults.

`InfrastructureConventionResolver` currently retains the stable subject and setting, canonical value, suite-wide
origin, and supplying authority. Equally authoritative conflicting values produce a structured ambiguity diagnostic.
`InfrastructureTargetCompiler` resolves supplied convention profiles as part of facility planning;
`InfrastructureCapabilityCompiler` remains independently usable and does not resolve ambient configuration. Later
explain artifacts can add input fingerprints, alternatives, reasons, and relevant capability evidence without making
conventions semantic authority. Conventions may select among semantically valid alternatives, derive stable names and
tags, choose a local emulator, attach standard telemetry, or add an attributable auxiliary resource. They may not
invent application requirements, hide ambiguity, or weaken a guarantee.

## Diagnostics are part of the programming model

Capability mismatches, ambiguous proofs, unaccepted operating boundaries, binding gaps, convention conflicts, and
lifecycle conflicts are expected compiler outcomes, not exceptional control flow or log strings. Every validation,
compilation, preview, execution, and reconciliation boundary should return its result together with one normalized
diagnostic set. Exceptions remain appropriate for malformed API inputs, violated object invariants, and failed tool
transport; an unsupported target is a diagnostic.

Infra uses the suite-wide `DocumentValidationDiagnostic` and `DocumentDiagnosticEvidence` contracts rather than
creating a parallel diagnostic family. A diagnostic can retain a stable code and severity, canonical document and
semantic locations, compiler stage, stable subject, related locations, exact source references, expected and observed
semantics, and resolution options. Capability diagnostics should identify the demanded capability, exact definition,
target-profile, binding-profile, and boundary-policy fingerprints, coherent variant, evidence or rules considered,
rejected alternatives and causes, accepted and missing operating boundaries, and remaining obligation. Backend-native
identifiers and evidence must be retained when Terraform, Pulumi, Aspire, or a provider diagnostic is normalized.

Diagnostic collections are deterministically ordered and serializable. Human CLI text, JSON, SARIF, editor/LSP
messages, CI annotations, test assertions, dashboards, and agent explanations are projections of the same diagnostic
artifacts; consumers must not parse rendered messages to recover semantics. A diagnostic may offer repair choices but
does not mutate the definition, accept a boundary, choose a proof, or apply infrastructure. Residual obligations remain
first-class compiler state and are not replaced by their diagnostic projections.

Fluent builders are authoring projections rather than semantic authority. Given the same inputs, referenced contracts,
producer/compiler version, conventions, and explicit configuration, fluent and direct-IR construction must normalize
to the same immutable definition. No closure, callback, reflection object, service provider, clock, environment
variable, or arbitrary host executable dependency may survive in canonical IR.

## Exact local construction realization

`InfrastructureLocalRealizationDocument` is the shared input boundary for local lifecycle adapters. It fences a
target-neutral local construction topology to one exact `InfrastructureRealizationReference`, a versioned environment
policy, and the existing four-tier `InfrastructureConventionResolution`. The topology describes executable services
constructed from either pinned container images or repository-relative .NET projects, endpoint exposure and UI roles,
external secret references, generated non-secret configuration files,
volumes, physically projected readiness dependencies, complete health policies with probe timing, graceful termination, and application-owned
harness operations.

Interactive and isolated-test environments are explicit profiles. Interactive environments retain managed data across
ordinary stops. Isolated-test environments make disposable data and any maximum lifetime visible to adapters; a
scoped-profile or explicit project name prevents accidental namespace sharing, and a destructive operation must
additionally name the exact lifecycle authority it may mutate. Configuration values remain
attributable to explicit declarations, scoped profiles, adapter conventions, or framework defaults. Secret payloads
are never effective configuration values in the local IR.

The local compiler validates the construction topology before an adapter performs I/O: resource services must match
managed lifecycle bindings, workload services must match exact workload placements, repository projects may attach
only to workloads, images must be pinned, referenced settings/endpoints/volumes/files/services must exist, loopback
ports must be valid and unique, readiness must agree with canonical physically lowered obligations and be backed by dependency health, likely secret environment
values must be external references, and destructive operations must remain inside the selected lifecycle authority.
Capability mismatches are portable `DocumentValidationDiagnostic` values with exact source references, expected and
observed states, related locations, and actionable resolution paths. A successful local document is still not an
executable deployment plan or receipt; Compose and Aspire compilers must fingerprint their own artifacts against this
document.

The freight materialization harness owns the first canonical fixture in
`eng/materialization-harness/model/FreightMaterializationInfrastructure.cs`. It declares PostgreSQL, Cosmos and its Data
Explorer, Elasticsearch, pgAdmin, Kibana, health/readiness, persistent volumes, the interactive and isolated-test
profiles, and the `start`, `stop`, `reset`, `status`, `seed`, `materialize`, `verify`, and `inspect` operation intents.
`Cohesive.Adapters.DockerCompose` emits the checked-in default Compose artifact and exact provenance manifest from this
fixture. Runtime profiles preserve explicit worktree and `.env` configuration without admitting secret values into
effective configuration. The Aspire AppHost consumes the same fixture and realization rather than maintaining a
parallel service graph. Both interpreters emit `InfrastructureLocalTargetDecision` evidence under target-neutral
concern identities, and the harness differential conformance fixture compares their common semantics and requires every
remaining target difference to be explicit. There is no independently authored Compose topology.

## First-slice fluent authoring and realization

The package API below is concrete; the small `AriCapabilities`, `AriContracts`, `AzureCapabilities`, and `AzurePlan`
catalogs/projections are conceptual application or adapter authorities. Application-owned capabilities should normally
be referenced from their owning IR rather than copied into Infra by hand. A real adapter would derive `AzurePlan` from
its attributable, versioned plan rather than maintain it manually.

```csharp
using Cohesive.Infra;
using Cohesive.Infra.Realization;

InfrastructureNodeId domainStateId = new("resource/domain-state");
InfrastructureNodeId artifactsId = new("resource/training-artifacts");
InfrastructureNodeId schedulerId = new("resource/process-scheduler");

var document = Infrastructure.Define(
    id: new("ari-training"),
    revision: new("1"),
    configure: infra =>
    {
        var api = infra.Workload(new("workload/training-api"));
        var jobs = infra.Workload(new("workload/training-jobs"));

        var domainState = infra.Resource(domainStateId)
            .Persistent()
            .Requires(AriCapabilities.DocumentAuthority)
            .Requires(AriCapabilities.ChangeFeed);

        var artifacts = infra.Resource(artifactsId)
            .Persistent()
            .Requires(AriCapabilities.ObjectStorage);

        var scheduler = infra.Resource(schedulerId)
            .Persistent()
            .Requires(AriCapabilities.DurableScheduling)
            .Requires(AriCapabilities.AuthenticatedClientAndWorkerAccess);

        infra.Bind(new("binding/api-domain-state"), api)
            .To(domainState).As(AriContracts.RepositoryReadWrite);
        infra.Bind(new("binding/api-scheduler"), api)
            .To(scheduler).As(AriContracts.ProcessClient);
        infra.Bind(new("binding/jobs-scheduler"), jobs)
            .To(scheduler).As(AriContracts.ProcessClientAndWorker);
        infra.Bind(new("binding/jobs-artifacts"), jobs)
            .To(artifacts).As(AriContracts.ObjectReadWrite);
    });

var closure = InfrastructureCapabilityCompiler.Compile(
    document,
    AzureCapabilities.Profile,
    AzureCapabilities.TerraformProductionVariant,
    AriBindings.ElaborationProfile);

var lifecycle = new InfrastructureLifecyclePlan(
    document,
    [
        new(domainStateId, new("cosmos/ari"), new("terraform"), new("state/ari-prod"),
            InfrastructureLifecycleDisposition.Managed),
        new(artifactsId, new("blob/ari"), new("terraform"), new("state/ari-prod"),
            InfrastructureLifecycleDisposition.Managed),
        new(schedulerId, new("durable-task/ari"), new("terraform"), new("state/ari-prod"),
            InfrastructureLifecycleDisposition.Managed)
    ]);

var realization = InfrastructureRealizationCompiler.Compile(
    closure,
    lifecycle,
    AzurePlan.WorkloadPlacements,
    AzurePlan.CapabilityWitnesses);

foreach (var diagnostic in realization.CapabilityClosure.Diagnostics.Concat(realization.Diagnostics))
    Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
```

`Infrastructure.Define` returns an immutable, normalized, fingerprinted `InfrastructureDefinitionDocument`; the
authoring callback is synchronous and is not retained. `InfrastructureCapabilityCompiler` decides whether the one
selected variant can plan every declared node requirement and binding-derived obligation. An unavailable scheduler,
ambiguous proof, unaccepted boundary, or unavailable or ambiguous binding contract yields a non-closed report.
`InfrastructureBoundaryAcceptancePolicy.Create` provides the explicit path for a constrained proof: every acceptance
names the exact requirement and boundary, includes a human-reviewable rationale and non-empty policy sources, and is
canonicalized into an exact policy fingerprint. Missing, stale, or proof-inapplicable acceptances remain diagnostics;
policy fence mismatches fail closed with exact expected/observed compiler-authority diagnostics and do not contribute
acceptance.
`InfrastructureBindingElaborationReport.FindDecision` exposes selected rules and stable obligation identities to
tooling. `InfrastructureLifecyclePlan` rejects a missing, duplicated, external, or conflicting manager. The resulting
`InfrastructureRealization.FindWitnessDecision` explains the exact subjects, selected evidence, physical coverage, and
residual gaps for one requirement. `IsCapabilityWitnessComplete` reports only that closure and physical applicability
are complete; it is deliberately not a deployment-readiness flag. Artifact fingerprints, previews, and backend
receipts remain required for that later authority.

## Lifecycle ownership

One lifecycle backend must be authoritative for each managed physical resource; referencing a resource never confers
management. `InfrastructureLifecyclePlan` currently requires every logical resource to have at least one lifecycle
binding, requires all participants for that logical resource to agree on its physical identity and authority, and
rejects more than one management pair for the same physical identity. Later adapters may consume typed outputs or
observe the resource, but two backends may not concurrently manage it.

Shared and externally managed resources use the `External` resource lifecycle and `Referenced` lifecycle disposition.
The first slice rejects any manager for an external resource and requires exactly one manager for every other resource.
Later adapters must add deletion protection and ensure cross-backend values retain type, sensitivity, producer,
consumer, and revision affinity. Destroying an application environment must not destroy a referenced shared registry,
network, identity, or data store.

The broader model also requires lifecycle-backend capability evidence. Being able to render a resource is not proof
that a backend can safely preview, create, update, replace, delete, import, refresh, detect drift, preserve secrets,
order dependencies, roll back, or return sufficient receipts. This first slice validates ownership identities and
dispositions; backend-operation capability manifests belong with the deferred adapters.

## ARI acceptance case

ARI is the bounded first acceptance case because it already projects one typed, Azure-shaped topology through Pulumi,
Aspire, runtime setup, and tests. The goal is to lift that authority into portable Infra semantics, not create another
catalog beside it.

The first proof should establish:

1. One definition owns the Cosmos database, containers, `/partitionKey`, required composite indexes, and stable
   runtime bindings. Managed Azure and local-emulator realizations preserve those identities while emulator image,
   ports, and platform constraints remain target evidence or extensions.
2. Training API and Jobs bindings derive repository, durable inbox, Blob artifact, scheduler, telemetry, secret,
   readiness, identity, network, and least-privilege requirements. API receives scheduler-client access; Jobs receives
   client and worker access.
3. The Azure production candidate fails before emission when no remote Durable Task scheduler, task hub, authenticated
   client path, and worker path discharge its Process requirements. Falling back to an in-memory scheduler is not a
   valid production convention.
4. A Key Vault resource alone does not close the GitHub App contract. The private-key secret, sensitivity and export
   policy, workload grant, and value path must all be bound and proved.
5. `local-emulator`, run-scoped test, Azure development, and Azure production variants remain distinct and
   explainable. Partial emulator availability fails closed.
6. A shared Azure ML registry is either managed by one lifecycle authority or referenced externally by an environment,
   never both. Environment teardown cannot delete the shared resource.

This case is intentionally narrow. It proves semantic authority, binding closure, target coherence, diagnostics, and
lifecycle ownership before the package grows a general resource ontology.

## Tooling and orchestration

The eventual `cohesive` CLI should be a thin client over a tooling-neutral Infra operation protocol, not deployment
authority. The same protocol should support CLI, IDE, CI, test, API, and agent clients with cancellation, structured
progress, normalized diagnostics, explain artifacts, backend receipts, and stable outcome/exit classifications.
`Cohesive.Cli` supplies command composition, configuration, middleware, output routing, and a test harness, while
`Cohesive.Host.Cli` adds host lifecycle integration. An Infra CLI should reuse those layers while keeping canonical
document serialization under Infra's strict serializer.

A useful command surface is:

- `cohesive validate` — materialize the exact definition and report structural, convention, binding, capability, and
  policy diagnostics without projecting or changing infrastructure;
- `cohesive explain` — trace one requirement, decision, diagnostic, resource, binding, or convention value through its
  causal and provenance graph;
- `cohesive plan` — compile an exact realization and ask each lifecycle authority for a non-mutating preview;
- `cohesive up` — after successful validation and an accepted exact preview, delegate local run or remote apply to the
  selected lifecycle authorities and ingest their receipts;
- `cohesive down` — preview and execute stop or destroy according to ownership, deletion protection, and environment
  policy; and
- `cohesive status` — refresh and reconcile exact realization, backend state, receipts, health, endpoints, drift, and
  observations without silently rewriting semantic intent.

`up` is convenient syntax, not one universal backend operation. For a local profile it may mean `RunLocal`; for a
remote environment it means an explicit, policy-gated `Apply` against named lifecycle authorities. Local run, observe,
stop, artifact publication, preview, apply, refresh, and destroy remain distinct operation intents. Remote mutation
must require an explicit environment/target and cannot proceed while error diagnostics or unaccepted residual
obligations remain.

The Aspire-backed local path derives an AppHost projection, then delegates project/container lifecycle,
networking, readiness, logs, telemetry, and dashboard behavior to Aspire and DCP. Cohesive bindings project to Aspire
references; readiness obligations project separately to wait/health relationships; lifecycle ownership projects
separately again. The integration should consume Aspire's machine-readable run and observation surfaces rather than
reimplementing its orchestrator or dashboard. Aspire AppHost code, manifests, DCP resources, and dashboard data remain
derived artifacts or observations.

Terraform and Pulumi follow the same ownership rule with different mechanisms. Terraform execution should delegate a
saved, inspectable plan to the Terraform CLI and consume versioned JSON event/show/output surfaces without touching raw
state. Pulumi execution should use Automation API and preserve engine events, URNs, outputs, and update results as
receipts and observations. Backend versions and supported operations belong in lifecycle capability manifests so
preview or evolving backend commands cannot be assumed merely because a tool is installed.

## Lifecycle adapters

Integrations remain separate `Cohesive.Adapters.*` packages and are never referenced or transitively required by this
semantic core. Terraform and Pulumi remain planned; the first local Aspire and Docker Compose interpretations exist.

### Terraform

A later Terraform adapter will lower one exact `InfrastructureRealization` to deterministic `.tf.json` and versioned
module calls. Terraform plan/apply runs outside the semantic core. The adapter will ingest versioned
`terraform show -json` output as diagnostics and observations, retain Infra identity alongside Terraform addresses,
and never parse or patch raw `tfstate`. Terraform owns its execution state; it does not own the definition.

### Pulumi

A later Pulumi .NET adapter will project semantic composites to `ComponentResource` instances and selected physical
leaves to provider resources. Infra identities remain associated with Pulumi URNs, and typed realization outputs map
to Pulumi Inputs/Outputs without becoming canonical meaning. Automation API preview, up, refresh, and destroy belong
in the adapter and may run only after capability and lifecycle validation. No Pulumi dependency belongs in this core
package.

### Aspire

A current Aspire hosting integration projects exact local realizations to container and repository-project resources,
endpoints, parameters, waits, health checks, and operation commands. Repository-project sources are retained in the
canonical local IR and fenced to exact workload placements; Docker Compose fails closed on them because it cannot
preserve that construction choice. `WithReference`-style discovery and configuration should continue to be derived
from canonical Infra bindings. Aspire AppHost and DCP are local/development orchestration, not production semantic
authority, and an emulator projection must state its differences from production. No Aspire dependency belongs in
this core package. A session adapter should prefer Aspire's machine-readable run and
observation surfaces—currently `aspire run --detach --format Json`, `aspire describe --follow --format Json`,
`aspire wait`, and `aspire stop`—and use isolated sessions for parallel worktrees and tests. Aspire command and
manifest support must be pinned and declared as backend capability because publication and deployment surfaces may
have different stability from local run/observe/stop surfaces.

## Non-goals of the first slice

The first slice does not attempt to provide:

- a universal cloud-resource ontology;
- automatic inference of every application requirement;
- production deployment or provider authentication;
- a generic patch, migration, or drift-reconciliation algebra;
- implicit equivalence between local emulators and managed services;
- concurrent ownership of one resource by several lifecycle backends; or
- a lowest-common-denominator facade over Terraform, Pulumi, Aspire, or cloud providers.

Direct provider access remains legitimate when Infra does not model a needed capability. It must be explicit, local,
versioned, attributable, and visible as an extension or residual obligation rather than become a hidden second model.

## Related documentation

- [Cohesive vision](../../docs/vision/cohesive-vision.md)
- [Cohesive semantic model](../../docs/concepts/semantic-model.md)
- [Code quality and optimization model](../../docs/quality/code-quality.md)
- [Conformance](../../docs/quality/conformance.md)

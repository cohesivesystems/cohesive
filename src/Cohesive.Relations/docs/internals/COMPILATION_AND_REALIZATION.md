# Cohesive.Relations internals: compilation and realization

## Architectural Principles

### Semantics before infrastructure

Canonical nodes describe relational meaning. Table names, partition keys, SQL dialects, batch sizes, SDK types, and connection details belong to compiler configuration and adapters.

### Source acquisition is an interpretation

A logical source does not require a database scan. Values may be supplied directly, loaded from storage, produced by another relation, or resolved through a composed execution plan.

### Capability-driven compilation

Adapters describe what their targets can do. Compilers compare semantic requirements with those capabilities and produce native, composed, constrained, overridden, or unavailable realizations.

Compilers must not silently weaken semantics.

### Demand-driven field selection

Interpreters should derive and acquire only the fields required by requested outputs, predicates, joins, ordering, aggregation, invariants, and diagnostics.

### Explicit expression sites

Every expression in canonical relation/query IR is analyzed at its semantic site. Filters see
their input bindings, joins see the combined left and right environments before outer-join absence
is applied, projections and aggregates see their input environments, and relation output keys and
invariants see the shaped output environment. Keyset continuation expressions see query
parameters but no row bindings or row-dependent ambient capabilities.

The shared `Cohesive.Model` expression analyzer derives requirements from these scopes. Relations
adds topology-derived binding shape and availability, target-field expectations, and stable site
identity; it does not maintain a separate expression language. This analysis is a compiler-front
end rather than an execution plan. SQL, document, graph, search, and in-memory compilers can match
the resulting requirements against their own capability profiles and retain diagnostics and
provenance to the originating site.

Two-argument `Any` gives structured collections portable existential semantics. Its predicate is
evaluated once per element with `item` as the current-element field root, and every field read in one
evaluation refers to that same element:

```csharp
Expr pickupInSeattle = Expr.Any(
    Expr.Field(load, FieldPath.FromField("Stops")),
    Expr.And(
        Expr.Eq(Expr.Field("item.Location"), Expr.Param(location.Value)),
        Expr.Eq(Expr.Field("item.Type"), Expr.Const("Pickup"))));
```

The expression is true when at least one element satisfies the complete predicate. It is false for an
empty collection. A missing, null, or non-collection operand and a non-Boolean predicate result are invalid
operands rather than alternate meanings for false. This differs from `Contains`, which tests membership in
a scalar collection and has no structured-element correlation requirement.

Static analysis resolves direct current-element fields from inline structural types and named structural
collection element types. It projects the outer collection input, current-item reads, and the portable
`CollectionElementCorrelation` guarantee with provenance to the originating expression site. The reference
interpreter realizes this direct-field closure over structured runtime values. Elasticsearch realizes it only
when binding evidence proves a `nested` mapping and same-document correlation; a flattened object array is not
semantically equivalent. Deeper current-element paths and nested collection scopes remain deferred.

Supplied shape-graph snapshots are retained exactly for provenance. Snapshots with semantic
errors are diagnosed and quarantined from scope and target resolution, so invalid schema data
cannot silently influence inferred contracts.

This front-end intentionally stops short of full cross-expression type inference. Exact selector-to-
aggregate-result correlation, keyset-boundary-to-order-key correlation, arbitrary deep graph resolution for
named structural source paths, and a common-domain/coercion model for mixed comparison operands belong to the
subsequent inference layer. Current analysis reports what it can prove and does not silently invent conversion
semantics for those cases.

### Demand-driven static compilation

`RelationQueryStaticCompiler` turns a persisted relation or query into a deterministic,
target-independent plan. A compilation request supplies the exact `relation-query/v1` document,
the shape-graph snapshots used to interpret its fields, an optional relationship-catalog snapshot,
and an output demand. Omitting the demand applies the all-declared-output convention: required
declared fields must have producers, while optional unassigned fields are treated as intentionally
absent. The plan records whether this demand was convention-derived or explicit. Callers can
instead make a strict request for selected relation fields or selected fields of named query results:

```csharp
using Cohesive.Model;
using Cohesive.Relations.Compilation;

var demand = RelationQueryCompilationDemand.ForRelationFields(
[
    new(outputShape, FieldPath.FromField("CustomerName"))
]);

var result = RelationQueryStaticCompiler.Compile(
    new(
        relationDocument,
        [loadShapes, customerShapes, searchDtoShapes],
        relationshipCatalogDocument,
        demand));

if (!result.IsSuccessful)
    throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

var plan = result.Plan!;
```

The plan exposes several immutable views of one canonical requirement graph:

- `InputContract` describes the source sets, selected fields, observation identities,
  relationship traversals, runtime parameters, expression capabilities that must be supplied,
  and target temporal capabilities that must be preserved.
- `Lineage` has one entry per demanded output. Its `Contributions` contain only value-, identity-,
  and aggregate-producing provenance, while `Influences` retain non-value effects such as
  membership, cardinality, ordering, and validation without misclassifying them as output values.
- `DependencyManifest` includes every semantic influence, including membership, correlation,
  acquisition, cardinality, ordering, grouping, aggregation, pagination, validation, and
  evaluation capabilities. It is the appropriate input to downstream impact analysis and index-synchronization planning.
- `LogicalPlan` retains the demanded canonical nodes and their dependency-first evaluation order.
  A safely pruned optional, at-most-one left traversal is represented by typed bypass evidence;
  pruning is never an invisible graph rewrite.
- `Provenance` retains the exact definition, shape, and catalog documents, their available
  fingerprints, and the static compiler profile that produced the plan.

Every requirement edge carries one or more ordered traces from the demanded output through the
typed expression sites, aggregate operations, and logical nodes that caused the requirement.
Backend compilers should consume this plan as the semantic input to physical planning, match its
requirements against their declared capabilities, and preserve those traces in target artifacts
and diagnostics. Static compilation does not select a database, source placement, batching policy,
join algorithm, or runtime missing-data behavior.

### Capability-driven realization reports

`RelationQueryRealizationCompiler` projects the exact demanded semantics from the compiled
execution slice and input contract. It does not rescan the complete definition, so pruned nodes,
assignments, sites, and terminals cannot reappear. Each projected requirement identifies its
logical, expression, temporal, structural, or guarantee capability together with its originating
input, binding, node, semantic site, field or expression path, and demanded-output traces.

A target supplies a versioned `RelationQueryTargetCapabilityProfile`; compiler configuration
supplies an explicit `RelationQueryRealizationPolicy`:

```csharp
var report = RelationQueryRealizationCompiler.Compile(plan, targetProfile, policy);

if (!report.IsRealizable)
{
    foreach (var diagnostic in report.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
}
```

The matcher produces exactly one final decision per demanded requirement:

- **Native** uses direct target capability evidence.
- **Composed** proves exact support through versioned composition rules and names every primitive
  capability assertion in the proof closure. The root rule must explicitly preserve every guarantee
  attached to the demanded operation; an unrelated target-wide guarantee cannot repair a lossy composition.
- **Constrained** is exact only inside declared operating boundaries. A boundary narrows where a
  realization is valid; it does not permit weaker semantics inside that boundary. Every boundary is
  accompanied either by an immutable measured plan fact (such as page size, expression depth, or field-path
  depth) or by target evidence that enforces that exact boundary at execution.
- **Override** uses an explicit local implementation with attributable evidence, boundaries,
  preserved guarantees, and justification.
- **Unavailable** prevents target execution and identifies the missing capabilities and exact
  semantic site.

Five plan-wide fidelity guarantees are unconditional: missing/null distinction, availability-state
distinction, determinism, evidence completeness, and inconclusive-evidence propagation. They are attached
to every non-guarantee requirement. Native proofs must carry evidence for them, and a composed rule must
explicitly preserve them; a separate global assertion cannot mask a locally lossy operator.

Contributing-occurrence lineage is an explicit result-observability requirement rather than an unconditional
value-semantics guarantee. The compatibility overload uses
`RelationQueryResultObservability.ExactContributors`, which requires every result row to retain its complete
contributor set and relation root. A value-oriented target can instead compile with
`RelationQueryResultObservability.NotRequested`:

```csharp
var report = RelationQueryRealizationCompiler.Compile(
    plan,
    targetProfile,
    policy,
    RelationQueryResultObservability.NotRequested);
```

This suppresses independent contributor-occurrence requirements for query row and aggregation results; it
does not relax their value, membership, cardinality, ordering, grouping, or evidence semantics. A rooted
relation still requires root-occurrence correlation because the root is part of that relation's semantic
output contract. Result observability is also distinct from compiler provenance: every derived artifact must
retain attribution to its plan and lowering decisions even when runtime contributor lineage is not requested.

Target profiles are compiler inputs. Their constructors validate basic object shape while retaining
semantically malformed declarations such as unknown numeric capability kinds, invalid limits, repeated
boundary references, and conflicting evidence identities. The matcher diagnoses these with stable
`REL2003`, `REL2004`, or `REL2008` codes and fails closed: every requirement becomes unavailable and the
report status is `Invalid`. Numeric boundary limits must be positive `Int64` values; static facts and measured
values may also be zero. The portable JSON contract encodes these fields as canonical base-10 strings, which
preserves the full non-negative `Int64` range and gives fingerprint-significant values one exact representation
through JavaScript and other runtimes whose JSON number type cannot represent every 64-bit integer. Generated
TypeScript contracts therefore expose these fields as `string`. Consumers should retain that form for transport
and fingerprinting and parse a validated value with `BigInt` when arithmetic is required; converting it to
`number` can lose precision.

Known realization declaration enums retain their canonical string encoding. Undefined 32-bit values retained
for diagnostics use JSON numbers; generated TypeScript widens only those declaration fields to the known enum
union plus `number`, so an invalid report remains a truthful portable artifact.

The report is a portable derived artifact, not canonical relation/query IR. Its fingerprint covers
the compiled-plan reference, relevant target evidence, policy and convention decisions, explicit
overrides, requirements, decisions, and diagnostics. Human descriptions do not affect identity,
and target configuration never changes the canonical definition fingerprint.

The projected static facts live on the requirement itself, so the derived report contains all measurements
used to justify constrained decisions. The public v1 compiler accepts the complete compiled plan and owns
requirement projection, preventing callers from presenting a stale or incomplete synthetic requirement set.
Boundary validation in the resulting portable report is therefore inspectable without access to runtime-only
compiler objects.

Target realization and runtime evidence completeness answer different questions. An unavailable
decision means the target cannot preserve demanded semantics even with complete data. A relation
requirement gap means the target can interpret the plan but an input needed for one evaluation is
missing, unavailable, failed, or inconclusive. The in-memory reference interpreter performs the
runtime gap analysis first, then consumes this same shared realization contract before execution.

Compiled DTO mappers, composed acquisition runtimes, SQL/document/graph/search adapters, explain
tools, and deployment gates can consume the same report contract.

### Profile feasibility and bound realization

Profile feasibility and contextual realization are deliberately separate interpretations. A
`RelationQueryRealizationReport` proves that a target family advertises an exact strategy for the
demanded semantic requirements. It does not claim that a particular table, container, index,
field mapping, source placement, or adapter configuration supplies the evidence needed by that
strategy.

An adapter's `Realize(...)` operation binds that family-level report to the exact plan, selected
result branches, source placement, and persisted adapter binding. The resulting
`RelationQueryBoundRealizationReport` records the exact configuration decisions, adapter evidence,
validated operating boundaries, preserved guarantees, failed settings, and any prerequisite-blocked
requirements. Only a realizable bound report can authorize target-native artifacts:

```csharp
var profileFeasibility = RelationQueryRealizationCompiler.Compile(
    plan,
    targetProfile,
    policy,
    RelationQueryResultObservability.NotRequested);

var contextualRequest = new RelationQueryBoundRealizationRequest(
    plan,
    profileFeasibility,
    placement);
var boundRealization = adapterCompiler.Realize(contextualRequest, adapterBinding);

if (!boundRealization.IsRealizable)
    throw new InvalidOperationException(string.Join(Environment.NewLine, boundRealization.Diagnostics));

var nativeRequest = new RelationQueryNativeCompilationRequest(
    plan,
    boundRealization,
    placement);
var nativeCompilation = adapterCompiler.Compile(nativeRequest, adapterBinding);
```

This split makes planning predictive: a host can inspect exactly which branch, physical fact,
configuration authority, capability, or boundary would prevent execution before the adapter emits
SQL, SDK objects, or another native artifact. Fingerprints connect the profile report, binding,
placement, contextual proof, and generated artifact without turning any of them into canonical
relation/query semantics.

### Explain artifacts and runtime telemetry

`RelationQueryExplainArtifact` is the portable, deterministic explanation of the lifecycle evidence
that is available for one relation/query. Its ordered, `$stage`-discriminated union keeps each phase's
authoritative artifact intact instead of collapsing different questions into one status:

- `staticCompilation` explains the target-independent plan and requirement graph, or the diagnostics
  that prevented a valid plan.
- `profileFeasibility` explains whether a target family advertises exact strategies for the demanded
  capabilities. It does not prove that a concrete source is configured correctly.
- `sourcePlacement` and `boundRealization` explain the selected sources and whether their exact adapter
  bindings provide the evidence and operating-boundary validation required by the profile strategy.
- `physicalPlanning` explains the deterministic acquisition and evaluation plan selected for those
  sources.
- `nativeCompilation` retains target-neutral provenance and fingerprint references for backend-native
  artifacts. Backend SQL, SDK objects, and other mutable native representations remain adapter-owned.
- `evaluation` retains result counts, evidence completeness, requirement-gap summaries, and sanitized
  diagnostics for one runtime observation. It deliberately excludes row values, keys, exception messages,
  and other sensitive or high-cardinality runtime data. Its observation fingerprint is separate from the
  deterministic lifecycle fingerprint.

Stages may be absent when their lifecycle phase was not attempted or its inputs were unavailable. A retained
stage always carries a status consistent with its source artifact, and stage order, affinity, diagnostics, and
the artifact fingerprint are validated by `RelationQueryExplainJsonSerializer`. Explain documents are derived
interpretations; they do not replace the canonical definition, plan, profile, placement, realization report,
or native artifact to which their IDs and fingerprints refer.

When profile or bound evidence is present, the explain artifact's `RelationQueryCapabilitySummary` is a
machine-readable index over the canonical `RelationQueryCapability` union. Each entry links a demanded or
missing capability to requirement IDs and to resolvable target-profile evidence, operating-boundary, and
contextual-evidence IDs. Detailed declarations and
decisions remain authoritative in the target profile and profile/bound realization stages; the summary does not
introduce a second capability or status model. Use it for capability matrices, missing-support views, deployment
gates, and links from a compact overview into the full evidence trail.

Runtime telemetry serves a different purpose. `RelationQueryTelemetry` exposes stable activity, metric, and tag
names for operational monitoring, while explain artifacts are persistable, fingerprinted evidence. The core
`ActivitySource` and `Meter` are both named `Cohesive.Relations`; each adapter owns a correspondingly named source
and meter. The principal activity hierarchy is:

```text
cohesive.relations.evaluate
  cohesive.relations.compile
  cohesive.relations.profile.evaluate
  cohesive.relations.physical.plan
  cohesive.relations.physical.execute
    cohesive.relations.source.read
      adapter source acquisition
    cohesive.relations.interpret

cohesive.relations.native.compile
  cohesive.relations.realize (when compilation performs contextual realization)
cohesive.relations.native.execute
cohesive.relations.dto.compile
cohesive.relations.dto.map
```

Contextual realization may also run as a standalone `cohesive.relations.realize` activity. Compilation from an
already-bound native request does not repeat that child phase.

Operation-duration histograms use only bounded operation, status, and terminal-phase dimensions. Source-row,
DTO-row, and requirement-gap counters likewise use bounded read-kind, row-outcome, policy, status, and gap-cause
dimensions. Evaluation and artifact fingerprints are high-cardinality correlation attributes on traces only; they
are never metric tags. Sampled activities retain structured diagnostic events containing only stable code and
severity. Telemetry does not embed full explain documents, result values, source keys, physical resource names,
diagnostic prose, resolutions, or arbitrary exception text. A trace fingerprint can therefore find separately
retained explain evidence without making sampling, timing, or runtime identities part of deterministic explain
identity.

Instrumentation is best effort. With no listener, the shared emission path does not allocate. Synchronous listener
failures during registration, sampling, recording, or completion are contained and cannot replace a compiler or
evaluation result. Core physical source reads own row measurements, so nested adapter acquisition activities do not
double-count the same work.

### Target-native compilation boundary

`RelationQueryNativeCompilationRequest` is the target-neutral handoff from semantic planning to a backend
compiler. It carries one exact `CompiledRelationQueryPlan`, its exact bound realization report, its plan-scoped
source placement, and the demanded terminal branches authorized by that report. `ValidateInputs()` rejects stale
plan, profile-feasibility, placement, binding, branch-selection, or bound-realization affinity before lowering.

Each selected branch identifies whether it produces relation rows, named query rows, or named query
aggregation rows, together with its retained logical node, result binding and shape, demanded outputs, and
selected fields. Adapters remain responsible for declaring a truthful target capability profile, binding
semantic inputs to physical selectors, validating target-specific operating boundaries, and failing closed
when exact lowering is unavailable.

A successful backend artifact carries `RelationQueryNativeCompilationProvenance`: the exact compiled-plan,
profile-feasibility, bound-realization, placement, adapter-binding, target-profile, compiler-profile, and
convention identities; covered nodes and assignments; physical input fields; contextual evidence; and the final
realization decisions, capability evidence, and validated boundaries that authorized lowering. This is derived
interpretation metadata, not canonical relation/query IR.
It lets execution, diagnostics, explain tooling, and artifact fingerprints attribute target SQL or another
native representation to the semantic requirements that produced it without rescanning or redefining those
semantics.

### Typed source-placement authoring

`RelationQueryPlacement` authors the physical placement for one exact compiled plan without requiring callers to
copy its input, node, binding, or shape identities into low-level constructors. The builder reads the plan's exact
input contract, derives safe acquisition modes and stable plan-scoped identities, and lowers typed CLR selectors or
structural `FieldPath` selectors into the persisted `RelationQuerySourcePlacement` artifact:

```csharp
var placementBuilder = RelationQueryPlacement.For(plan);
var source = placementBuilder.Source(
    sourceKey: "loads-read",
    targetProfile: targetProfile);

var loads = placementBuilder.PlaceSource(source, loadShape)
    .Identity(load => load.Id)
    .FieldsBySemanticPath();

var authoredPlacement = placementBuilder.Build().RequireValue();
RelationQueryPlacedInput<LoadSearchDocument> placedLoads =
    authoredPlacement.GetInput(loads);
```

The `sourceKey` is a deterministic authoring key used to derive convention identities. It is not a database,
container, index, or endpoint name; adapter-owned binding builders declare those physical target facts. Imported
or non-CLR shapes use the same builder through exact compiled contracts and structural paths. A typed shape handle
must match the plan's complete shape-graph snapshot, so rehydrated equivalent documents are accepted while stale
same-ID documents are diagnosed. Missing, duplicate, foreign, stale, or incompatible declarations return stable
structured diagnostics rather than partially populated artifacts.

Placement schema v2 retains one normalized configuration decision for each effective setting. Each decision records
whether the value came from an explicit local declaration, a scoped profile, an adapter convention, or a framework
default, together with the responsible versioned authority. Effective values remain in their dedicated artifact
properties, so provenance is inspectable without creating another source of truth. These decisions participate in
the placement fingerprint and survive serialization.

### Deterministic federated physical planning

`RelationQueryPhysicalPlanner` turns one exact `CompiledRelationQueryPlan`, its canonical reference-interpreter
realization report, plan-scoped `RelationQuerySourcePlacement`, and bounded `RelationQueryPhysicalPlanningPolicy`
into `relation-query-physical-plan/v1`. Placement remains outside canonical relation/query IR: it binds
each compiled source or traversal input to a source instance, execution domain, capability-profile
snapshot, semantic-to-source selectors, and explicit limits. Explicit and convention-derived placement
remain distinguishable and fingerprint-significant.

Policy limits independently bound lookup batches, cumulative retained rows, plan-wide local rows,
per-owner result fan-out, relationship-reference keys extracted from one observation, and concurrency.
Source capability evidence is usable only when every condition attached to that evidence is enforced by
these physical bounds or by attributable target-enforcement evidence; the selected evidence and boundary
identities remain in stage provenance.

The v1 physical stage graph makes acquisition mechanics inspectable:

```text
supplied input or bounded source read
→ exact field projection
→ relationship key extraction
→ key deduplication
→ bounded identity or predicate batches
→ local occurrence correlation
→ runtime evidence assembly
→ canonical reference interpretation
```

Forward at-most-one observation-identity relationships lower to batched identity reads. Forward-many
acquisition remains unavailable in v1 until mixed successful and failed batch outcomes can be represented
without discarding attributable rows. Inverse relationships
lower to batched predicates over the canonical source-reference field. Both paths retain the exact
compiled input IDs and realization, placement, capability, boundary, lowering, and policy provenance
that authorized each stage. An optional traversal removed by demand pruning never reappears in the
physical plan. A statically proven field-equality join can use bounded local correlation over placed
source sets when at least one string identity field is tied to the reader's unique observation identity.
Multiple left, at-most-one sibling enrichments from the same owner binding are sequenced in semantic order;
each downstream read is restricted to owner occurrences that conclusively reached it, and non-reaching owners
receive `NotApplicable` evidence without I/O. A traversal separated from its declared source binding by a filter,
ordering, distinctness, or another cardinality-changing operator may instead use conservative binding
over-acquisition. That strategy reads every already-bounded occurrence of the declared binding, retains `REL2113`
in the physical plan, and leaves logical reachability entirely to the canonical interpreter; it may therefore do
more physical work or reach a declared boundary earlier, but it does not weaken result semantics. Predeclared
conversion failures that can alter an exact sibling reachability chain still fail preflight rather than guessing.
Arbitrary predicates, unbounded collection expansion, and temporal cross-source acquisition fail with structured
`REL21xx` diagnostics rather than falling back to unbounded enumeration or weakened semantics.

`IRelationQuerySourceReader` is the narrow provider port. Every request carries an exact physical-plan
and stage identity, source placement, graph-qualified shape, identity selector, selected semantic fields,
physical-only correlation fields where required, and one closed bounded constraint: enumeration,
identity batch, or relationship-key batch. Results distinguish complete, partial, authoritative
not-found, failed, and inconclusive acquisition, including per-field null, missing, failure, and
inconclusive states. An inconclusive request carries no rows because the runtime evidence contract
cannot attribute provisional rows without overstating their meaning; a provider that has attributable
rows but cannot prove the result set complete returns `Partial`. Providers return expected failures as
evidence; cancellation propagates.

The composed physical executor validates the semantic plan, realization, physical fingerprint, source
instance, execution domain, and capability profile before I/O. It deduplicates keys, chunks them to the
smaller of placement and policy bounds, restores a distinct occurrence for every semantic participation,
assembles `RelationQueryRuntimeEvidence`, and then delegates filters, joins, projections, aggregations,
ordering, paging, gap policy, and output shaping to the evaluator's configured canonical interpreter (the shared
`RelationQueryInMemoryInterpreter.Default` when none is supplied). Realization and terminal execution therefore use
the same interpreter instance; acquisition does not become a second semantic evaluator, and the physical proof
target cannot drift from the interpreter that executes the terminal stage. The v1
reference executor uses a deterministic serial stage schedule, which obeys every positive source and policy
concurrency limit; target runtimes may introduce bounded parallel scheduling without changing stage or evidence
semantics.

### Runtime-compiled DTO materialization

Canonical relation output rows can be specialized into CLR DTO construction kernels without introducing a
second relation/query evaluator. A DTO mapper compiles against one exact `CompiledRelationQueryPlan`, relation
terminal, output shape, CLR target contract, mapper profile, and compilation-options identity. It consumes the
object-shaped `RelationQueryOutputRow.Value` instances produced by canonical interpretation; traversal, join,
filter, projection, temporal, invariant, and requirement-gap semantics remain the responsibility of the
interpreter.

The conventional v1 mapper supports top-level scalar relation fields, immutable constructor or record targets,
and writable or init-only properties. Explicit member bindings take precedence over approved serialized-name
metadata and exact CLR member names. Unsupported structures, ambiguous construction, incompatible conversions,
and stale execution results fail closed with structured `REL33xx` diagnostics. There is no reflection, member-name
discovery, legacy-mapper fallback, or per-field JSON conversion after a kernel has compiled.

Mapping results retain the exact canonical execution and source row objects. Execution status, terminal state,
runtime evidence, requirement-gap analysis, output identity, root and contributing occurrences, and unresolved
row gaps therefore remain inspectable without copying or weakening their meaning. Strict, diagnostic-collection,
and skip-invalid-row policies govern only CLR conversion failures; they do not change canonical row membership or
missing-input policy.

Compiled kernels are derived, process-local artifacts. Cache identity includes the exact plan reference, output
type, mapper profile, and compilation options, while weak plan ownership prevents the cache from extending plan
lifetime. Runtime delegates, reflection metadata, and CLR types are never persisted as canonical IR.

```csharp
var compilation = RelationDtoMapperCompiler.Default.Compile<LoadSearchDto>(plan);
if (!compilation.IsSuccessful)
    return compilation.Diagnostics;

var mapped = compilation.Mapper!.Map(
    canonicalExecution,
    RelationDtoMappingFailurePolicy.Strict);
var dtos = mapped.Rows.Select(static row => row.Value);
```

Use `CollectDiagnostics` when valid rows and every failed source row should be retained for inspection, or
`SkipInvalidRows` when explicitly dropping invalid rows is acceptable. In every mode, inspect `Execution`,
`FailedRows`, and `Diagnostics` rather than treating DTO construction as a replacement for canonical status and
requirement-gap analysis.

### Explicit missing-data semantics

Missing, null, absent, unavailable, and failed are not interchangeable states.

### Deterministic and inspectable conventions

Conventions may simplify common mappings and planning decisions, but convention-derived behavior must remain deterministic, explainable, and attributable.

### First-class provenance

Execution plans, generated queries, compiled mappers, diagnostics, and downstream derived outputs should retain links to their originating semantic definitions.

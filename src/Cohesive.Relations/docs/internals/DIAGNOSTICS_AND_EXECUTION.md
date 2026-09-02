# Cohesive.Relations internals: diagnostics and execution

## Diagnostics and Derivability

Because mappings are represented as derivations, missing inputs can be explained rather than reduced to mapper failures.

For example:

```text
Cannot derive LoadSearchDto.CustomerName.

Required premises:
  Load.CustomerId = "customer-123"
  Customer.Id = "customer-123"

Available:
  Load.CustomerId

Missing:
  Customer with Id "customer-123"
```

Cohesive distinguishes conditions that conventional mapping systems often conflate:

- A field is known and non-null.
- A field is known to be null.
- A relationship is known to be absent.
- A required fact has not been supplied or fetched.
- A lookup completed without finding a match.
- A lookup failed.
- A fact was not requested because the output did not require it.

Diagnostics are structured product output. They should be usable by applications, tests, deployment gates, index-management tools, and developer tooling.

### Runtime evidence and relation requirement gaps

Runtime availability analysis consumes the static plan rather than walking the relation/query
expressions again:

```text
Compiled input contract
+ occurrence-scoped runtime evidence
→ causal relation requirement gaps
+ explicit missing-data policy
→ decisions and diagnostics
```

These stages remain separate. The input contract says what the demanded semantics require.
Evidence says what one evaluation knows. A requirement gap identifies the causal boundary and retains every
affected output, effect, and requirement trace. Policy decides whether an impact remains
unresolved, is suppressed, receives null, or receives an explicit semantic default. Reporting
policy independently decides whether that impact becomes a diagnostic.

Evidence is scoped by both an evaluation ID and a binding-occurrence ID. An occurrence is not an
entity ID: the same observation may participate more than once through different routes or
bindings. This distinction lets one load have a missing customer without making the customer input
appear globally unavailable for every load in a batch.

Every evidence snapshot is also attributed to the compiler profile, definition and relationship
catalog fingerprints, a versioned semantic fingerprint of the shape snapshots, the effective
output-demand fingerprint, and the ordered compiled input identities. Equivalent plans rebuilt
from rehydrated semantic documents match; document metadata and object identity do not participate.
Evidence produced for a different semantic shape, demand, or input contract is rejected with a
structured plan-mismatch diagnostic before requirement-gap analysis.

The evidence model preserves distinctions required for useful diagnostics:

- a source was not provided versus was provided successfully with zero rows;
- a source result was complete versus partial, failed, or explicitly inconclusive;
- a field was not loaded versus was loaded and semantically absent;
- an explicit null versus an absent value;
- a parameter that was not supplied versus one supplied as missing, explicit null, or a concrete value;
- relationship resolution was inapplicable, not attempted, failed, rejected, inconclusive, or completed;
- a completed result is partial versus authoritative and complete;
- an authoritative lookup found no row versus no lookup evidence being available.

For example, a caller can describe a load whose reference is available but whose customer lookup
has not run:

```csharp
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;

var root = plan.InputContract.Sources.Single(
    source => source.Role == RelationQuerySourceInputRole.RelationRoot);
var traversal = plan.InputContract.Traversals.Single();
var loadId = root.Fields.Single(field => field.Input.Field.Path == FieldPath.FromField("Id"));
var customerId = root.Fields.Single(
    field => field.Input.Field.Path == traversal.Definition.SourceReference);
var load = new RelationQueryObservationOccurrence(
    new("load/L1"), root.Binding, root.Shape, observationIdentity: "L1");

var evidence = new RelationQueryRuntimeEvidence(
    evaluation: new("index-load/L1"),
    plan: plan,
    sources:
    [
        new(root.Input.Id, RelationQuerySourceEvidenceState.Provided, [load])
    ],
    fields:
    [
        new(loadId.Input.Id, load.Id, RelationQueryFieldEvidenceState.Value,
            ObservationValue.FromString("L1")),
        new(customerId.Input.Id, load.Id, RelationQueryFieldEvidenceState.Value,
            ObservationValue.FromString("C7"))
    ],
    traversals:
    [
        new(traversal.Input.Id, load.Id, RelationQueryTraversalEvidenceState.NotAttempted)
    ]);

var analysis = RelationRequirementGapAnalyzer.Analyze(plan, evidence);
```

The result contains one causal `ResolutionNotAttempted` requirement gap anchored to the traversal. It carries
the customer-derived output impacts and original compiler traces; it does not also emit noisy
Customer identity and Customer.Name gaps. The conventional policy reports required impacts and
retains optional impacts without reporting them. Suppression, null substitution, and default
substitution are explicit alternatives, and invalid substitution is diagnosed against the output
shape contract. A semantic default must be a concrete non-null, non-missing value; explicit null
uses the distinct null-substitution disposition so persisted policy decisions retain their meaning.

A complete evidence snapshot gives omission closed-world meaning inside its declared boundary.
Partial evidence does not: it may establish an explicit requirement gap, but an omitted entry cannot prove
not-loaded or not-found. If a source occurrence did not reach a traversal because of filtering or
another logical operation, evidence marks that traversal occurrence `NotApplicable` rather than
allowing omission to be mistaken for `NotAttempted`.

`RelationRequirementGapAnalyzer` is analysis only. It does not read sources, resolve relationships, execute
expressions, construct output rows, or apply suppression/substitution decisions. Those are later
execution or acquisition consumers of the same compiled contract.

### Canonical in-memory reference execution

`RelationQueryInMemoryInterpreter` executes a successful static plan over materialized runtime evidence.
It consumes the plan's explicit demand-scoped `ExecutionSlice`; it does not rediscover assignments or
expression sites by scanning the persisted definition, acquire external data, or choose a physical join
or batching strategy.

The reference interpreter currently executes every canonical logical node: source, filter, relationship
traversal, explicit join, valid-time join, collection expansion, projection, distinct, aggregation, ordering,
and offset or keyset paging. Relation terminals enforce per-root cardinality, keys, and invariants. Query terminals retain
their named row or aggregation branches. Results carry exact root attribution, contributing occurrence
provenance, requirement gaps, policy effects, and deterministic diagnostics. Partial evidence remains
explicitly incomplete; it is never converted into semantic null or absence. Expression input availability
is checked when a field, parameter, or capability is actually read, preserving short-circuit and conditional
evaluation semantics. Causal structural gaps are enforced through their compiled `BlockedInputs`, so nominal
source or traversal evidence is never consumed after an upstream conversion or relationship boundary failed.

The evaluator intentionally has a bounded first-version surface. It supports canonical unary and binary
operators plus the pure collection, object, string, and aggregate functions covered by the reference tests.
This includes direct current-element field reads inside scoped collection functions such as two-argument `Any`
when the structured collection is present in runtime evidence. Ambient functions (`entityId`, `key`, and
`sourceRows`) and the pure `groupBy`, `groupByRows`, and expression-level `join` functions are not yet interpreted.
Explicit element-path evidence records and deeper nested collection-element paths also remain outside this
closure. The interpreter publishes this narrower expression surface through
`RelationQueryInMemoryInterpreter.ExpressionCapabilities`, publishes valid-time semantics through
`DefaultTemporalCapabilities`, and rejects unsupported demanded semantics during preflight with an attributable
`REL3209` diagnostic rather than falling back to a different or weakened meaning.

Valid-time join support is declared independently through
`RelationQueryInMemoryInterpreter.DefaultTemporalCapabilities`. The conventional interpreter supports the
complete canonical temporal surface, including both match forms, explicit boundary and null policies, exact
temporal domains, outer-join absence semantics, all-match multiplicity, interval validation, and inconclusive
evidence propagation. An interpreter instance may instead receive a narrower
`RelationQueryTemporalExecutionCapabilityProfile`; preflight rejects each unsupported demanded temporal semantic
with `REL3209` attributed to the temporal node and exact expression or structural match site.

Runtime value semantics are likewise explicit. Equality is structural and ordinal, distinguishes null from
undefined, and compares integers with floating-point values only when they represent the same exact integer.
Ordering accepts only a shared comparable domain and applies the query's declared null placement; arithmetic
uses finite values in a checked decimal execution domain and reports divide-by-zero, overflow, and unsupported
numeric representations as structured expression failures.

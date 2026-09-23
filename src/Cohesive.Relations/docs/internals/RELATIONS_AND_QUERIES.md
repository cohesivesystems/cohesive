# Cohesive.Relations internals: Relations and Queries

## Relations and Queries

Relations and queries use the same logical operators because both describe relational computation:

- Sources
- Filters
- Relationship traversal
- Explicit joins
- Valid-time joins
- Unnesting
- Projection
- Distinctness
- Ordered representative selection
- Aggregation
- Ordering
- Paging

They differ in their semantic contract:

| Relation | Query |
|---|---|
| Describes a reusable correspondence or derivation | Describes an invoked request for results |
| Rooted in an input value | Rooted in one or more named result branches |
| Declares output cardinality per root | Declares rows, aggregations, ordering, and paging |
| Supports rooted derivation and dependency analysis | Supports retrieval and reporting |
| Can identify inputs that influence rooted outputs | Is normally evaluated in response to an invocation |

The distinction is semantic rather than physical. Neither construct chooses a database, join algorithm, batching strategy, or execution runtime.

A Cohesive relation is also not synonymous with a table in the relational-database sense. A compiler may realize a relation as a SQL expression, compiled mapper, or application-side plan, but the relation itself remains portable.

## Ordered representative selection

`SelectRepresentativeQueryNode` chooses one uniquely best input occurrence per partition. Its keys use canonical
value equality, preserving missing versus null. Its nonempty `QueryOrdering` sequence defines the preference:
direction and null placement are explicit, and missing/null ordering values share the null placement. Empty keys
mean one global partition; rooted relations always partition by root occurrence as well. Empty input produces no row.

The best complete ordering tuple must identify one occurrence. Tied winners fail with `REL3212`, even if their
visible values are identical. Ties among losing rows do not matter. Append a stable identity to the preference
sequence when the source permits equal primary preferences. The selected row retains its bindings, outer-join
presence and input provenance; discarded candidates do not contribute provenance. Partition output order is
unspecified, so use an explicit `OrderQueryNode` for presentation or paging.

```csharp
var author = RelationQuery.Expression();
var candidates = author.Source<Candidate>();
var winners = author.SelectRepresentative(candidates.Node, row => row.Category, candidates.Binding,
    [author.Ordering(row => row.Revision, candidates.Binding, QuerySortDirection.Descending),
     author.Ordering(row => row.Id, candidates.Binding)]);
```

For a category with revision 1 eligible and revision 2 ineligible, selection followed by an eligibility filter
returns no row. Moving that filter before selection would return revision 1 and change the meaning. Static
compilation retains partition and ordering expressions as membership dependencies even when the output does not
project them. Keyed `DistinctQueryNode` keeps its existing first-encountered row and merged group provenance.

The node persists as `$node: "selectRepresentative"` in `relation-query/v1`, participates in canonical fingerprints,
and is emitted into TypeScript contracts by code generation. Consumers with exhaustive node unions must recognize
the additive case; older readers reject it. The in-memory realization profile advances to
`cohesive.relations.in-memory/realization-v3`. Its execution uses a single candidate scan and retains a best row plus
tie status per partition, on top of the interpreter's already materialized input and evidence.

The reference interpreter supports this operation. Native adapter profiles do not yet advertise it. Shared SQL
`RowNumber` construction is a lowering primitive, not proof of these semantics. COH-96 still requires a SQLite
compiler with bound placement evidence, exact encoding/comparison constraints, unique-order proof, presence and
provenance projection, differential tests and indexed query measurements before application adoption.

## Valid-Time Joins

A `TemporalJoinQueryNode` correlates two independently produced rowsets using both an ordinary
Boolean key predicate and an explicit valid-time condition. It models when row-valued facts were
valid; it does not select a database snapshot, read system-versioned history, or consult an ambient
clock.

For example, an event can be enriched with the customer version that was valid when the event
occurred:

```csharp
var temporalJoin = new TemporalJoinQueryNode(
    id: new("event-customer-version"),
    left: eventSource,
    right: customerVersionSource,
    kind: JoinKind.Left,
    correlation: Expr.Eq(
        Expr.Field(eventBinding, FieldPath.FromField("CustomerId")),
        Expr.Field(versionBinding, FieldPath.FromField("CustomerId"))),
    match: new TemporalPointInIntervalMatch(
        Expr.Field(eventBinding, FieldPath.FromField("OccurredAt")),
        TemporalInterval.HalfOpen(
            Expr.Field(versionBinding, FieldPath.FromField("ValidFrom")),
            Expr.Field(versionBinding, FieldPath.FromField("ValidTo")),
            upperNullBehavior: TemporalNullBoundBehavior.Unbounded)));
```

Point containment reads its point from the left rowset and its interval from the right. Interval
overlap reads its first interval from the left and its second from the right. Only the ordinary
correlation expression sees the combined pre-null-extension binding scope. Swapping the inputs
expresses the inverse point/interval orientation.

The persisted interval records every finite endpoint as inclusive or exclusive. `HalfOpen`
conventionally produces `[lower, upper)`, but canonical semantics never depend on that convention.
Endpoint states remain distinct:

| Endpoint or interval state | Meaning |
|---|---|
| `UnboundedTemporalIntervalBound` | Structural negative or positive infinity |
| Expression evaluates to null with `Invalid` | Invalid operand and incomplete result |
| Expression evaluates to null with `Unbounded` | Explicit source convention for an open end |
| Expression is missing or unavailable | Indeterminate evidence, never an unbounded endpoint |
| Lower follows upper | Invalid interval with an attributable diagnostic |
| Equal inclusive endpoints | A valid singleton interval |
| Equal endpoints with either endpoint exclusive | A valid empty interval |

`Date`, `DateTime`, and `Instant` are exact, non-coercing domains. `DateTime` uses civil or
wall-clock ordering; `Instant` uses absolute ordering, so equivalent instants with different
offsets compare equally. Interval emptiness and overlap use the domain's representable precision:
civil days for `Date`, civil ticks for `DateTime`, and UTC ticks for `Instant`. Every pair satisfying
both correlation and temporal membership is emitted. Overlapping versions therefore produce
multiple rows; the operator never chooses a "latest" winner.

Missing fields, partial source results, and partial traversals make affected candidates
indeterminate. An outer join emits an unmatched row only when the opposite candidate set is
complete and every applicable candidate is a conclusive non-match. Matched rows may still be
returned from incomplete evidence, but the result remains explicitly incomplete.

Backend interpreters may preserve these semantics with SQL range predicates, native range types,
interval indexes, or batched acquisition followed by in-memory evaluation. The temporal execution
capability profile declares which match, boundary, domain, join, and inconclusive-evidence semantics
a target preserves. The compiled input contract lists the exact demand-scoped temporal capabilities
with stable requirement IDs and node/site provenance. A target missing one must reject the plan with
an attributable diagnostic rather than weakening it. System-time acquisition, nearest-predecessor
`ASOF` joins, temporal relationship traversal, and physical interval-index planning are separate
future semantics.

Temporal operands are retained as correlation, membership, cardinality, and validation influences
in the requirement graph, output-oriented lineage, and dependency manifest. Lineage keeps these in
its `Influences` channel while `Contributions` remains intentionally narrow to value, identity, and
aggregation provenance. Consumers performing invalidation or dependency-impact analysis can therefore
walk output-oriented influences or the inverse input-oriented dependency manifest without treating
a membership predicate as a projected value.

## Portable Relation Drafts

A relation draft is a portable, persistable relation under construction. It carries the canonical
logical graph up to the projection boundary, stable output-assignment slots, semantic expression
candidates for those slots, and an explicit resolution for each slot. The draft is separate from
the accepted `RelationDefinition`: unresolved and ambiguous assignments are useful states for
authoring, inference, review, and diagnostics, but they are not executable relation semantics.

Draft producers can be simple conventions, host-language authoring tools, importers, or inference
systems such as Ari. Producer-specific evidence remains outside the semantic draft. For example,
Ari may associate model scores, explanations, review decisions, and run metadata with stable draft
slot and candidate identifiers without placing those concerns in `Cohesive.Relations`.

This is an integration boundary rather than a competing proposal model. Ari's
`MappingInferenceResult` remains its inference and workflow artifact. An Ari adapter can lower its
source/target paths into draft slots and canonical expression candidates, retain confidence,
alternatives, features, model versions, and review state in Ari, and associate that evidence with
the content-derived draft candidate identifiers. A draft document can point back to the Ari
artifact through an opaque producer-artifact reference. Changing Ari evidence does not change the
draft fingerprint; changing a slot, candidate expression, or resolution does.

Acceptance is a shape-aware semantic boundary. It verifies that every output field is explicitly
resolved or, when optional, explicitly omitted; that selected source fields exist; and that type,
cardinality, presence, and nullability are safe. Successful acceptance appends the canonical
projection node and produces a normal `RelationDefinition`. The draft identity remains stable
across revisions, while its content fingerprint changes as candidates and resolutions change.
The result retains the consumed draft fingerprint and relationship-catalog fingerprint as
provenance, separately from the accepted relation's canonical fingerprint.

Explicit binding-qualified field candidates may navigate nested inline or graph-local named
structural fields, for example `load.Header.ShipmentId`. Acceptance reuses the canonical query
shape resolver and compares the effective `ValueContract`; optional or nullable ancestors weaken
the resulting value even when the leaf itself is required and non-null. Optional relationship
bindings remain a separate absence check. The terminal field retains its type and cardinality,
and graph-local named types are not equated across different graphs.

This capability does not infer a traversal or restructure values. Every path segment must name a
field, and intermediate values must be single-valued structures. Collection elements, indexes,
scalar descent, absent members and unknown structures remain rejected. Target slots remain top-level
output fields and convention matching remains top-level only.

Selected `object` calls may construct single-valued inline or named structures from constant string
keys and supported field, object or collection-selector expressions. Every supplied key must be
unique and declared by the target; computed children cannot be assigned. Every required, non-computed
child must be supplied. Optional child omission is explicit in the authored expression. An optional
or nullable *containing target* does not weaken its children: constructing an object always creates
a present, non-null object whose required children must meet their own contracts. Source ancestor
weakness and optional relationship bindings are still checked at every leaf. Copying a complete
collection-valued child is allowed only when its exact type/cardinality/presence/nullability match.
Declared object return types must match the target type, or use the normal unknown metadata marker.

The acceptor extends its existing conservative assignment policy rather than adding another
expression IR or an application validator. The query expression analyzer remains authoritative for
scope, capability, arity and declared-expression checks; its general `object` result category alone
does not prove child contracts. Acceptance uses the existing shape resolver's one-level structural
view and the same direct-field compatibility routine as convention matching. Named child identities
remain graph-local; structural construction does not license direct assignment between named types
from different graphs. The semantic draft and exact supplied shape snapshots remain authoritative.

Selected `select(source, selector)` calls project ordered collections. The source must be a statically
resolved binding-qualified field, a current-item read inside an enclosing selector, or an explicit default over either. Both `Many`
fields and explicit array types use their canonical effective element contract. Every source must
be required and non-null, including containing fields and relationship binding availability: the
canonical evaluator rejects missing/null collections, so acceptance never substitutes an empty one.
The target must be a collection. Its optionality does not weaken its elements. A declared return type
must equal the effective target collection type, or retain the normal unknown metadata marker.

The selector can read `CurrentItem()` or `item.Field`, construct an object, read an explicit outer
binding, or select a nested collection. Nested selectors replace the current-item scope; outer
binding reads remain explicit. Item paths use the existing graph-aware resolver and direct value
compatibility, preserving required/nullable children and named-type identity. Repeated values, empty
collections and ordering are preserved by the existing canonical evaluator. No new IR, runtime
operator or implicit path-to-projection rewrite is introduced; authors must declare `select`.

Explicit defaults use the canonical `coalesce(value, fallback)` call, also authored with
`Expr.Coalesce`. For example, `select(coalesce(source.references, []), item => item)`
returns an empty collection for a missing or null field and preserves supplied references in order.
Without the authored default, the optional-to-required assignment remains unsafe. False, zero,
empty strings and empty collections are present values; they do not select the fallback.

The evaluator evaluates the source once and evaluates the fallback only for missing or null values.
Unavailable input and evaluation errors propagate. Static dependency analysis still records both
branches, so lazy evaluation does not promise lazy backend acquisition. General expression analysis
joins the present source contract with the fallback contract without refining other reads.

Native draft acceptance admits a narrower profile: the source must resolve through an explicit
binding or current-item scope, and the fallback must be a portable constant satisfying the source's
present, non-null contract. The resulting value retains source graph identity and cardinality.
Computed fallbacks and named scalar literals need further admission semantics. Standalone literals
may populate known scalar, enum, quantity, entity-reference or JSON contracts; null requires a
nullable target, and an empty array requires a collection target. Nonempty arrays, object literals
and undefined literals are not admitted. Undefined cannot be a portable default literal because
the current value wire format serializes it as null.

This is reusable expression semantics and acceptance, not a product default-selection policy.
Ari must separately expose and retain an explicit policy decision before generating defaults.
Existing `CallExpr`, value-contract compatibility, native serialization and capability declarations
remain authoritative; no alternate expression IR or backend-specific default rewrite is introduced.
Other interpreters must declare support before realizing this function.

Dynamic keys and conversions remain outside this bounded acceptance profile. They can remain in portable draft documents and receive structured
unsupported diagnostics on acceptance. Static acceptance does not prove input-dependent field or
shape constraints, business meaning, or operational correctness; execution still admits observations
against their exact graphs. This extends Ari's ARI-569 acceptance profile; conversion acceptance,
publication and Ari compiler adoption remain separate deliverables.

The resolver is created once per acceptance invocation over validated exact graphs, with no
process-wide cache or new expression/path IR. `RelationDraftAcceptanceTests` exercises inline and
named nesting through native serialization, acceptance, compilation and supplied-root execution,
as well as ancestor absence/nullability and unsafe type/cardinality/collection paths.
`RelationDraftObjectAcceptanceTests` covers inline/named and nested construction, omission, malformed
keys/arity, source weakness, child cardinality, exact provenance and execution. Object validation
visits each constructed node and child once per acceptance, with invocation-owned field indexes;
there is no per-row preparation or persistent cache. `RelationDraftCollectionAcceptanceTests` covers
portable roundtrip/provenance, scalar/object item projection, inline/named item structures, nested
scope/correlation, empty/repeated/ordered results, source absence, child safety and graph-local type
identity. Acceptance traverses selector syntax and contracts once, independent of observation count;
execution continues to use the existing select evaluator. `RelationDraftDefaultAcceptanceTests`
covers native roundtrip, deterministic provenance, missing/null/present execution and unsafe fallback
rejection. Core analysis tests protect contract refinement; runtime tests protect lazy evaluation,
error propagation and payload reuse independent of collection size. BenchmarkDotNet exercises scalar,
nested and collection values with preparation outside measurement.

Three kinds of incomplete information remain distinct:

- A **definition hole** is an unresolved, ambiguous, or unsafe draft assignment and prevents
  acceptance.
- **Inference uncertainty** is producer-owned evidence such as an Ari confidence score; policy may
  turn it into a selected, ambiguous, or unresolved draft state.
- A **runtime requirement gap** occurs after acceptance when required observations are unavailable, such
  as a load whose referenced customer cannot be resolved. Runtime availability is evaluated against
  the compiled input contract and does not make an otherwise complete draft unresolved.

The draft graph may already contain relationship traversals, so a flat DTO projection can select
fields from several visible bindings:

```text
Source(Load as load)
→ TraverseRelationship(load.CustomerId → Customer as customer, join: Left)
→ Project(
    LoadSearchDto.Id           = load.Id,
    LoadSearchDto.CustomerId   = load.CustomerId,
    LoadSearchDto.CustomerName = customer.Name,
    LoadSearchDto.CustomerType = customer.Type)
```

The built-in convention matcher intentionally handles only direct, top-level field matches in its
first version. It applies explicit aliases first, then exact ordinal names, then a unique
ordinal-ignore-case name. An unsafe higher-precedence match remains a diagnosed hole rather than
falling through to a lower-precedence guess. Exact whole-value copies of arrays and inline objects
are allowed when their portable types and field guarantees match; inferring navigation or
restructuring inside those values is not.

More sophisticated producers can propose relationship traversals and cross-binding assignments
using the same draft contract, so flattening `Customer.Name` into `LoadSearchDto.CustomerName` does
not require a second relation model. Automatic traversal discovery, automatic nested structural
mapping, broader structural mappers, backend lowering, and proof that a declared
relation output mode matches row-multiplying or row-dropping graph behavior remain follow-on
interpretations or analyses.

## Relationship to GraphQL

GraphQL and `Cohesive.Relations` both support querying heterogeneous data sources through a
uniform interface, but they operate at different levels of abstraction.

GraphQL defines a remote API contract through which remote clients submit queries and receive
results. `Cohesive.Relations` defines a node-level semantic protocol through which application
components describe relationships, queries, projections, aggregations, and data requirements.
Node-level does not mean that the data or execution must be local: a Relations interpreter may
read from local memory, databases, remote services, or several heterogeneous sources. It describes
the level at which the semantic contract is consumed rather than the placement of its data.

The Relations IR is independent of the remote API used to expose it, but it is itself a protocol
between authoring tools, compilers, planners, repositories, and interpreters. It may also be
persisted, transferred between nodes, and projected into other host languages.

### Resolvers and canonical source acquisition

A GraphQL implementation binds field resolvers to fields in its remote API schema. Canonical
Relations execution instead derives source requirements from a compiled definition and binds them
through explicit source placement. The physical executor issues bounded requests through
`IRelationQuerySourceReader`; target-native compilers can replace compatible work with a native
backend query.

The contracts are different. A GraphQL resolver satisfies a field in a particular client-facing
schema. A Relations source reader supplies evidence for an exact placed semantic input,
independently of whether or how that data is exposed remotely. The compiled plan retains enough
meaning to validate batching, local joins, native pushdown, completeness, and failure behavior.

### Remote API exposure

`Cohesive.Relations` does not itself establish a remote client/server interface or define a remote
invocation protocol. `Cohesive.Api` can expose Relations semantics through GraphQL, REST, gRPC, or
another remote API technology. A GraphQL interpretation could lower a client selection set into
relation/query IR, execute it through selected readers and adapters, and project the
result into the GraphQL response shape.

```text
Remote client
→ Cohesive.Api GraphQL operation
→ Cohesive.Relations query
→ source placement, readers, and backend adapters
→ heterogeneous data sources
```

### Mutations and workflows

`Cohesive.Relations` models data acquisition and computation rather than state mutation. A GraphQL
mutation defines a remote API operation, but not the underlying state-change semantics.
`Cohesive.Api` may expose an operation as a GraphQL mutation, REST action, or gRPC method;
`Cohesive.Transitions` defines its entity state changes and invariants; and `Cohesive.Processes`
coordinates multistep workflows involving transitions, queries, waits, external effects, and
recovery or compensation behavior.

```text
Remote client
→ Cohesive.Api operation
→ Cohesive.Transitions transition or Cohesive.Processes process
→ state changes and external effects
→ Cohesive.Relations result projection
→ remote API response
```

## Key-qualified collection mapping

The existing canonical `Expr.Join(leftKey, rightKey, rightCollection)` filters a collection by
key equality. The in-memory evaluator now realizes it, and native draft acceptance supports a bounded
constant-key profile. For example:

```text
select(
  join("PO", item.qualifier, coalesce(source.references, [])),
  item => item.value)
```

For references `[{"qualifier":"PO","value":"A"},{"qualifier":"BN","value":"B"},
{"qualifier":"PO","value":"A"}]`, this returns `["A","A"]`. An empty source or no matching keys
returns `[]`. The source must be present and non-null unless an explicit coalesce supplies it.
Filtering does not assert that a qualifying item has a required value; selector acceptance still
checks its original contract. This is a reusable key-selection mechanism, not an inferred EDI policy.

The left key is evaluated once in the enclosing scope, then the right collection is evaluated once.
Each right key is evaluated in that item's scope; nested operations do not overwrite the enclosing
scope. Equality delegates to canonical `ObservationValueSemantics`: numeric equality is preserved,
text is ordinal/case-sensitive, and missing and null remain distinct. Runtime errors and unavailable
inputs propagate. General expression execution supports arbitrary key expressions; draft acceptance
currently requires a non-null portable scalar literal on the left and a resolvable field/current-item
read (or admitted coalesce) on the right. The literal must inhabit the right key's present contract.
Named scalar literals, arbitrary predicates, implicit conversions and single-result selection remain
outside this profile. Optional qualifier fields can simply fail to match the non-null key, but no
other field is refined by that comparison.

The canonical function catalog owns arity and scoped argument metadata. Its collection-of-scoped-source
result rule preserves the source's element type for subsequent selectors. Acceptance preserves exact
source graph identity and cardinality. Native documents, candidate fingerprints and capability evidence
remain authoritative; no new wire node, Ari validator or protocol-specific identifier table is added.
Adapters without the join capability continue to reject it.

Cohesive fit: `Expr.Join` already declares exactly the required key-filter semantics. A new filter
function would duplicate that responsibility for this use case. `FilterQueryNode` filters relation rows,
and `ExpandCollectionQueryNode` expands them; composing those would change row grain and require
regrouping to preserve per-parent collection outputs. Extending the existing expression interpreter
and draft acceptor is the narrower fit. Ari still owns explicit qualifier selection and later compiler/
CLI adoption; no automatic business policy is supplied here.

Execution scans once and evaluates each right key once, with invocation-owned storage growing only
with matches. Result elements retain existing immutable payload storage; builder buffers are temporary,
and the final collection contains matching values in source order. There is no cache, backend query
or per-row acceptance preparation. Benchmark results cover scalar, nested, collection-heavy and
4,096-item inputs with no matches and half matching. This is new-function cost evidence, not an
end-to-end latency or optimization claim.

`RelationDraftJoinAcceptanceTests` covers native roundtrip, exact fingerprints, direct filtering and
selection, ordered duplicates, no matches, optional sources/defaults, key/type/arity rejection and the
absence of inferred presence refinement. Runtime tests cover enclosing/item scopes, canonical equality,
null/missing source rejection, propagated key errors and shared payload storage. Core analysis tests
check the retained element contract, capability requirement and scoped-key boundary.


### Explicit code decisions in draft candidates

Native draft acceptance supports existing `ConditionalExpr` / `Expr.If` candidates with a bounded
predicate: `Eq(resolved source, non-null portable scalar constant)`. For example:

```csharp
var code = Expr.Coalesce(Expr.Field(new ValueBindingId("source"), "purposeCode"), Expr.Const("missing"));
var purpose = Expr.If(Expr.Eq(code, Expr.Const("00")), Expr.Const("Original"),
    Expr.If(Expr.Eq(code, Expr.Const("01")), Expr.Const("Cancel"), Expr.Const("Unknown")));
```

Before this admission extension, that portable expression could be retained but not accepted as a
relation draft candidate. It now maps `00` to `Original`, `01` to `Cancel`, and other/missing/null
codes to `Unknown` under the explicit fallback decisions above. These are illustrative authored
codes, not protocol rules. Ari owns any protocol-specific data and the decision to apply it.

The source resolves through existing binding/item scope and default rules. Canonical equality
fails on missing values, so optional source reads require an explicit default. Present nullable
values can be compared: null does not equal a non-null code. The literal must satisfy the source's
present scalar contract; there are no implicit numeric/text conversions or named-scalar escapes.

Both branches are validated against the complete target contract, including every enum member,
required object child, graph-local type identity, presence and collection element contract.
Predicates never refine branch contracts. Nested conditionals, objects and collection selectors
reuse the same recursive validation. A declared conditional return type must match the effective
target type or retain the normal unknown metadata marker. Computed/dynamic keys, arbitrary
predicates, reversed equality and general conversion functions remain outside this bounded profile.

The existing canonical evaluator retains equality and lazy branch execution; no evaluator,
expression kind, wire format, type conversion model or backend capability is added. Static
analysis still records both branches, so lazy evaluation does not imply lazy backend acquisition.
Admission remains invocation-owned preparation with no per-row work or persistent cache. Each
conditional validates its predicate once and visits each branch once; existing canonical analysis,
validation and fingerprinting retain their own costs. Code tables lower to ordered conditional
chains, so evaluation cost grows with the number of attempted cases; this is not an indexed lookup.

`RelationDraftConditionalAcceptanceTests` covers native document roundtrip, exact fingerprint
provenance, supplied-observation execution, source defaults, null and unmatched codes, collection
item scope and duplicates, nested branches, target enums, required object children and rejected
unsafe/malformed policies. Full protocol-to-canonical acceptance and application authoring policy
remain consumer deliverables; successful native acceptance alone is not business approval.


### Qualified scalar selection and exact numeric conversion

Portable drafts can compose `join`, `select`, `single` and `parseDecimal`. For example:

```csharp
var matching = Expr.Join(Expr.Const("SH"), Expr.Field("item.role"),
    Expr.Field(new ValueBindingId("source"), "values"));
var weights = Expr.Call(ExprFunctionNames.Select, matching,
    Expr.Call(ExprFunctionNames.ParseDecimal, Expr.Field("item.weight")));
var weight = Expr.Call(ExprFunctionNames.Single, weights);
```

With `[{role:"CN",weight:"999"},{role:"SH",weight:"0012.50"}]`, the result is Decimal
`12.50`, independent of ordering. Zero or multiple SH entries fail at execution; identical duplicates
are still ambiguous. Neither operation invents a missing-value default. The caller must declare
one with `coalesce` when appropriate, and native admission still checks its contract.

`parseDecimal` accepts an optional leading sign, one or more ASCII integer digits and an optional
fraction containing one or more digits. Leading integer zeros and trailing fraction zeros are
allowed. Whitespace, grouping separators, exponents, missing/null values, overflow and any loss of
precision are rejected. Parsing delegates to `ObservationValue.TryParseExactDecimal`. Strict text and JSON-number acquisition
share the same core scanner and exact representability check, with exponents permitted only by the
JSON entry point. The scanner retains nonzero digit bounds; at most 29 coefficient digits are passed
to BCL `UInt128.TryParse` using stack storage. The coefficient must fit 96 bits and scale 0–28. It does not reinterpret protocol-specific implied scales.
Those are separate, explicitly authored semantics.

`single` retains the exact element type and payload. A read-valued `select` can supply its input,
including scoped numeric conversion. Draft admission verifies source presence, nullability, element
contracts and declared return metadata. It does not claim that a schema proves runtime cardinality
or that every string is a valid number: those failures remain execution diagnostics. Source reads
are evaluated once per operation; selectors retain their existing current-item scope.

Named enum keys, fallbacks and output literals use `ObservationValidator` against the **owning**
graph, including declared names and underlying literals. A source and target may use identical local
type IDs with different definitions. Direct assignments still require compatible graph identity;
accepting a literal does not erase that identity. Conditional branches with unknown return metadata
can use the site type after independent validation, allowing `code == "yes" ? 1 : 0` to populate
Int32. Out-of-range constants and unknown Int64 field values cannot narrow this way.

These are extensions of the canonical expression catalog, analyzer, evaluator and draft admission;
no protocol catalog, alternate expression IR or application-specific conversion registry is added.
Other interpreters must explicitly advertise support. The new calls use the existing portable call
wire format and are not automatically translated from arbitrary C# methods or pushed into backends.

Qualification: `RelationDraftConditionalAcceptanceTests` roundtrips and executes the composed
example, graph-local enums and contextual integers; `RelationQueryExpressionEvaluatorTests` covers
empty/ambiguous selection, exact numeric boundaries, malformed input and bounded allocations.
`ExprAnalysisTests` covers static source/result contracts. This qualifies generic primitives, **not**
a complete EDI 204 mapping, its acquisition boundary or Ari's operational acceptance corpus.

See [operation performance evidence](../../../../docs/performance/relation-value-contracts.md) for
warm costs, allocation bounds and the reproducible benchmark command.


Select's assignment and nested-composition admission share one source boundary for arity, collection
type, presence, nullability and item scope. Target-driven selector validation remains distinct from
inferred selector contracts because they prove different things. Select, Join and conversion source
failures now stop resolution before a required-looking contract can reach a parent expression.

General `ConstantExpr` widening across compositions remains an open follow-up to #370/#372.
This change retains `IsSatisfiedByConstant` and the existing contextual conditional analysis;
it does not introduce a graph-aware, generally typed constant representation or close that issue.

The parsing audit also checked `TryGetDecimal`, CLR Single projection, primitive-literal matching,
and temporal JSON conversion. Those use BCL parsing for their existing coercion or wire-format
contracts; they are not interchangeable with strict exact-decimal conversion. Their accepted syntax
and rounding behavior are unchanged here. DOM and streaming JSON numeric acquisition both delegate
to the consolidated exact parser, retaining their existing non-Decimal fallback behavior.

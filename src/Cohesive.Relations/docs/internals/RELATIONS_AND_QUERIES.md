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

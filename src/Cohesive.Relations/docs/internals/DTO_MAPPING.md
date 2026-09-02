# Cohesive.Relations internals: DTO mapping

## DTO Mapping as Relational Programming

Consider a search document containing data from a load and its customer:

```text
Source(Load as load)
→ TraverseRelationship(load.CustomerId → Customer as customer)
→ Project(
    LoadSearchDto.LoadId       = load.Id,
    LoadSearchDto.Status       = load.Status,
    LoadSearchDto.CustomerName = customer.Name)
```

This graph is persisted as canonical relation/query IR. The expression-based C# surface is the primary
developer UX and the structural surface is its unbounded escape hatch. Both lower through the same
`RelationQueryAuthoringCore`; neither owns a second query model.

### Expression-based C# authoring

`RelationQuery.Expression()` creates a typed authoring session. CLR expressions are inspected, never
compiled or executed, and immediately lower to canonical `Expr` values and structural operations. The
session discovers deterministic shape documents using the configured attributes and System.Text.Json
metadata. Explicit imported `ShapeGraphDocument` registrations and member-path overrides take precedence.
Each typed shape exposes `IdentityOrigin`, while `ResolveMemberPathWithProvenance` reports the origin of
every path segment as convention-, metadata-, explicit-, or import-derived. Imported documents are checked
against the CLR member type, cardinality, presence, nullability, and reachable named-type structure before
they can participate in a terminal.

A simple DTO relation needs no manually authored shape IDs, field paths, expressions, or node arrays:

```csharp
var author = RelationQuery.Expression();
var loads = author.Source<Load>();
var relation = author
    .Project(
        loads,
        (Load load) => new LoadDto
        {
            Id = load.Id,
            Status = load.Status
        })
    .BuildRelation((LoadDto document) => document.Id);
```

Relationship and terminal identities, display names, and provenance references are convention-derived unless an
explicit overload or optional override is used. The versioned relation convention hashes the root shape, output
shape, and output mode; multiple conceptual relations with the same endpoints use the optional `id` or the explicit
terminal overload. A minimal joined DTO relation is therefore:

```csharp
var author = RelationQuery.Expression();
var loads = author.Source<Load>();
var customers = author.Traverse<Load, Customer>(
    loads,
    load => load.CustomerId);

var relation = author
    .Project(
        customers,
        (Load load, Customer customer) => new LoadSearchDto
        {
            Id = load.Id,
            CustomerId = load.CustomerId,
            CustomerName = customer.Name,
            CustomerType = customer.Type
        })
    .BuildRelation((LoadSearchDto document) => document.Id);

var relationshipCatalog = author.CreateRelationshipCatalogDocument();
```

Inline traversal definitions are retained in the session's deterministic `RelationshipCatalog`; the document above
can be supplied directly to static compilation. A bound node carries its focused binding and, along the unary
`Source → Traverse → Project` path, its originating relation root. A one-parameter projection uses the focused
binding. A two-parameter projection without explicit bindings uses the root first and the focus second. For other
binding layouts, the focused-binding-last overload accepts the earlier bindings explicitly:

```csharp
var loadEquipment = author.Relationship<Load, Equipment>(load => load.EquipmentId);
var equipment = author.Traverse(customers, loads.Binding, loadEquipment);

var documents = author.Project(
    equipment,
    (Load load, Customer customer, Equipment unit) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerId = load.CustomerId,
        CustomerName = customer.Name,
        CustomerType = customer.Type,
        EquipmentNumber = unit.Number
    },
    loads.Binding,
    customers.Binding);
```

Branches assembled through joins or the structural escape hatch do not guess a relation root; pass the intended root
to the existing explicit `BuildRelation` overload. The same session can consequently author multiple enrichments,
correlated collection semantics, a row result, an aggregation, and a target-neutral typed evaluation:

```csharp
var author = RelationQuery.Expression();
var loadCustomer = author.Relationship<Load, Customer>(
    load => load.CustomerId);
var loadEquipment = author.Relationship<Load, Equipment>(
    load => load.EquipmentId);

var loads = author.Source<Load>();
var customers = author.Traverse(loads, loadCustomer);
var equipment = author.Traverse(customers, loads.Binding, loadEquipment);
var location = author.Parameter<string>("location");

var filtered = author.Filter(
    equipment.Node,
    (Load load, Customer customer, Equipment _) =>
        customer.Name == "Acme"
        && load.Stops.Any(stop => stop.Location == location.Value),
    loads.Binding,
    customers.Binding,
    equipment.Binding);

var documents = author.Project(
    filtered,
    (Load load, Customer customer, Equipment unit) => new LoadSearchDto
    {
        Id = load.Id,
        CustomerId = load.CustomerId,
        CustomerName = customer.Name,
        CustomerType = customer.Type,
        EquipmentNumber = unit.Number
    },
    loads.Binding,
    customers.Binding,
    equipment.Binding);

var summary = author.Aggregate(
    filtered,
    author.Clr.Shape<LoadSearchSummary>(),
    aggregate => aggregate
        .Group(
            result => result.CustomerType,
            (Customer customer) => customer.Type,
            customers.Binding)
        .Count(result => result.Count));

var rows = author.Rows(documents, id: "rows");
var aggregation = author.Aggregation(summary, id: "summary");
var query = author.BuildQuery(
    new QueryId("load-search"),
    new QueryName("LoadSearch"),
    rows,
    aggregation);

var evaluation = author.Evaluate(
        query,
        new RelationQueryEvaluationId("request/42"))
    .Set(location, "Seattle")
    .Select(rows, document => document.Id, document => document.CustomerName)
    .Select(aggregation)
    .Build();

var shapeDocuments = author.ShapeDocuments;
```

The `Any` predicate preserves same-element correlation through canonical current-item scope; it is not
flattened into independent collection tests. Typed parameters become declarations in the canonical query
and only receive values in the evaluation. Captured application state is rejected with a
`RelationQueryExpressionAuthoringException` containing stable diagnostics, expression paths, source
references, and suggested alternatives; arbitrary captured getters are never evaluated.

The translator accepts sequence syntax only where its CLR behavior identifies the canonical behavior exactly:
`Select(...).ToArray()` is an eager, order-preserving projection and predicate-free `LongCount()` has the
canonical Int64 count domain. An exact named enum member lowers to a portable typed enum literal, including
equality or inequality against an enum field. Same-type enum fields can also be compared directly. Unnamed flag
combinations and ambiguous enum aliases remain unsupported. A convention-inferred nullable CLR member,
whose semantic field has required presence, may be read inside a branch guarded by `HasValue` or an exact null
comparison. Compiler-synthesized record equality operators retain that null-guard meaning; arbitrary user-defined
operators remain unsupported. `??` over the same kind of member lowers to an explicit canonical conditional.
`DateTimeOffset.EqualsExact(...)` lowers to
canonical representation equality; ordinary `DateTimeOffset` equality remains unsupported because its instant-based
CLR semantics differ from canonical observation equality. Imported mappings remain fail-closed until
they can supply equivalent per-path presence evidence. Unguarded nullable navigation, Int32-returning
`Count`/`Length`, unmaterialized lazy
`Select`, custom constructors or
setters, and collection equality whose CLR contract differs from canonical equality still fail closed. Author
the intended behavior through `author.Structural` when one of those distinctions matters.

Keyed ordering, distinctness, grouping, and relation identity require a fixed carrier-independent scalar
domain. Raw `DateOnly`, `DateTime`, `DateTimeOffset`, `ObservationValue`, and dynamic JSON carriers are
rejected even when hidden inside a conditional or composite key; project them to an explicitly normalized
field first. The expression facade also rejects untyped whole-row distinctness because it cannot prove that
every visible field has portable equality. A rows result or relation terminal must receive a branch exposing
exactly one value binding, since canonical terminals persist the node and output shape rather than a selected
binding. Project joined, traversed, or expanded branches to one output shape before declaring the terminal.

`author.Structural` exposes the exact underlying core. Use it to add a canonical construct outside the
expression translator's supported closure, then continue with the resulting handles. This is an explicit
authoring escape hatch, not a separate semantic model.

```csharp
var structuralFilter = author.Structural.Filter(
    loads.Node,
    Expr.EndsWith(
        loads.Binding.Structural.Field("status"),
        Expr.Const("Ready")));

// Typed expression authoring can resume over the structural node.
var documents = author.Project(
    structuralFilter,
    (Load load) => new LoadDto { Id = load.Id, Status = load.Status },
    loads.Binding);
```

Expression and structural authoring stop at canonical definitions. An evaluation supplies parameters, relation
roots, and output demand; it still does not choose storage. Placement and adapter bindings attach physical
sources and target capabilities to a compiled plan, and an execution integration performs I/O. Those are
separate interpretations of the persisted canonical IR.

### Structural C# authoring

`RelationQuery.Structural()` creates the reusable construction core. It returns typed handles for nodes,
bindings, parameters, and named results; uses deterministic identities when an override is omitted; records
the origin of every identity and producer-attributed decision; and runs the canonical validator at relation
or query terminals. A terminal snapshots the current core, so the same derivation can first produce a rooted
relation and then be extended into an independently evaluated query without changing the earlier result.

The following example flattens customer and equipment data into a load search DTO, exposes the derivation as
a rooted relation, and then adds a status-filtered query and target-neutral evaluation:

```csharp
var domain = new GraphId("example/domain/v1");
var search = new GraphId("example/search/v1");
var loadShape = new QualifiedShapeId(domain, new("Load"));
var customerShape = new QualifiedShapeId(domain, new("Customer"));
var equipmentShape = new QualifiedShapeId(domain, new("Equipment"));
var loadSearchShape = new QualifiedShapeId(search, new("LoadSearchDto"));

var loadCustomer = Relationship
    .From(loadShape)
    .Reference(FieldPath.FromField("CustomerId"))
    .To(customerShape);
var loadEquipment = Relationship
    .From(loadShape)
    .Reference(FieldPath.FromField("EquipmentId"))
    .To(equipmentShape);

var author = RelationQuery.Structural();
var loads = author.Source(loadShape);
var customers = author.Traverse(loads.Node, loads.Binding, loadCustomer.Id);
var equipment = author.Traverse(customers.Node, loads.Binding, loadEquipment.Id);
var documents = author.Project(
    equipment.Node,
    loadSearchShape,
    [
        new(FieldPath.FromField("Id"), loads.Binding.Field("Id")),
        new(FieldPath.FromField("Status"), loads.Binding.Field("Status")),
        new(FieldPath.FromField("CustomerName"), customers.Binding.Field("Name")),
        new(FieldPath.FromField("EquipmentNumber"), equipment.Binding.Field("Number"))
    ]);

var relation = author.BuildRelation(
    new RelationId("load-search-document"),
    new RelationName("LoadSearchDocument"),
    loads.Binding,
    documents.Node,
    loadSearchShape,
    RelationOutputMode.OnePerRoot,
    key: documents.Binding.Field("Id"));

var status = author.Parameter(
    new ScalarTypeRef(ScalarTypeKind.String),
    id: new QueryParameterId("status"));
var filtered = author.Filter(
    documents.Node,
    Expr.Eq(documents.Binding.Field("Status"), status.Expression));
var rows = author.Rows(filtered, id: new QueryResultId("rows"));
var query = author.BuildQuery(
    new QueryId("loads-by-status"),
    new QueryName("LoadsByStatus"),
    [rows]);

var evaluation = query.CreateDocument()
    .Evaluate(new RelationQueryEvaluationId("request/42"))
    .Set(status.Id, ObservationValue.FromString("InTransit"))
    .Select(rows.Id)
    .Build();
```

`relation.Validation` and `query.Validation` contain the authoritative structured diagnostics. The
evaluation retains the exact definition, shape and relationship snapshots, evaluation identity, parameter evidence,
supplied roots, output demand, and optional compiled-plan attribution; it does not select an adapter, placement, or
execution engine. Pass persisted shape and relationship documents to `Evaluate` when using the structural frontend
directly; `RelationQueryExpressionAuthoring.Evaluate` supplies its session snapshots by convention.
The complete executable version is in `RelationQueryStructuralAuthoringExampleTests`.

### Canonical evaluation and host execution

`RelationQueryEvaluation` is the common runtime request for relations and queries. Its canonical definition
determines whether the request evaluates a rooted correspondence or independently acquires query inputs; there is no
second kind enum or parallel execution model. The request carries the exact `RelationQueryCompilationRequest`,
parameter evidence, optional supplied roots, output demand, and optional compiled-plan attribution.
The normalized request is a portable `relation-query-evaluation/v3` document with a deterministic fingerprint over
the complete compilation snapshots, demand and its origin, evaluation identity, parameter and root evidence,
provenance references, and optional plan attribution. Use `RelationQueryEvaluationJsonSerializer` for strict
round trips and `HasSameSemantics` when a host must compare independently reconstructed requests.

For expression-authored relations, the common DTO-enrichment case is concise:

```csharp
var evaluation = author.Evaluate(
        loadSearchRelation,
        new RelationQueryEvaluationId("index/load/load-42"))
    .Supply(
        new[] { load },
        static value => value.Id,
        evidenceReference: "change-feed/9381")
    .Build();
```

`Supply` accepts validated core `Observation` values with an identity selector, `EntityObservationSnapshot` values,
portable `RelationQuerySuppliedRoot` evidence, or CLR values projected and validated through the exact root graph.
It does not copy entity versions or relation lineage into root evidence. Omitted roots mean no root evidence was
supplied. An explicitly typed empty root collection is intentionally different: it is a known empty root set, with
complete or partial evidence selected by the caller.

Hosts depend on one boundary:

```csharp
IRelationQueryEvaluator evaluator = new RelationQueryEvaluator(
    plan => placementCatalog.Resolve(plan),
    physicalPlanningPolicy,
    sourceReaders);

RelationQueryEvaluationOutcome outcome = await evaluator.EvaluateAsync(
    evaluation,
    cancellationToken);
```

The reference evaluator performs static compilation, capability realization, physical planning, bounded source
acquisition, and canonical interpretation. `RelationQueryEvaluationOutcome` retains those exact phase artifacts and
exposes the existing `RelationQueryExecutionResult`; it does not copy rows, aggregations, requirement gaps,
diagnostics, provenance, or source-read traces into another hierarchy. Compilation, realization, and planning
failures remain inspectable in their respective artifacts. Missing related observations appear through
`outcome.Result.RequirementGapAnalysis` when interpretation runs.

The outcome and physical execution result are in-process composites, not durable wire contracts. API endpoints map
an outcome explicitly to their response contract. Durable process definitions use the `Evaluate(evaluation,
projectResult)` overload, which projects canonical rows, aggregations, gaps, and diagnostics to a declared application
DTO in the same node before checkpoint capture; Process authoring intentionally has no overload that checkpoints the
complete outcome. In-memory host code may inspect the outcome directly at the evaluator boundary.

### Compiled mapper

For a successful static relation plan, `RelationDtoMapperCompiler` compiles its supported canonical output
terminal into a fast CLR materialization kernel. Mapping consumes canonical execution rows and retains their
status, diagnostics, gaps, and provenance; it does not acquire related data or execute a second relation model.

### PostgreSQL projection

When both facts live in one PostgreSQL execution domain, `Cohesive.Adapters.Postgres` can lower the same graph to a
native query such as:

```sql
SELECT
    l.id AS load_id,
    l.status,
    c.name AS customer_name
FROM loads AS l
LEFT JOIN customers AS c
    ON c.id = l.customer_id;
```

PostgreSQL compilation is an adapter interpretation with retained capability evidence and provenance; it is not part
of the core IR contract. See the executable
[PostgreSQL native join versus Cosmos composed reads](../EXECUTION_AND_ADAPTERS.md#postgresql-native-join-versus-cosmos-composed-reads)
comparison for the exact placement boundary.

The same adapter binding also drives `PostgresRelationQuerySourceReader` for Npgsql-backed bounded enumeration,
identity batches, and relationship-key predicate batches in composed physical plans. That source-reader path is
distinct from automatic execution of a native compiled artifact. `PostgresMaterializationSource` reuses the reader for
bounded rebuild/reconciliation pages with keyset continuation and explicit per-statement, rather than cross-page,
snapshot semantics.

### Federated acquisition

When loads and customers do not share a native query boundary, the canonical physical planner can select a
bounded acquisition strategy within its supported closure:

```text
Read loads
→ collect distinct CustomerIds
→ batch-read customers
→ hash join in memory
→ project LoadSearchDto
```

This is physical planning and source acquisition, not the removed prototype hydration API. Source readers
return explicit completeness and field evidence; the canonical interpreter determines the semantic result.

### Dependency analysis, not materialization

Static compilation can describe which inputs influence a derived search document:

```text
LoadSearchDto
├── depends on Load
└── depends on Customer through Load.CustomerId
```

`Cohesive.Relations` produces the semantic relation, lineage, requirements, and dependency manifest. It does
not own index rebuilds, real-time synchronization, checkpoints, target writes, or operational scheduling.
Those responsibilities belong to the separate Storage/materialization workstream. Industrial batching,
parallelism, throttling, and retry policy belong to the planned `Cohesive.Control` workstream. Those systems
may consume Relations artifacts without moving their operational semantics into the relation IR.

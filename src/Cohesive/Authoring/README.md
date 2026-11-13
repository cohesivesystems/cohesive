# Shared C# authoring

The `Cohesive.Authoring` namespace in the core `Cohesive` project owns typed expression
bindings, CLR shape mapping, exact expression lowering, projection assignments and
lowering diagnostics. No separate authoring project or package is required.
Relations and Assurance consume this compiler; neither owns another copy of its rules.

```csharp
var author = new ExpressionAuthoring();
var stock = author.Input<Inventory>("stock");
// Assurance:
var claim = declaration
    .AtEveryCommittedState(population, InitialStateSelection.Include)
    .Require(stock, ClaimInputRole.State, s => s.Reserved <= s.OnHand)
    .Build();
```

Inputs are declarations, not reads or queries. Automatic IDs follow declaration order;
explicit IDs keep binding identity stable when declarations move. A lambda's bindings
must belong to one session and match its parameter types and order. The session is
single-threaded authoring state; its CLR metadata context maintains synchronized caches.
Use `author.Clr.Shape<T>(document, shapeId)` and `author.Input(shape, "stock")` for an
imported authoritative shape rather than inferring another one.

Relations keeps its `RelationQuery.Expression()` surface and query-specific bindings,
visibility checks, parameters and traversal. Those bindings derive from the shared
`ExpressionBinding` and expose `IExpressionInput<T>`. Query projections adapt the shared
field assignments into query assignments where query-specific IDs are attached.
Core friend access lets Relations reuse mapping and marker machinery without making
those construction seams public or admitting arbitrary captured parameter getters.

The compiler never compiles expression trees, executes arbitrary methods or property
getters, or captures host state as portable constants. Unsupported constructs retain
structured diagnostics. CLR metadata and ownership handles exist only during authoring;
canonical `Expr` and shape contracts remain the semantic authority. This is build-time
work, with no change to per-event execution paths or claimed runtime performance gain.

## Migration

- `RelationQueryClrAuthoringContext` becomes `ClrAuthoringContext` in `Cohesive.Authoring`;
  the related profile, shape and identity-origin types have the same neutral prefix.
- `RelationQueryExpressionLowerer` becomes `ExpressionLowerer`; its result, diagnostic
  and exception types also lose the domain prefix.
- `RelationQueryAuthoringSource` becomes `AuthoringSource`.
- Assurance's temporary `Cohesive.Assurance.Relations` bridge is removed. Typed claim
  extensions are in `Cohesive.Assurance`, using `ExpressionAuthoring.Input<T>()`.

This is a source API change. The shared expression producer is now
`cohesive.csharp-expression/v1`, and diagnostic codes use `authoring.expression.*`.
Query-level producer attribution remains domain-specific. Existing CLR profile IDs,
shape conventions and portable-value diagnostic IDs are deliberately retained so moving
implementation ownership does not rename existing model identities. Canonical expression
semantics remain unchanged; source provenance fingerprints can change with the producer.

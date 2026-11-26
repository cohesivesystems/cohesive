# Cross-database migrations with Cohesive.Relations

Design proposal for discussion. The first milestone is an offline, independently verifiable migration between different database engines.

## The semantic contract

Given an exact source schema revision and an admitted instance at a declared source cut, a data migration specifies the target instance that should exist at an exact target revision. A schema revision here includes the shapes, identities, relationships, and constraints used by the mapping. A placement binds those logical objects to databases. Moving an unchanged schema between engines is the identity migration with different placements. Reshaping data inside one database is the same compilation problem with colocated placements. A compiler may use DDL, server-side queries, or a staged transfer where the selected strategy preserves the contract.

The proposed authoring unit is a versioned schema mapping plus a choice of how to transport instances along it. Keep the mapping independently referenceable so several transports can use it. A typed builder should select entities, paths, and fields against pinned contracts. Lower it deterministically to a mapping document that references graph-qualified identities and relationship IDs. Persist source attribution, referenced fingerprints, and the lowering version. A structural authoring surface or an importer should produce equivalent source and lower through the same checks.

## What the categorical interpretation buys us

For a mapping F from schema A to schema B, each object in A maps to an object in B. Each generating arrow in A maps to a well-typed path in B, possibly an identity path. Substitution must preserve A's path equations under B's equations. In the basic set-valued model an instance assigns a set of identities to each object and a total function to each arrow. One mapping then determines three operations. [Spivak and Wisnesky, Relational Foundations for Functorial Data Migration](https://arxiv.org/abs/1212.5303)

| Operation | Instance direction | Meaning |
| --- | --- | --- |
| Pullback ΔF | B → A | Evaluate each mapped object and path in the given B instance. |
| Left pushforward ΣF | A → B | Freely extend the instance along F, identifying elements as required by the mapping and equations. |
| Right pushforward ΠF | A → B | At each target object, form compatible families of source elements indexed by paths from that object into the image of F. |

The adjunctions ΣF ⊣ ΔF ⊣ ΠF characterize valid implementations. They also identify which comparisons are available after a round trip. For example, the unit maps each source element into its pullback after a left pushforward. Two elements can acquire the same image, so rollback requires independent evidence of invertibility or a retained source. For typed attributes, use a shared scalar theory with declared operations and equality. The algebraic extension gives a foundation for that choice. [Schultz and Wisnesky, Algebraic Data Integration](https://arxiv.org/abs/1503.03571)

A small example distinguishes all three operations. Let A have two unrelated entity types, RetailAccount and TradeAccount, and let B have one Account type. F sends both types to Account. Δ presents the same B account population in each A role. Σ produces the tagged disjoint union of the two A populations. Π produces every retail/trade pair. Two retail accounts and three trade accounts therefore yield five outputs under Σ and six under Π. Overlapping account numbers alone establish no identification. Merging by account number needs a declared identification rule with business authority.

## First cross-store example

Suppose PostgreSQL holds Order, Customer, and Region, with total references Order.customer and Customer.region. A Cosmos destination needs one OrderSummary document per order, with its order ID and the region code. Define F **from the summary schema to the operational schema**. Map OrderSummary to Order, its identity to the order identity, and its regionCode attribute to the path Order.customer.region.code. Applying ΔF moves instances **from PostgreSQL to Cosmos**. Two orders sharing a customer still yield two documents. Customer and Region need no destination collections. A document partition key can be derived from the mapped order ID under the placement policy.

The relation compiler can implement this pullback with two traversals and a projection. The authoritative mapping supplies the path and identity, and the compiler emits the relation graph with provenance to those clauses. Moving the destination to a second SQL database should change the binding and execution plan while preserving the expected keyed outputs. General migrations can also designate an independently authored canonical relation per target extent when business semantics require filters, aggregation, or deduplication. Such a declaration must specify the output identity, multiplicity, and target-reference obligations. Claim a categorical interpretation only for operators whose laws the compiler can establish.

### Typed authoring and the derived relation

The following C# sketches a proposed API. `SummaryV1` and `SalesV3` stand for application-owned typed projections of exact schema contracts. Both declare which string field represents the observation identity. `SalesV3.OrderCustomer` and `SalesV3.CustomerRegion` expose existing typed relationship handles over the pinned catalog. The names PostgreSQL and Cosmos enter only when binding a compiled migration to stores.

```csharp
var mapping = SchemaMapping.Define(
    id: "order-summary-over-sales",
    domain: SummaryV1.Schema,
    codomain: SalesV3.Schema,
    define: map => map.Entity(SummaryV1.OrderSummary, SalesV3.Order, (summary, order) =>
    {
        var region = order.Via(SalesV3.OrderCustomer).Via(SalesV3.CustomerRegion);
        summary.Field(s => s.RegionCode, region.Field(r => r.Code));
    }));

var pullback = mapping.Pullback();
```

`Entity` maps one domain extent to one codomain extent. For this identity-preserving fragment, interpreting that object clause under Δ takes the entire order population with its existing identities. The compiler projects the order identity into the summary identity field using the two pinned identity contracts. If either contract lacks the field-to-identity binding, compilation reports that missing binding. A property named `Id` alone supplies no such evidence. The author writes only the additional attribute assignment.

At the call site, `order` is a path rooted at Order. On a path ending at `T`, `Via<TNext>` takes the existing `RelationQueryExpressionRelationship<T, TNext>` and returns a path with the same root ending at `TNext`. Thus `order.Via(SalesV3.CustomerRegion)` fails C# type checking. The terminal `Field` accepts a member-selector expression on the endpoint. The destination selector and the resulting path value share a CLR value type, and lowering also checks exact shape membership, scalar semantics, requiredness, and catalog revision. The builder rejects foreign-root paths, duplicate assignments (including an explicit assignment to the convention-bound identity), and uncovered required target fields. All callbacks execute during authoring. The materialized mapping contains typed field paths, relationship IDs in traversal order, and exact contract references.

For this mapping, Δ derives one existing `RelationDefinition`. The table uses short binding names for readability. Actual node, binding, and assignment IDs come from the structural authoring conventions. The compiler walks mapping clauses in canonical order and shares identical path prefixes from the same root.

| Step | Existing IR | Arguments for this example |
| --- | --- | --- |
| 1 | `SourceQueryNode` | Shape `SalesV3.Order`, binding `o` |
| 2 | `TraverseRelationshipQueryNode` | Input 1, from `o`, relationship `OrderCustomer`, forward, result `c`, left, required |
| 3 | `TraverseRelationshipQueryNode` | Input 2, from **`c`**, relationship `CustomerRegion`, forward, result `r`, left, required |
| 4 | `ProjectQueryNode` | Input 3, shape `SummaryV1.OrderSummary`, result `s`. Assign `Id ← Expr.Field(o, orderIdPath)` and `RegionCode ← Expr.Field(r, regionCodePath)` |
| Terminal | `RelationDefinition` | Root binding `o`. Output node 4, shape `OrderSummary`, mode `OnePerRoot`, key `Expr.Field(s, summaryIdPath)` |

Here `regionCodePath` is the resolved path for **Region.Code**. The projection reads two simultaneously visible bindings. The target key reads the **projected** summary identity. Emit through the existing structural `Source`, `Traverse`, `Project`, and `BuildRelation` operations, as in the federated conformance fixture. Retain a derivation from the mapping clauses to the emitted nodes and assignments so an unresolved CustomerRegion can be attributed to the RegionCode clause. [Structural lowering fixture](../../src/Cohesive.Relations.TestFixtures/FederatedLoadExpressionAuthoringFixture.cs)

Left traversal retains evidence of an unmatched order and required resolution makes the missing reference actionable. Migration acceptance additionally requires complete root enumeration at the pinned cut, exactly one resolved output for each root, and equality of its key to that root's admitted identity. A partial outcome or a substituted default cannot discharge the path obligation. For orders O17 and O18 sharing a customer in region WEST, the expected keys are O17 and O18 and both values carry WEST. Removing that region at the test cut must fail acceptance with the affected orders attributable to the missing path. The logical graph ends at the keyed summary. Cosmos document ID encoding, partition key, and writes are decisions of the target binding.

## Admission and ownership

- Core shapes and the Relations relationship catalog own field types, presence, reference paths, and direction. Derive the categorical presentation from these contracts plus explicit equations and totality evidence. A required reference field can still dangle. Classify each obligation as established by the schema, checked at this source cut, or unresolved. A failed check identifies the clause, object identity, and path. An unresolved obligation blocks that interpretation. [RelationshipDefinition](../../src/Cohesive.Relations/Model/RelationshipDefinition.cs)
- Relations owns mapping validation and expected target data. Start with finite acyclic entity paths, total single-valued references, identity preservation, and exact scalar projection. Prove path equality by syntactic equality after bounded normalization using admitted equations. Report unsupported equations explicitly. Optional references, bag multiplicity, collection order, scalar conversions, and collation each require a declared interpretation before admission. Reuse the existing canonical query IR and its reference evaluator for execution. [Relation IR](../../src/Cohesive.Relations/IR)
- Storage owns materialization policy and target capabilities. Reuse generation identity, coordinated snapshot or baseline-plus-catch-up, idempotency, ready barriers, and verification where their contracts apply. Extend target bindings when a writable entity store requires import-specific guarantees, such as preserving entity identity or bypassing ordinary creation effects under an explicit import contract. A materialized read model and an authoritative entity store have different write obligations. [Materialization contracts](../../src/Cohesive.Storage/Materialization/MaterializationDefinition.cs)
- Application deployment owns changes in read routing and write authority. An existing atomic routing manifest may cover participating readers. Transferring write ownership requires a separately evidenced writer fence, disposition of in-flight writes, and a final source position. Independent snapshots from multiple databases support only the consistency claim justified by their coordination protocol.

## Execution and verification

For the first slice, take a repeatable source cut, target a fresh isolated generation, and keep the source authoritative. Compile the mapping and bindings before writing. Fail planning if the requested cut, complete enumeration, identity encoding, or isolated target writes cannot be realized. Record the mapping fingerprint, bound plan, source cut, target generation, and checkpoint. Retry deterministic writes to the same generation and advance the checkpoint only after durable acknowledgement. If the cut cannot be reopened after interruption, start a fresh generation at a new cut.

Compute expected keyed outputs through the reference interpretation and independently read the candidate. Compare identities and canonical values, check target constraints across all extents, and reject incomplete reads, collisions, or unresolved references. Row counts alone would miss an order assigned the wrong region. A verification receipt binds to the exact cut, plan, and generation. The existing bounded shadow comparison is a starting point for small populations. Larger scopes need a verified partitioned comparison before making the same claim. [Shadow verification](../../src/Cohesive.Storage/Materialization/MaterializationShadowComparison.cs)

The acceptance fixture should include two orders sharing one customer, two regions with distinct codes, and deliberately conflicting or dangling inputs. Compare a hand-specified expected instance, a reference evaluation, and a destination readback. Inject interruption immediately after a durable write and before checkpoint advancement. Verify identical keyed output after retry. Compare identity and composed pullbacks on small fixtures and compare expression and structural lowering fingerprints. Failure to establish a total path must prevent a successful receipt even when a physical inner join would quietly drop the offending order.

## Sequence and open decisions

1. Deliver the pullback fragment and one PostgreSQL-to-Cosmos shadow migration. Exercise path substitution, identity preservation, and a verifiable result across engines. Keep the first public API small until this fixture establishes the binding requirements.
2. Add finite coproducts for discrete Σ and finite products for discrete Π, using the account fixture above. Specify deterministic representations of tagged and tuple identities. Add quotients and constrained joins only with reference tests for their universal properties. Before broader Kan extension support, establish finite computability and explicit resource bounds for the admitted schema fragment.
3. Qualify live migration and authoritative cutover independently. A source change feed, an exact baseline boundary, delete propagation, and a writer handoff protocol are additional requirements. After destination writes begin, recovery needs reverse propagation, replay, or an explicitly accepted loss boundary.

The open architecture decision is where reusable path equations and totality guarantees belong. Start by attaching scoped obligations to the mapping and citing their evidence. Promote a law into the shared schema contract when the domain owner intends it to constrain every admitted instance. A successful scan can substantiate an individual migration without elevating an accidental property of today's data into a permanent schema law.

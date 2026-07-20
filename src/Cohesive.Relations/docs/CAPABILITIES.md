# Relation/Query Capability Reference

A target profile is evidence, not a marketing checklist. It declares exact canonical capabilities, guarantees, and
operating boundaries. Static compilation projects demand-scoped requirements; the realization compiler then matches
those requirements against a selected profile and policy. Exact placement and adapter bindings qualify that family-
level answer with contextual physical evidence.

## Reading realization decisions

Every demanded requirement receives one decision:

| Decision | Meaning |
| --- | --- |
| `Native` | One target capability directly proves the requirement and its guarantees. |
| `Composed` | A declared composition of facilities proves the requirement and preserves its guarantees. |
| `Constrained` | The strategy is valid only after its named operating boundaries are validated. |
| `Override` | An explicit, attributable override supplies the required proof. |
| `Unavailable` | No policy-permitted strategy proves the requirement without weakening semantics. |

These are demand-scoped outcomes, not permanent labels attached to an adapter. For example, PostgreSQL can natively
join co-located tables, while the same semantic traversal is unavailable when its table placements identify different
execution domains. Cosmos SQL can natively interpret supported single-container branches, while a relationship
between separately stored documents requires the composed source-acquisition path.

## Generated target-profile inventory

The block below is generated from the actual profiles through
`RelationQueryCapabilitySummaryProjector.Project(profile)`. It intentionally separates the Cosmos SQL compiler
profile from the Cosmos entity-source acquisition profile. The deterministic currentness test fails when a profile
changes without regenerating this artifact.

<!-- generated-capability-profiles:start -->
### In-memory reference

- Target: `cohesive.relations.in-memory`
- Profile: `cohesive.relations.in-memory/realization-v2`
- Capability evidence: 159
- Definition schemas: `relation-query/v1`
- Compiler profiles: `relation-query-static/v1`
- Full-profile SHA-256: `d110aee136f26ef8f94536f52770bdac6e39a8bfbabf9b512b832b228ba073ea`
- Families: expression=46, guarantee=24, logical=48, structural=25, temporal=16

- Logical semantics: `AggregateFilter`, `AggregateGrouping`, `Aggregation`, `AllAggregate`, `AlwaysPresentBinding`, `AnyAggregate`, `AscendingOrdering`, `AtMostOneRelationshipTraversal`, `AverageAggregate`, `CountAggregate`, `DescendingOrdering`, `DistinctKeys`, `DistinctRows`, `ExpandCollection`, `Filter`, `ForwardRelationshipTraversal`, `FullOuterJoin`, `InnerJoin`, `InverseRelationshipTraversal`, `Join`, `KeysetPaging`, `LeftOuterJoin`, `ManyPerRootRelationOutput`, `ManyRelationshipTraversal`, `MaximumAggregate`, `MayBeAbsentBinding`, `MinimumAggregate`, `NullsFirst`, `NullsLast`, `OffsetPaging`, `OnePerRootRelationOutput`, `OptionalRelationshipTraversal`, `Ordering`, `Projection`, `ProjectionAssignment`, `QueryAggregationResult`, `QueryRowsResult`, `RelationInvariant`, `RelationOutputIdentity`, `RelationshipTraversal`, `RequiredRelationshipTraversal`, `RightOuterJoin`, `SetRelationOutput`, `Source`, `StableTieOrdering`, `SumAggregate`, `TemporalJoin`, `ZeroOrOnePerRootRelationOutput`
- Expression semantics: `Operation:expr.function.all`, `Operation:expr.function.any`, `Operation:expr.function.append`, `Operation:expr.function.appendRange`, `Operation:expr.function.avg`, `Operation:expr.function.concat`, `Operation:expr.function.contains`, `Operation:expr.function.count`, `Operation:expr.function.endsWith`, `Operation:expr.function.insertAt`, `Operation:expr.function.insertRangeAt`, `Operation:expr.function.max`, `Operation:expr.function.min`, `Operation:expr.function.object`, `Operation:expr.function.select`, `Operation:expr.function.startsWith`, `Operation:expr.function.sum`, `Operation:expr.function.textContains`, `Operation:expr.node.conditional`, `Operation:expr.node.constant`, `Operation:expr.node.currentItem`, `Operation:expr.node.field`, `Operation:expr.node.field.nestedPath`, `Operation:expr.node.parameter`, `Operation:expr.node.typedField`, `Operation:expr.node.typedLiteral`, `Operation:expr.operator.aggregate.all`, `Operation:expr.operator.aggregate.any`, `Operation:expr.operator.aggregate.average`, `Operation:expr.operator.aggregate.count`, `Operation:expr.operator.aggregate.max`, `Operation:expr.operator.aggregate.min`, `Operation:expr.operator.aggregate.sum`, `Operation:expr.operator.binary.add`, `Operation:expr.operator.binary.and`, `Operation:expr.operator.binary.div`, `Operation:expr.operator.binary.eq`, `Operation:expr.operator.binary.ge`, `Operation:expr.operator.binary.gt`, `Operation:expr.operator.binary.le`, `Operation:expr.operator.binary.lt`, `Operation:expr.operator.binary.mul`, `Operation:expr.operator.binary.ne`, `Operation:expr.operator.binary.or`, `Operation:expr.operator.binary.sub`, `Operation:expr.operator.unary.not`
- Structural paths: `AggregateTarget:NestedField`, `AggregateTarget:RootValue`, `AggregateTarget:TopLevelField`, `BindingRead:NestedField`, `BindingRead:RootValue`, `BindingRead:TopLevelField`, `CompleteValue:NestedField`, `CompleteValue:RootValue`, `CompleteValue:TopLevelField`, `CurrentItemRead:CollectionElement`, `CurrentItemRead:NestedField`, `CurrentItemRead:RootValue`, `CurrentItemRead:TopLevelField`, `GroupingTarget:NestedField`, `GroupingTarget:RootValue`, `GroupingTarget:TopLevelField`, `OccurrenceEvidenceReconstruction:NestedField`, `OccurrenceEvidenceReconstruction:RootValue`, `OccurrenceEvidenceReconstruction:TopLevelField`, `OutputSelection:NestedField`, `OutputSelection:RootValue`, `OutputSelection:TopLevelField`, `ProjectionTarget:NestedField`, `ProjectionTarget:RootValue`, `ProjectionTarget:TopLevelField`
- Preserved guarantees: `AbsenceAvailabilityFailureDistinction`, `Aggregation`, `Cardinality`, `CollectionElementCorrelation`, `DeterministicResult`, `DuplicateHandling`, `EvidenceCompleteness`, `Grouping`, `InconclusiveEvidence`, `InvariantEnforcement`, `JoinMembership`, `MissingNullDistinction`, `NullPlacement`, `OccurrenceProvenance`, `Ordering`, `OutputIdentity`, `OutputMode`, `RelationRootCorrelation`, `RelationshipDirection`, `RelationshipMultiplicity`, `StablePaging`, `TemporalBoundary`, `TemporalDomain`, `UnboundedTemporalBoundary`
- Temporal semantics: `DateDomain`, `DateTimeDomain`, `ExclusiveBoundary`, `FullOuterJoin`, `InclusiveBoundary`, `InconclusiveEvidence`, `InnerJoin`, `InstantDomain`, `IntervalOverlap`, `LeftOuterJoin`, `NullAsUnbounded`, `PointInInterval`, `PreserveAllMatches`, `RightOuterJoin`, `UnboundedBoundary`, `ValidateIntervals`
- Operating boundaries: none declared by this profile.

### Cosmos SQL

- Target: `cohesive.adapters.cosmos.sql`
- Profile: `cohesive.adapters.cosmos.sql/canonical-v2`
- Capability evidence: 80
- Definition schemas: `relation-query/v1`
- Compiler profiles: `relation-query-static/v1`
- Full-profile SHA-256: `0727009663c60635749780993f555cdf113a11f1b243d3e56c3d9a52670e8451`
- Families: boundary validation=6, expression=19, guarantee=15, logical=21, structural=19

- Logical semantics: `AggregateGrouping`, `Aggregation`, `AlwaysPresentBinding`, `AscendingOrdering`, `CountAggregate`, `DescendingOrdering`, `DistinctRows`, `ExpandCollection`, `Filter`, `MaximumAggregate`, `MinimumAggregate`, `NullsFirst`, `NullsLast`, `OffsetPaging`, `Ordering`, `Projection`, `ProjectionAssignment`, `QueryAggregationResult`, `QueryRowsResult`, `Source`, `StableTieOrdering`
- Expression semantics: `Operation:expr.function.any`, `Operation:expr.function.contains`, `Operation:expr.node.conditional`, `Operation:expr.node.constant`, `Operation:expr.node.currentItem`, `Operation:expr.node.field`, `Operation:expr.node.field.nestedPath`, `Operation:expr.node.parameter`, `Operation:expr.node.typedField`, `Operation:expr.node.typedLiteral`, `Operation:expr.operator.binary.and`, `Operation:expr.operator.binary.eq`, `Operation:expr.operator.binary.ge`, `Operation:expr.operator.binary.gt`, `Operation:expr.operator.binary.le`, `Operation:expr.operator.binary.lt`, `Operation:expr.operator.binary.ne`, `Operation:expr.operator.binary.or`, `Operation:expr.operator.unary.not`
- Structural paths: `AggregateTarget:NestedField`, `AggregateTarget:RootValue`, `AggregateTarget:TopLevelField`, `BindingRead:NestedField`, `BindingRead:RootValue`, `BindingRead:TopLevelField`, `CompleteValue:NestedField`, `CompleteValue:RootValue`, `CompleteValue:TopLevelField`, `CurrentItemRead:CollectionElement`, `GroupingTarget:NestedField`, `GroupingTarget:RootValue`, `GroupingTarget:TopLevelField`, `OutputSelection:NestedField`, `OutputSelection:RootValue`, `OutputSelection:TopLevelField`, `ProjectionTarget:NestedField`, `ProjectionTarget:RootValue`, `ProjectionTarget:TopLevelField`
- Preserved guarantees: `AbsenceAvailabilityFailureDistinction`, `Aggregation`, `Cardinality`, `CollectionElementCorrelation`, `DeterministicResult`, `DuplicateHandling`, `EvidenceCompleteness`, `Grouping`, `InconclusiveEvidence`, `MissingNullDistinction`, `NullPlacement`, `Ordering`, `OutputIdentity`, `OutputMode`, `StablePaging`
- Enforced boundaries: `cosmos/boundary/exact-count-input-rows`, `cosmos/boundary/max-page-size`, `cosmos/boundary/non-null-operands`, `cosmos/boundary/scalar-operands`, `cosmos/boundary/single-source`, `cosmos/boundary/stable-unique-ordering`
- Operating boundaries:
  - `cosmos/boundary/exact-count-input-rows`: MaximumInputRows = 9007199254740991
  - `cosmos/boundary/max-page-size`: MaximumPageSize = 1000
  - `cosmos/boundary/non-null-operands`: NonNullOperands
  - `cosmos/boundary/scalar-operands`: ScalarOperands
  - `cosmos/boundary/single-source`: SingleSource
  - `cosmos/boundary/stable-unique-ordering`: StableUniqueOrdering

### Cosmos entity source

- Target: `cohesive.adapters.cosmos.entity-source`
- Profile: `cohesive.adapters.cosmos.entity-source/v1`
- Capability evidence: 6
- Definition schemas: `relation-query/v1`
- Compiler profiles: `relation-query-static/v1`
- Full-profile SHA-256: `c6b979291e4151ce4f7a7eaac5b66fa1b07dec41af5deddce27e661eeed9ddcb`
- Families: primitive=6

- Primitive facilities: `BatchedKeyLookup`, `BatchedPredicateLookup`, `CompleteSetEnumeration`, `FieldProjection`, `ObservationIdentityRead`, `RelationshipReferenceRead`
- Operating boundaries: none declared by this profile.

### Elasticsearch

- Target: `cohesive.adapters.elastic.search`
- Profile: `cohesive.adapters.elastic.search/canonical-v2`
- Capability evidence: 74
- Definition schemas: `relation-query/v1`
- Compiler profiles: `relation-query-static/v1`
- Full-profile SHA-256: `937c4aa6c960cfe24770c33d4b3936ce99043dcf38586326b209174f1faa19bc`
- Families: boundary validation=6, expression=19, guarantee=12, logical=18, structural=19

- Logical semantics: `AggregateGrouping`, `Aggregation`, `AlwaysPresentBinding`, `AscendingOrdering`, `CountAggregate`, `DescendingOrdering`, `Filter`, `KeysetPaging`, `NullsFirst`, `NullsLast`, `OffsetPaging`, `Ordering`, `Projection`, `ProjectionAssignment`, `QueryAggregationResult`, `QueryRowsResult`, `Source`, `StableTieOrdering`
- Expression semantics: `Operation:expr.function.any`, `Operation:expr.function.contains`, `Operation:expr.function.endsWith`, `Operation:expr.node.constant`, `Operation:expr.node.currentItem`, `Operation:expr.node.field`, `Operation:expr.node.field.nestedPath`, `Operation:expr.node.parameter`, `Operation:expr.node.typedField`, `Operation:expr.node.typedLiteral`, `Operation:expr.operator.binary.and`, `Operation:expr.operator.binary.eq`, `Operation:expr.operator.binary.ge`, `Operation:expr.operator.binary.gt`, `Operation:expr.operator.binary.le`, `Operation:expr.operator.binary.lt`, `Operation:expr.operator.binary.ne`, `Operation:expr.operator.binary.or`, `Operation:expr.operator.unary.not`
- Structural paths: `AggregateTarget:NestedField`, `AggregateTarget:RootValue`, `AggregateTarget:TopLevelField`, `BindingRead:NestedField`, `BindingRead:RootValue`, `BindingRead:TopLevelField`, `CompleteValue:NestedField`, `CompleteValue:RootValue`, `CompleteValue:TopLevelField`, `CurrentItemRead:CollectionElement`, `GroupingTarget:NestedField`, `GroupingTarget:RootValue`, `GroupingTarget:TopLevelField`, `OutputSelection:NestedField`, `OutputSelection:RootValue`, `OutputSelection:TopLevelField`, `ProjectionTarget:NestedField`, `ProjectionTarget:RootValue`, `ProjectionTarget:TopLevelField`
- Preserved guarantees: `AbsenceAvailabilityFailureDistinction`, `Aggregation`, `Cardinality`, `CollectionElementCorrelation`, `DeterministicResult`, `EvidenceCompleteness`, `Grouping`, `InconclusiveEvidence`, `MissingNullDistinction`, `NullPlacement`, `Ordering`, `StablePaging`
- Enforced boundaries: `elastic/boundary/deterministic-provider`, `elastic/boundary/max-page-size`, `elastic/boundary/non-null-operands`, `elastic/boundary/scalar-operands`, `elastic/boundary/single-index`, `elastic/boundary/stable-unique-ordering`
- Operating boundaries:
  - `elastic/boundary/deterministic-provider`: DeterministicProvider
  - `elastic/boundary/max-page-size`: MaximumPageSize = 1000
  - `elastic/boundary/non-null-operands`: NonNullOperands
  - `elastic/boundary/scalar-operands`: ScalarOperands
  - `elastic/boundary/single-index`: SingleSource
  - `elastic/boundary/stable-unique-ordering`: StableUniqueOrdering

### PostgreSQL

- Target: `cohesive.adapters.postgres.sql`
- Profile: `cohesive.adapters.postgres.sql/canonical-v1`
- Capability evidence: 123
- Definition schemas: `relation-query/v1`
- Compiler profiles: `relation-query-static/v1`
- Full-profile SHA-256: `25161955b6f2da37ee1ca9693f72c90e66bf4c2fd67e7fdd1fa2bf8255491ea2`
- Families: boundary validation=11, expression=24, guarantee=22, logical=42, structural=11, temporal=13

- Logical semantics: `AggregateFilter`, `AggregateGrouping`, `Aggregation`, `AlwaysPresentBinding`, `AscendingOrdering`, `AtMostOneRelationshipTraversal`, `AverageAggregate`, `CountAggregate`, `DescendingOrdering`, `DistinctRows`, `Filter`, `ForwardRelationshipTraversal`, `InnerJoin`, `InverseRelationshipTraversal`, `Join`, `KeysetPaging`, `LeftOuterJoin`, `ManyPerRootRelationOutput`, `ManyRelationshipTraversal`, `MaximumAggregate`, `MayBeAbsentBinding`, `MinimumAggregate`, `NullsFirst`, `NullsLast`, `OffsetPaging`, `OnePerRootRelationOutput`, `OptionalRelationshipTraversal`, `Ordering`, `Projection`, `ProjectionAssignment`, `QueryAggregationResult`, `QueryRowsResult`, `RelationInvariant`, `RelationOutputIdentity`, `RelationshipTraversal`, `RequiredRelationshipTraversal`, `SetRelationOutput`, `Source`, `StableTieOrdering`, `SumAggregate`, `TemporalJoin`, `ZeroOrOnePerRootRelationOutput`
- Expression semantics: `Operation:expr.function.endsWith`, `Operation:expr.function.startsWith`, `Operation:expr.function.textContains`, `Operation:expr.node.conditional`, `Operation:expr.node.constant`, `Operation:expr.node.field`, `Operation:expr.node.field.nestedPath`, `Operation:expr.node.parameter`, `Operation:expr.node.typedField`, `Operation:expr.node.typedLiteral`, `Operation:expr.operator.aggregate.average`, `Operation:expr.operator.aggregate.count`, `Operation:expr.operator.aggregate.max`, `Operation:expr.operator.aggregate.min`, `Operation:expr.operator.aggregate.sum`, `Operation:expr.operator.binary.and`, `Operation:expr.operator.binary.eq`, `Operation:expr.operator.binary.ge`, `Operation:expr.operator.binary.gt`, `Operation:expr.operator.binary.le`, `Operation:expr.operator.binary.lt`, `Operation:expr.operator.binary.ne`, `Operation:expr.operator.binary.or`, `Operation:expr.operator.unary.not`
- Structural paths: `AggregateTarget:NestedField`, `AggregateTarget:TopLevelField`, `BindingRead:NestedField`, `BindingRead:TopLevelField`, `CompleteValue:RootValue`, `GroupingTarget:NestedField`, `GroupingTarget:TopLevelField`, `OutputSelection:NestedField`, `OutputSelection:TopLevelField`, `ProjectionTarget:NestedField`, `ProjectionTarget:TopLevelField`
- Preserved guarantees: `AbsenceAvailabilityFailureDistinction`, `Aggregation`, `Cardinality`, `ConsistentSnapshot`, `DeterministicResult`, `DuplicateHandling`, `EvidenceCompleteness`, `Grouping`, `InconclusiveEvidence`, `JoinMembership`, `MissingNullDistinction`, `NullPlacement`, `Ordering`, `OutputIdentity`, `OutputMode`, `RelationRootCorrelation`, `RelationshipDirection`, `RelationshipMultiplicity`, `StablePaging`, `TemporalBoundary`, `TemporalDomain`, `UnboundedTemporalBoundary`
- Enforced boundaries: `postgres/boundary/complete-input-evidence`, `postgres/boundary/deterministic-provider`, `postgres/boundary/exact-numeric-aggregate-domain`, `postgres/boundary/exact-temporal-domain`, `postgres/boundary/homogeneous-temporal-domain`, `postgres/boundary/max-page-size`, `postgres/boundary/non-null-operands`, `postgres/boundary/scalar-operands`, `postgres/boundary/single-database`, `postgres/boundary/stable-unique-ordering`, `postgres/boundary/supplied-relation-root`
- Temporal semantics: `DateDomain`, `DateTimeDomain`, `ExclusiveBoundary`, `InclusiveBoundary`, `InconclusiveEvidence`, `InnerJoin`, `InstantDomain`, `LeftOuterJoin`, `NullAsUnbounded`, `PointInInterval`, `PreserveAllMatches`, `UnboundedBoundary`, `ValidateIntervals`
- Operating boundaries:
  - `postgres/boundary/complete-input-evidence`: CompleteInputEvidence
  - `postgres/boundary/deterministic-provider`: DeterministicProvider
  - `postgres/boundary/exact-numeric-aggregate-domain`: ExactNumericAggregateDomain
  - `postgres/boundary/exact-temporal-domain`: ExactTemporalDomain
  - `postgres/boundary/homogeneous-temporal-domain`: HomogeneousTemporalDomain
  - `postgres/boundary/max-page-size`: MaximumPageSize = 1000
  - `postgres/boundary/non-null-operands`: NonNullOperands
  - `postgres/boundary/scalar-operands`: ScalarOperands
  - `postgres/boundary/single-database`: SingleSource
  - `postgres/boundary/stable-unique-ordering`: StableUniqueOrdering
  - `postgres/boundary/supplied-relation-root`: SuppliedRelationRoot
<!-- generated-capability-profiles:end -->

Regenerate and verify only this document:

```bash
UPDATE_RELATIONS_CAPABILITY_DOCS=1 \
  dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release \
  --filter FullyQualifiedName~RelationQueryCapabilityDocumentationTests
```

Without the environment variable, the same test is read-only and verifies that the checked-in block is current.

## Important interpretation boundaries

The generated inventory answers what a profile declares. A successful execution additionally requires:

- The canonical definition schema and static compiler profile to be supported.
- Every exact demand to match capability evidence.
- Every constrained strategy's boundaries to be proved by placement and bindings.
- Runtime parameters and supplied fields to satisfy their compiled value contracts.
- Native artifacts, source placement, storage bindings, and result readers to retain plan affinity.
- The executor or driver to preserve declared completeness, consistency, ordering, and result reconstruction.

Descriptions on target profiles and operating boundaries are metadata. Capability identities, boundary kinds and
limits, contextual assessments, compiler diagnostics, and fingerprints are the machine-readable authorities.

## Current target roles

- **In-memory reference:** canonical interpretation over already materialized evidence. It performs no I/O and is the
  semantic reference for differential tests.
- **Cosmos SQL:** native single-container query and aggregation compilation within its advertised structural, value,
  ordering, paging, and exact numeric boundaries.
- **Cosmos entity source:** bounded enumeration and batched identity/predicate acquisition through the Cosmos SDK. It
  supplies primitive evidence to composed plans; it is not a cross-document SQL join profile.
- **Elasticsearch:** native single-index SDK query and aggregation lowering within mapped scalar, nested-correlation,
  deterministic-provider, ordering, and paging boundaries.
- **PostgreSQL:** provider-neutral SQL compilation for supported rows, aggregation, relationship and explicit joins,
  temporal joins, ordering, and paging when exact table/column/domain evidence proves the operating boundaries.

See [Execution and adapters](EXECUTION_AND_ADAPTERS.md) for concrete paths and each adapter README for its binding and
override surface.

using System.Text.Json.Serialization;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Realization;

/// <summary>Logical relation/query semantic implemented by an interpretation target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryLogicalCapabilityKind
{
    /// <summary>Enumerates a canonical logical source.</summary>
    Source = 0,

    /// <summary>Filters rows with a Boolean predicate.</summary>
    Filter = 1,

    /// <summary>Traverses a declared semantic relationship.</summary>
    RelationshipTraversal = 2,

    /// <summary>Traverses a relationship in its declared forward direction.</summary>
    ForwardRelationshipTraversal = 3,

    /// <summary>Traverses a relationship in its inverse direction.</summary>
    InverseRelationshipTraversal = 4,

    /// <summary>Preserves an at-most-one relationship traversal.</summary>
    AtMostOneRelationshipTraversal = 5,

    /// <summary>Preserves a many-valued relationship traversal.</summary>
    ManyRelationshipTraversal = 6,

    /// <summary>Preserves a required relationship input.</summary>
    RequiredRelationshipTraversal = 7,

    /// <summary>Preserves an optional relationship input.</summary>
    OptionalRelationshipTraversal = 8,

    /// <summary>Correlates two independently produced logical rowsets.</summary>
    Join = 9,

    /// <summary>Preserves inner-join membership semantics.</summary>
    InnerJoin = 10,

    /// <summary>Preserves left-outer-join membership semantics.</summary>
    LeftOuterJoin = 11,

    /// <summary>Preserves right-outer-join membership semantics.</summary>
    RightOuterJoin = 12,

    /// <summary>Preserves full-outer-join membership semantics.</summary>
    FullOuterJoin = 13,

    /// <summary>Correlates rows using canonical valid-time semantics.</summary>
    TemporalJoin = 14,

    /// <summary>Expands a collection while retaining parent-row provenance.</summary>
    ExpandCollection = 15,

    /// <summary>Constructs a shaped projection.</summary>
    Projection = 16,

    /// <summary>Evaluates one projection assignment.</summary>
    ProjectionAssignment = 17,

    /// <summary>Removes duplicate complete rows.</summary>
    DistinctRows = 18,

    /// <summary>Removes duplicates according to declared key expressions.</summary>
    DistinctKeys = 19,

    /// <summary>Groups input rows and produces aggregate output.</summary>
    Aggregation = 20,

    /// <summary>Forms an aggregation group from a declared key.</summary>
    AggregateGrouping = 21,

    /// <summary>Filters the values supplied to one aggregate assignment.</summary>
    AggregateFilter = 22,

    /// <summary>Counts rows or non-null aggregate values.</summary>
    CountAggregate = 23,

    /// <summary>Sums aggregate values.</summary>
    SumAggregate = 24,

    /// <summary>Selects the minimum aggregate value.</summary>
    MinimumAggregate = 25,

    /// <summary>Selects the maximum aggregate value.</summary>
    MaximumAggregate = 26,

    /// <summary>Evaluates existential aggregate semantics.</summary>
    AnyAggregate = 27,

    /// <summary>Evaluates universal aggregate semantics.</summary>
    AllAggregate = 28,

    /// <summary>Orders a logical rowset by one or more keys.</summary>
    Ordering = 29,

    /// <summary>Orders values in ascending semantic order.</summary>
    AscendingOrdering = 30,

    /// <summary>Orders values in descending semantic order.</summary>
    DescendingOrdering = 31,

    /// <summary>Places null and missing values before concrete values.</summary>
    NullsFirst = 32,

    /// <summary>Places null and missing values after concrete values.</summary>
    NullsLast = 33,

    /// <summary>Preserves deterministic tie order across equivalent ordering keys.</summary>
    StableTieOrdering = 34,

    /// <summary>Applies offset-and-limit paging.</summary>
    OffsetPaging = 35,

    /// <summary>Applies ordered keyset continuation paging.</summary>
    KeysetPaging = 36,

    /// <summary>Emits exactly one relation output for every root.</summary>
    OnePerRootRelationOutput = 37,

    /// <summary>Emits zero or one relation output for every root.</summary>
    ZeroOrOnePerRootRelationOutput = 38,

    /// <summary>Emits any number of relation outputs for every root.</summary>
    ManyPerRootRelationOutput = 39,

    /// <summary>Emits relation output over the complete input set.</summary>
    SetRelationOutput = 40,

    /// <summary>Evaluates and validates stable relation output identity.</summary>
    RelationOutputIdentity = 41,

    /// <summary>Evaluates a declared relation invariant.</summary>
    RelationInvariant = 42,

    /// <summary>Produces a named query row-result terminal.</summary>
    QueryRowsResult = 43,

    /// <summary>Produces a named query aggregation-result terminal.</summary>
    QueryAggregationResult = 44,

    /// <summary>Consumes a named binding that is present for every evaluation row.</summary>
    AlwaysPresentBinding = 45,

    /// <summary>Consumes a named binding that may be absent for some evaluation rows.</summary>
    MayBeAbsentBinding = 46,

    /// <summary>Computes an arithmetic average in the canonical decimal result domain.</summary>
    AverageAggregate = 47,

    /// <summary>Selects the unique best ordered row per partition, retaining only its provenance.</summary>
    SelectRepresentative = 48
}

/// <summary>Semantic role in which a target interprets a structural field path.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryStructuralCapabilityRole
{
    /// <summary>Reads a path from a named expression binding.</summary>
    BindingRead = 0,

    /// <summary>
    /// Reads a path from an expression's scoped collection element while preserving the element boundary.
    /// </summary>
    CurrentItemRead = 1,

    /// <summary>Reconstructs a path from occurrence-scoped runtime evidence.</summary>
    OccurrenceEvidenceReconstruction = 2,

    /// <summary>Writes a path in a projected output object.</summary>
    ProjectionTarget = 3,

    /// <summary>Writes a grouping key into an aggregate output object.</summary>
    GroupingTarget = 4,

    /// <summary>Writes an aggregate result into an aggregate output object.</summary>
    AggregateTarget = 5,

    /// <summary>Selects a demanded path from a shaped terminal value.</summary>
    OutputSelection = 6,

    /// <summary>Reads or copies a complete shaped value.</summary>
    CompleteValue = 7
}

/// <summary>Portable structural category of a field path.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryStructuralPathKind
{
    /// <summary>The operation addresses a complete root value rather than a child path.</summary>
    RootValue = 0,

    /// <summary>The path contains one top-level field segment.</summary>
    TopLevelField = 1,

    /// <summary>The path contains nested field segments and no collection-element segment.</summary>
    NestedField = 2,

    /// <summary>The path contains a collection-element segment.</summary>
    CollectionElement = 3,

    /// <summary>The path contains nested fields and one or more collection-element segments.</summary>
    NestedCollectionElement = 4
}

/// <summary>Semantic guarantee that a realization must preserve.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryGuaranteeCapabilityKind
{
    /// <summary>Preserves distinct missing and explicit-null states.</summary>
    MissingNullDistinction = 0,

    /// <summary>Preserves absent, unavailable, and failed states without conflating them.</summary>
    AbsenceAvailabilityFailureDistinction = 1,

    /// <summary>Preserves join membership semantics.</summary>
    JoinMembership = 2,

    /// <summary>Preserves row and traversal cardinality.</summary>
    Cardinality = 3,

    /// <summary>Preserves relationship traversal direction.</summary>
    RelationshipDirection = 4,

    /// <summary>Preserves relationship traversal multiplicity.</summary>
    RelationshipMultiplicity = 5,

    /// <summary>Preserves exact temporal scalar domains without coercion.</summary>
    TemporalDomain = 6,

    /// <summary>Preserves inclusive and exclusive temporal boundaries.</summary>
    TemporalBoundary = 7,

    /// <summary>Preserves structural and convention-derived unbounded interval endpoints.</summary>
    UnboundedTemporalBoundary = 8,

    /// <summary>Preserves declared ordering direction.</summary>
    Ordering = 9,

    /// <summary>Preserves declared placement of null and missing ordering keys.</summary>
    NullPlacement = 10,

    /// <summary>Preserves stable paging membership and continuation behavior.</summary>
    StablePaging = 11,

    /// <summary>Preserves grouping semantics.</summary>
    Grouping = 12,

    /// <summary>Preserves aggregate operator semantics, including empty-input behavior.</summary>
    Aggregation = 13,

    /// <summary>Preserves duplicate elimination semantics.</summary>
    DuplicateHandling = 14,

    /// <summary>Preserves relation output identity.</summary>
    OutputIdentity = 15,

    /// <summary>Preserves declared relation output cardinality mode.</summary>
    OutputMode = 16,

    /// <summary>Enforces every demanded relation invariant.</summary>
    InvariantEnforcement = 17,

    /// <summary>Produces deterministic results for equivalent semantic inputs.</summary>
    DeterministicResult = 18,

    /// <summary>Retains contributing occurrence and root provenance.</summary>
    OccurrenceProvenance = 19,

    /// <summary>Distinguishes complete from partial runtime evidence.</summary>
    EvidenceCompleteness = 20,

    /// <summary>Propagates inconclusive evidence instead of treating it as a conclusive miss.</summary>
    InconclusiveEvidence = 21,

    /// <summary>Reads all participating inputs from one semantically consistent snapshot.</summary>
    ConsistentSnapshot = 22,

    /// <summary>
    /// Preserves same-element correlation among all current-item reads performed within one scoped
    /// collection-expression evaluation.
    /// </summary>
    CollectionElementCorrelation = 23,

    /// <summary>Correlates every rooted non-set relation output row with the root occurrence that produced it.</summary>
    RelationRootCorrelation = 24
}

/// <summary>Primitive target facility from which exact higher-level semantics may be composed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryPrimitiveCapabilityKind
{
    /// <summary>Extracts a stable semantic key from an input value.</summary>
    KeyExtraction = 0,

    /// <summary>Reads multiple observations by key in one bounded operation.</summary>
    BatchedKeyLookup = 1,

    /// <summary>Reads observations satisfying a portable predicate.</summary>
    PredicateRead = 2,

    /// <summary>Enumerates a complete logical source set.</summary>
    CompleteSetEnumeration = 3,

    /// <summary>Correlates two materialized rowsets locally.</summary>
    LocalCorrelation = 4,

    /// <summary>Performs a local hash join.</summary>
    HashJoin = 5,

    /// <summary>Performs a stable local sort.</summary>
    StableSort = 6,

    /// <summary>Performs local grouping and aggregation.</summary>
    LocalAggregation = 7,

    /// <summary>Projects selected fields without loading a complete value.</summary>
    FieldProjection = 8,

    /// <summary>Compares values while preserving null and missing semantics.</summary>
    NullAwareComparison = 9,

    /// <summary>Compares values within one exact temporal domain.</summary>
    TemporalComparison = 10,

    /// <summary>Evaluates interval containment or overlap predicates.</summary>
    IntervalPredicate = 11,

    /// <summary>Reads stable observation identity.</summary>
    ObservationIdentityRead = 12,

    /// <summary>Reads a declared relationship reference.</summary>
    RelationshipReferenceRead = 13,

    /// <summary>Binds invocation parameters for expression evaluation.</summary>
    ParameterBinding = 14,

    /// <summary>Binds evaluation-scoped ambient expression capabilities.</summary>
    AmbientCapabilityBinding = 15,

    /// <summary>Constructs a shaped output object.</summary>
    OutputObjectConstruction = 16,

    /// <summary>Tracks source occurrence and realization provenance.</summary>
    ProvenanceTracking = 17,

    /// <summary>Reads observations matching multiple predicate values in one bounded operation.</summary>
    BatchedPredicateLookup = 18,

    /// <summary>Evaluates a declared semantic invariant.</summary>
    InvariantEvaluation = 19,

    /// <summary>Applies offset-based paging.</summary>
    OffsetPaging = 20,

    /// <summary>Seeks from a stable keyset continuation.</summary>
    KeysetSeek = 21
}

/// <summary>Closed portable description of one relation/query target capability.</summary>
/// <remarks>
/// Capability variants retain unknown numeric enum values at the declaration boundary so the realization compiler
/// can produce attributable diagnostics for a target profile imported from a newer or malformed producer. A
/// capability is not evidence of support until profile validation and matching accept it.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryRealizationWireNames.CapabilityDiscriminator)]
[JsonDerivedType(typeof(LogicalRelationQueryCapability), RelationQueryRealizationWireNames.LogicalCapability)]
[JsonDerivedType(typeof(ExpressionRelationQueryCapability), RelationQueryRealizationWireNames.ExpressionCapability)]
[JsonDerivedType(typeof(TemporalRelationQueryCapability), RelationQueryRealizationWireNames.TemporalCapability)]
[JsonDerivedType(typeof(StructuralRelationQueryCapability), RelationQueryRealizationWireNames.StructuralCapability)]
[JsonDerivedType(typeof(GuaranteeRelationQueryCapability), RelationQueryRealizationWireNames.GuaranteeCapability)]
[JsonDerivedType(typeof(OperatingBoundaryValidationRelationQueryCapability), RelationQueryRealizationWireNames.OperatingBoundaryValidationCapability)]
[JsonDerivedType(typeof(PrimitiveRelationQueryCapability), RelationQueryRealizationWireNames.PrimitiveCapability)]
public abstract record RelationQueryCapability
{
    /// <summary>Creates a capability variant.</summary>
    private protected RelationQueryCapability()
    {
    }
}

/// <summary>Capability to preserve one logical relation/query semantic.</summary>
public sealed record LogicalRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates a logical capability.</summary>
    /// <param name="kind">Exact logical semantic supported by the target.</param>
    public LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind kind)
    {
        Kind = kind;
    }

    /// <summary>Exact logical semantic supported by the target.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryLogicalCapabilityKind>))]
    public RelationQueryLogicalCapabilityKind Kind { get; }
}

/// <summary>Capability to evaluate one portable expression operation or ambient dependency.</summary>
public sealed record ExpressionRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates an expression capability.</summary>
    /// <param name="capability">Stable expression capability identity.</param>
    /// <param name="requirementKind">Whether the capability is an operation or ambient dependency.</param>
    /// <exception cref="ArgumentException"><paramref name="capability"/> is default or empty.</exception>
    public ExpressionRelationQueryCapability(
        ExprCapabilityId capability,
        ExprCapabilityRequirementKind requirementKind)
    {
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("An expression capability requires a non-empty identity.", nameof(capability));
        Capability = capability;
        RequirementKind = requirementKind;
    }

    /// <summary>Stable expression capability identity.</summary>
    public ExprCapabilityId Capability { get; }

    /// <summary>Whether the capability is an operation or ambient dependency.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<ExprCapabilityRequirementKind>))]
    public ExprCapabilityRequirementKind RequirementKind { get; }
}

/// <summary>Capability to preserve one exact valid-time join semantic.</summary>
public sealed record TemporalRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates a temporal capability.</summary>
    /// <param name="capability">Exact temporal execution semantic.</param>
    public TemporalRelationQueryCapability(RelationQueryTemporalExecutionCapability capability)
    {
        Capability = capability;
    }

    /// <summary>Exact temporal execution semantic.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryTemporalExecutionCapability>))]
    public RelationQueryTemporalExecutionCapability Capability { get; }
}

/// <summary>Capability to interpret a structural path in one semantic role.</summary>
public sealed record StructuralRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates a structural capability.</summary>
    /// <param name="role">Semantic role in which the path is interpreted.</param>
    /// <param name="pathKind">Portable structure of supported paths.</param>
    public StructuralRelationQueryCapability(
        RelationQueryStructuralCapabilityRole role,
        RelationQueryStructuralPathKind pathKind)
    {
        Role = role;
        PathKind = pathKind;
    }

    /// <summary>Semantic role in which the path is interpreted.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryStructuralCapabilityRole>))]
    public RelationQueryStructuralCapabilityRole Role { get; }

    /// <summary>Portable structure of supported paths.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryStructuralPathKind>))]
    public RelationQueryStructuralPathKind PathKind { get; }
}

/// <summary>Capability to preserve one cross-cutting semantic guarantee.</summary>
public sealed record GuaranteeRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates a guarantee capability.</summary>
    /// <param name="kind">Exact guarantee preserved by the target.</param>
    public GuaranteeRelationQueryCapability(RelationQueryGuaranteeCapabilityKind kind)
    {
        Kind = kind;
    }

    /// <summary>Exact guarantee preserved by the target.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryGuaranteeCapabilityKind>))]
    public RelationQueryGuaranteeCapabilityKind Kind { get; }
}

/// <summary>Capability to enforce one exact declared operating boundary at target execution.</summary>
public sealed record OperatingBoundaryValidationRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates an exact boundary-enforcement capability.</summary>
    /// <param name="boundary">Stable identity of the boundary the target can enforce.</param>
    /// <exception cref="ArgumentException"><paramref name="boundary"/> is default.</exception>
    public OperatingBoundaryValidationRelationQueryCapability(RelationQueryOperatingBoundaryId boundary)
    {
        if (string.IsNullOrWhiteSpace(boundary.Value))
            throw new ArgumentException("Boundary validation capability requires a boundary identity.", nameof(boundary));
        Boundary = boundary;
    }

    /// <summary>Stable identity of the boundary the target can enforce.</summary>
    public RelationQueryOperatingBoundaryId Boundary { get; }
}

/// <summary>Primitive target facility that can participate in an exact composition rule.</summary>
public sealed record PrimitiveRelationQueryCapability : RelationQueryCapability
{
    /// <summary>Creates a primitive target capability.</summary>
    /// <param name="kind">Exact primitive facility supplied by the target.</param>
    public PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind kind)
    {
        Kind = kind;
    }

    /// <summary>Exact primitive facility supplied by the target.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryPrimitiveCapabilityKind>))]
    public RelationQueryPrimitiveCapabilityKind Kind { get; }
}

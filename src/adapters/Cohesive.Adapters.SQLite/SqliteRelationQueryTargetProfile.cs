using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.SQLite;

/// <summary>Capabilities of the bounded SQLite compiler for canonical row queries.</summary>
public static class SqliteRelationQueryTargetProfile
{
    /// <summary>SQLite native query target.</summary>
    public static RelationQueryTargetId Target { get; } = new("cohesive.adapters.sqlite.sql");
    /// <summary>Versioned interpretation contract.</summary>
    public static RelationQueryTargetProfileId ProfileId { get; } = new("cohesive.adapters.sqlite.sql/canonical-v1");
    /// <summary>Deterministic placement and lowering convention version.</summary>
    public const string ConventionSet = "cohesive.adapters.sqlite.sql/conventions-v1";
    /// <summary>Boundary requiring exact encodings, complete co-located tables and unique ordering.</summary>
    public static RelationQueryOperatingBoundaryId StorageBoundary { get; } = new("sqlite/boundary/exact-storage");
    /// <summary>Supported semantics, subject to storage-binding inspection.</summary>
    public static RelationQueryTargetCapabilityProfile Default { get; } = Create();
    /// <summary>Policy requiring validation of every constrained realization.</summary>
    public static RelationQueryRealizationPolicy Policy { get; } = new(
        new("sqlite/policy-v1"), ConventionSet, constrainedRealizations: RelationQueryConstrainedRealizationPolicy.AllowValidated);

    static RelationQueryTargetCapabilityProfile Create()
    {
        List<RelationQueryCapability> capabilities = [];
        RelationQueryLogicalCapabilityKind[] logical =
        [
            RelationQueryLogicalCapabilityKind.Source, RelationQueryLogicalCapabilityKind.Filter,
            RelationQueryLogicalCapabilityKind.Join, RelationQueryLogicalCapabilityKind.InnerJoin,
            RelationQueryLogicalCapabilityKind.LeftOuterJoin, RelationQueryLogicalCapabilityKind.Projection,
            RelationQueryLogicalCapabilityKind.ProjectionAssignment, RelationQueryLogicalCapabilityKind.SelectRepresentative,
            RelationQueryLogicalCapabilityKind.Ordering, RelationQueryLogicalCapabilityKind.AscendingOrdering,
            RelationQueryLogicalCapabilityKind.DescendingOrdering, RelationQueryLogicalCapabilityKind.NullsFirst,
            RelationQueryLogicalCapabilityKind.NullsLast, RelationQueryLogicalCapabilityKind.StableTieOrdering,
            RelationQueryLogicalCapabilityKind.QueryRowsResult, RelationQueryLogicalCapabilityKind.AlwaysPresentBinding,
            RelationQueryLogicalCapabilityKind.MayBeAbsentBinding
        ];
        capabilities.AddRange(logical.Select(static kind => new LogicalRelationQueryCapability(kind)));
        ExprCapabilityId[] expressions =
        [
            ExprCapabilities.Field, ExprCapabilities.TypedField, ExprCapabilities.Parameter,
            ExprCapabilities.Constant, ExprCapabilities.TypedLiteral, ExprCapabilities.ForUnary(UnaryOperator.Not),
            .. new[] { BinaryOperator.Eq, BinaryOperator.Ne, BinaryOperator.Gt, BinaryOperator.Ge,
                BinaryOperator.Lt, BinaryOperator.Le, BinaryOperator.And, BinaryOperator.Or }.Select(ExprCapabilities.ForBinary)
        ];
        capabilities.AddRange(expressions.Select(static id => new ExpressionRelationQueryCapability(id, ExprCapabilityRequirementKind.Operation)));
        foreach (var role in new[] { RelationQueryStructuralCapabilityRole.BindingRead,
                     RelationQueryStructuralCapabilityRole.ProjectionTarget, RelationQueryStructuralCapabilityRole.OutputSelection,
                     RelationQueryStructuralCapabilityRole.OccurrenceEvidenceReconstruction })
            capabilities.Add(new StructuralRelationQueryCapability(role, RelationQueryStructuralPathKind.TopLevelField));
        capabilities.Add(new StructuralRelationQueryCapability(RelationQueryStructuralCapabilityRole.CompleteValue,
            RelationQueryStructuralPathKind.RootValue));
        RelationQueryGuaranteeCapabilityKind[] guarantees =
        [
            RelationQueryGuaranteeCapabilityKind.MissingNullDistinction,
            RelationQueryGuaranteeCapabilityKind.AbsenceAvailabilityFailureDistinction,
            RelationQueryGuaranteeCapabilityKind.JoinMembership, RelationQueryGuaranteeCapabilityKind.Cardinality,
            RelationQueryGuaranteeCapabilityKind.Ordering, RelationQueryGuaranteeCapabilityKind.NullPlacement,
            RelationQueryGuaranteeCapabilityKind.Grouping, RelationQueryGuaranteeCapabilityKind.DeterministicResult,
            RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance, RelationQueryGuaranteeCapabilityKind.EvidenceCompleteness,
            RelationQueryGuaranteeCapabilityKind.InconclusiveEvidence, RelationQueryGuaranteeCapabilityKind.ConsistentSnapshot
        ];
        capabilities.AddRange(guarantees.Select(static kind => new GuaranteeRelationQueryCapability(kind)));
        foreach (var kind in new[] { RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
                     RelationQueryPrimitiveCapabilityKind.FieldProjection, RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead })
            capabilities.Add(new PrimitiveRelationQueryCapability(kind));
        var evidence = capabilities.Select(capability => new RelationQueryTargetCapabilityEvidence(
            new($"sqlite/capability/{CapabilityId(capability)}"), capability, [StorageBoundary])).ToList();
        evidence.Add(new(new("sqlite/validate-storage"), new OperatingBoundaryValidationRelationQueryCapability(StorageBoundary)));
        return new(Target, ProfileId, [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile], [.. evidence],
            [new(StorageBoundary, RelationQueryOperatingBoundaryKind.CompleteInputEvidence,
                description: "One SQLite database snapshot; complete codec-encoded tables with non-null unique integer identities, explicit optional-field presence, and proven unique order tuples.")]);
    }

    static string CapabilityId(RelationQueryCapability capability) => capability switch
    {
        LogicalRelationQueryCapability logical => $"logical/{(int)logical.Kind}",
        ExpressionRelationQueryCapability expression => $"expression/{Uri.EscapeDataString(expression.Capability.Value)}",
        StructuralRelationQueryCapability structural => $"structural/{(int)structural.Role}/{(int)structural.PathKind}",
        GuaranteeRelationQueryCapability guarantee => $"guarantee/{(int)guarantee.Kind}",
        PrimitiveRelationQueryCapability primitive => $"primitive/{(int)primitive.Kind}",
        _ => throw new ArgumentException("Unsupported SQLite profile declaration.", nameof(capability))
    };
}

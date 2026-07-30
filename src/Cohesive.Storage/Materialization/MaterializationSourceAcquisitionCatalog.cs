using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Canonical target-independent projection from Relations acquisition semantics to materialization source reads.
/// </summary>
public static class MaterializationSourceAcquisitionCatalog
{
    /// <summary>Projects one compiled input to its required materialization source-read capability.</summary>
    /// <param name="plan">Canonical compiled relation plan that owns the input.</param>
    /// <param name="input">Compiled source or traversal input.</param>
    /// <param name="capability">Projected materialization capability when the input is known.</param>
    /// <returns><see langword="true"/> when <paramref name="input"/> belongs to the plan's source contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The input uses an unsupported relationship direction.</exception>
    public static bool TryGetReadCapability(
        CompiledRelationQueryPlan plan,
        RelationQueryInputId input,
        out MaterializationCapabilityKind capability)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.InputContract.Sources.Any(source => source.Input.Id == input))
        {
            capability = MaterializationCapabilityKind.SourceBoundedEnumeration;
            return true;
        }

        var traversal = plan.InputContract.Traversals.FirstOrDefault(candidate => candidate.Input.Id == input);
        if (traversal is null)
        {
            capability = default;
            return false;
        }

        capability = traversal.Input.Direction switch
        {
            RelationshipTraversalDirection.Forward => MaterializationCapabilityKind.SourceBatchedPointRead,
            RelationshipTraversalDirection.Inverse => MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
            _ => throw new ArgumentOutOfRangeException(
                nameof(plan),
                traversal.Input.Direction,
                "Unsupported Relations traversal direction.")
        };
        return true;
    }

    /// <summary>Projects one canonical Relations read constraint to its materialization source capability.</summary>
    /// <param name="constraint">Bounded Relations acquisition constraint.</param>
    /// <returns>The exact materialization source capability required by <paramref name="constraint"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="constraint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The constraint kind is unsupported.</exception>
    public static MaterializationCapabilityKind GetReadCapability(RelationQuerySourceReadConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        return constraint switch
        {
            RelationQueryBoundedEnumeration => MaterializationCapabilityKind.SourceBoundedEnumeration,
            RelationQueryIdentityBatchLookup => MaterializationCapabilityKind.SourceBatchedPointRead,
            RelationQueryRelationshipKeyBatchLookup => MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
            _ => throw new ArgumentOutOfRangeException(
                nameof(constraint),
                constraint.GetType().Name,
                "Unsupported materialization source-read constraint.")
        };
    }

    /// <summary>Validates that one Relations read is authorized by the exact materialization source scope.</summary>
    /// <param name="read">Canonical Relations source read.</param>
    /// <param name="scope">Exact physical-plan, placement, partition, and ordering scope.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The read and scope differ in affinity, constraint, or selected fields.</exception>
    public static void RequireCompatibleRead(
        RelationQuerySourceReadRequest read,
        MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(scope);
        var placement = scope.Placement;
        if (scope.PhysicalPlan != read.PhysicalPlan
            || placement.Id != read.PlacementBinding
            || placement.Source != read.Source
            || placement.Shape != read.Shape
            || !string.Equals(placement.Identity?.SourceSelector, read.IdentitySelector, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The source scope must contain the exact physical plan and placement that authorize the Relations read.",
                nameof(scope));
        }

        var capability = GetReadCapability(read.Constraint);
        var constraintMatches = capability switch
        {
            MaterializationCapabilityKind.SourceBoundedEnumeration =>
                placement.Kind == RelationQuerySourcePlacementBindingKind.SourceSet
                && placement.Acquisition == RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            MaterializationCapabilityKind.SourceBatchedPointRead =>
                placement.Kind == RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                && placement.Acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup,
            MaterializationCapabilityKind.SourceParameterizedPredicateQuery =>
                placement.Kind == RelationQuerySourcePlacementBindingKind.RelationshipTraversal
                && placement.Acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup
                && read.Constraint is RelationQueryRelationshipKeyBatchLookup relationship
                && placement.RelationshipKeys.Any(key =>
                    key.SemanticPath == relationship.RelationshipReference
                    && string.Equals(key.SourceSelector, relationship.SourceSelector, StringComparison.Ordinal)),
            _ => false
        };
        if (!constraintMatches)
        {
            throw new ArgumentException(
                "The Relations read constraint is incompatible with its canonical source-placement binding.",
                nameof(read));
        }

        foreach (var field in read.Fields)
        {
            var matched = field.Input is { } input
                ? placement.Fields.Any(candidate =>
                    candidate.Input == input
                    && candidate.SemanticPath == field.SemanticPath
                    && string.Equals(candidate.SourceSelector, field.SourceSelector, StringComparison.Ordinal))
                : placement.RelationshipKeys.Any(candidate =>
                    candidate.SemanticPath == field.SemanticPath
                    && string.Equals(candidate.SourceSelector, field.SourceSelector, StringComparison.Ordinal));
            if (!matched)
            {
                throw new ArgumentException(
                    "Every Relations read field must be authorized by its exact source-placement binding.",
                    nameof(read));
            }
        }
    }
}

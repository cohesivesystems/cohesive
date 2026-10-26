using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Auxiliary source-read interpretations of an admitted rooted hydration plan.</summary>
/// <remarks>These plans authorize baseline and inverse-impact acquisition; they are not alternate semantic queries.
/// The impact plan is a bounded read envelope over declared inputs: enumeration, identity and predicate stages
/// are available to the admitted resolver, including optional current-state enrichment. It is deliberately not
/// a minimal per-route authorization proof. Semantic plan, source, partition and native bounds remain fenced.</remarks>
public sealed record MaterializationRelationReadPlans
{
    MaterializationRelationReadPlans(CompiledRelationQueryPhysicalPlan baseline, CompiledRelationQueryPhysicalPlan impact)
    { Baseline = baseline; Impact = impact; }

    /// <summary>Bounded root enumeration alongside the original hydration inputs.</summary>
    public CompiledRelationQueryPhysicalPlan Baseline { get; }
    /// <summary>Bounded identity and relationship reads required by the compiled impact routes.</summary>
    public CompiledRelationQueryPhysicalPlan Impact { get; }

    /// <summary>Derives a bounded read envelope from exact hydration placement and inverse-impact evidence.</summary>
    /// <param name="plan">Canonical rooted relation plan.</param>
    /// <param name="hydration">Physical hydration with a supplied root.</param>
    /// <param name="impact">Impact routes linked to that relation.</param>
    /// <param name="fieldSelector">Maps a semantic field to the provider selector already owned by storage mappings.</param>
    /// <returns>Exact baseline and impact read plans.</returns>
    /// <exception cref="ArgumentException">The plans differ, the root is not supplied, or an impact field lacks placement.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static MaterializationRelationReadPlans Create(CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan hydration, MaterializationImpactPlan impact,
        Func<QualifiedShapeId, FieldPath, string> fieldSelector)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(hydration);
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(fieldSelector);
        var exact = RelationQueryCompiledPlanReference.From(plan);
        if (RelationQueryCompiledPlanReferenceFingerprinter.Compute(exact) !=
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(hydration.Plan))
            throw new ArgumentException("Hydration belongs to another relation.", nameof(hydration));
        if (RelationQueryCompiledPlanReferenceFingerprinter.Compute(exact) !=
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(impact.RelationPlan))
            throw new ArgumentException("Impact routes belong to another relation.", nameof(impact));
        var root = plan.InputContract.Sources.Single(source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var rootBinding = hydration.Placement.Bindings.Single(binding => binding.Input == root.Input.Id);
        if (rootBinding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied || rootBinding.Identity is null)
            throw new ArgumentException("Hydration must supply one root with a declared source identity.", nameof(hydration));
        var rootStage = hydration.Stages.Single(stage => stage.PlacementBinding == rootBinding.Id
            && stage.Kind == RelationQueryPhysicalStageKind.SuppliedInput);
        var inputs = root.Fields.Select(field => field.Input.Id).Append(root.Input.Id).ToImmutableArray();
        var fields = root.Fields.Select(field => field.Input.Id).ToImmutableArray();
        var provenance = new RelationQueryPhysicalStageProvenance(nodes: [root.Node], inputs: inputs,
            placementBindings: [rootBinding.Id]);
        var baselineBindings = hydration.Placement.Bindings.Select(binding => binding.Input == root.Input.Id
            ? Copy(binding, RelationQuerySourceAcquisitionKind.BoundedEnumeration, binding.Kind, binding.RelationshipKeys)
            : binding).ToImmutableArray();
        var baselineStages = hydration.Stages.Select(stage => stage.Id == rootStage.Id
            ? new RelationQueryPhysicalStage(stage.Id, RelationQueryPhysicalStageKind.SourceRead, stage.Dependencies,
                stage.PlacementBinding, inputs, fields, null, provenance)
            : stage).ToImmutableArray();
        var baseline = Build("baseline", baselineBindings, baselineStages);

        var relationships = impact.Routes.SelectMany(route => route.Strategy is MaterializationInverseTraversalImpactStrategy inverse
            ? inverse.Steps : []).Where(step => step.CollectionOccurrence is null)
            .GroupBy(step => step.ReferenceSourceInput)
            .ToDictionary(group => group.Key, group => group.Select(step => step.RelationshipInput).Distinct().ToArray());
        var impactBindings = hydration.Placement.Bindings.Select(binding =>
        {
            var keys = binding.RelationshipKeys.ToBuilder();
            foreach (var id in relationships.GetValueOrDefault(binding.Input, []))
            {
                if (keys.Any(key => key.Input == id)) continue;
                var traversal = plan.InputContract.Traversals.Single(candidate => candidate.Input.Id == id);
                keys.Add(new(id, traversal.Definition.SourceReference, fieldSelector(binding.Shape, traversal.Definition.SourceReference)));
            }
            return Copy(binding, binding.Input == root.Input.Id ? RelationQuerySourceAcquisitionKind.BoundedLookup : binding.Acquisition,
                binding.Input == root.Input.Id ? RelationQuerySourcePlacementBindingKind.RelationshipTraversal : binding.Kind, keys.ToImmutable());
        }).ToImmutableArray();
        var impactStages = hydration.Stages.ToBuilder();
        foreach (var binding in impactBindings)
        {
            var contractFields = binding.Fields.Select(field => field.Input).ToImmutableArray();
            var stageProvenance = new RelationQueryPhysicalStageProvenance(nodes: [binding.Node],
                inputs: contractFields.Add(binding.Input), placementBindings: [binding.Id]);
            foreach (var kind in new[] { RelationQueryPhysicalStageKind.SourceRead,
                         RelationQueryPhysicalStageKind.BatchedIdentityLookup, RelationQueryPhysicalStageKind.BatchedPredicateLookup })
            {
                if (impactStages.Any(stage => stage.PlacementBinding == binding.Id && stage.Kind == kind)) continue;
                impactStages.Add(new(new(binding.Id.Value + "/impact/" + kind), kind, kind == RelationQueryPhysicalStageKind.SourceRead ? [] : [rootStage.Id], binding.Id,
                    [binding.Input], contractFields, kind == RelationQueryPhysicalStageKind.SourceRead ? null : hydration.Policy.MaximumBatchSize,
                    stageProvenance));
            }
        }
        return new(baseline, Build("impact", impactBindings, impactStages.ToImmutable()));

        CompiledRelationQueryPhysicalPlan Build(string purpose, ImmutableArray<RelationQuerySourcePlacementBinding> bindings,
            ImmutableArray<RelationQueryPhysicalStage> stages)
        {
            var placement = new RelationQuerySourcePlacement(RelationQuerySourcePlacement.CurrentSchemaVersion, exact,
                "cohesive/materialization/" + purpose + "/v1", hydration.Placement.SourceInstances, bindings);
            return new(CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion, exact, hydration.Realization,
                placement, hydration.Policy, stages, hydration.Terminal, hydration.Diagnostics);
        }
    }

    static RelationQuerySourcePlacementBinding Copy(RelationQuerySourcePlacementBinding binding,
        RelationQuerySourceAcquisitionKind acquisition, RelationQuerySourcePlacementBindingKind kind,
        ImmutableArray<RelationQueryRelationshipKeyBinding> keys) => new(binding.Id, binding.Input, binding.Node,
        binding.Binding, binding.Shape, binding.Source, kind, acquisition, binding.Origin, binding.Identity,
        binding.Fields, keys, binding.Partition);
}

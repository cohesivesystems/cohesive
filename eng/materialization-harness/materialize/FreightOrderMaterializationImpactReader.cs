using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>
/// Binds provider-neutral compiled inverse-impact reads to exact official Relations source readers.
/// </summary>
public sealed class FreightOrderMaterializationImpactReader
{
    readonly CompiledRelationQueryPlan plan;
    readonly CompiledRelationQueryPhysicalPlan physicalPlan;
    readonly ImmutableDictionary<RelationQuerySourceInstanceId, IRelationQuerySourceReader> readers;
    readonly ImmutableDictionary<RelationQueryInputId, InputContract> inputs;

    /// <summary>Creates one plan-, placement-, and logical-partition-fenced impact reader.</summary>
    /// <param name="plan">Canonical compiled freight relation.</param>
    /// <param name="physicalPlan">Auxiliary physical plan authorizing exact impact lookups.</param>
    /// <param name="sourceReaders">Official provider readers covering every impact placement source.</param>
    /// <exception cref="ArgumentNullException">A required reference or collection is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Readers are absent, repeated, or belong to different logical partitions.</exception>
    public FreightOrderMaterializationImpactReader(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        IEnumerable<IRelationQuerySourceReader> sourceReaders)
    {
        this.plan = Guard.RequireNotNull(plan);
        this.physicalPlan = Guard.RequireNotNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(sourceReaders);
        var normalized = sourceReaders.ToArray();
        if (normalized.Any(static reader => reader is null)
            || normalized.GroupBy(static reader => reader.Descriptor.Source).Any(static group => group.Skip(1).Any()))
        {
            throw new ArgumentException("Impact source readers must cover each physical source exactly once.", nameof(sourceReaders));
        }
        readers = normalized.ToImmutableDictionary(static reader => reader.Descriptor.Source);
        if (physicalPlan.Placement.SourceInstances.Any(source => !readers.ContainsKey(source.Id)))
            throw new ArgumentException("Impact source readers do not cover every placed physical source.", nameof(sourceReaders));
        if (readers.Values.Select(static reader => reader.Descriptor.LogicalPartition).Distinct().Skip(1).Any())
            throw new ArgumentException("One impact reader cannot compose different logical partitions.", nameof(sourceReaders));

        var builder = ImmutableDictionary.CreateBuilder<RelationQueryInputId, InputContract>();
        foreach (var source in plan.InputContract.Sources)
            builder.Add(source.Input.Id, new(source.Shape, source.Fields));
        foreach (var traversal in plan.InputContract.Traversals)
            builder.Add(traversal.Input.Id, new(traversal.ResultShape, traversal.Fields));
        inputs = builder.ToImmutable();
    }

    /// <summary>Executes one provider-neutral impact read through its exact provider Relations reader.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="request">Exact identity or relationship-predicate read.</param>
    /// <returns>Complete, not-found, partial, inconclusive, or failed provider evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request names another input, relationship, or logical partition.</exception>
    /// <exception cref="InvalidOperationException">The read exceeds its hard encoded-byte boundary.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<RelationQuerySourceReadResult> ReadAsync(
        OperationContext context,
        MaterializationImpactObservationReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        if (!inputs.TryGetValue(request.Input, out var input) || input.Shape != request.Shape)
            throw new ArgumentException("The impact read input or shape is absent from the canonical relation.", nameof(request));
        var binding = physicalPlan.Placement.Bindings.SingleOrDefault(candidate => candidate.Input == request.Input)
            ?? throw new ArgumentException("The impact read input has no exact physical placement.", nameof(request));
        var reader = readers[binding.Source];
        if (reader.Descriptor.LogicalPartition != request.LogicalPartition)
            throw new ArgumentException("The impact read belongs to another provider-neutral logical partition.", nameof(request));
        var kind = request.Kind == MaterializationImpactObservationReadKind.IdentityLookup
            ? RelationQueryPhysicalStageKind.BatchedIdentityLookup
            : RelationQueryPhysicalStageKind.BatchedPredicateLookup;
        var stage = physicalPlan.Stages.Single(candidate =>
            candidate.PlacementBinding == binding.Id && candidate.Kind == kind);
        var fields = CreateFields(input.Fields, binding, request.RelationshipInput);
        RelationQuerySourceReadConstraint constraint = request.Kind switch
        {
            MaterializationImpactObservationReadKind.IdentityLookup => new RelationQueryIdentityBatchLookup(request.Keys),
            MaterializationImpactObservationReadKind.RelationshipPredicateLookup => CreatePredicate(binding, request),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported impact observation read kind.")
        };
        var read = new RelationQuerySourceReadRequest(
            physicalPlan: physicalPlan.Fingerprint,
            stage: stage.Id,
            placementBinding: binding.Id,
            source: binding.Source,
            shape: binding.Shape,
            identitySelector: binding.Identity!.SourceSelector,
            fields: fields,
            constraint: constraint,
            maximumBufferedRows: request.MaximumRows);
        var result = await reader.ReadAsync(read, context.CancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The provider Relations reader returned no impact result.");
        var encodedBytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            MaterializationJsonSerializer.CreateOptions()).LongLength;
        if (encodedBytes > request.MaximumBytes)
            throw new InvalidOperationException("The provider impact read exceeded its hard encoded-byte boundary.");
        return result;
    }

    static RelationQueryRelationshipKeyBatchLookup CreatePredicate(
        RelationQuerySourcePlacementBinding binding,
        MaterializationImpactObservationReadRequest request)
    {
        var relationshipInput = request.RelationshipInput
            ?? throw new ArgumentException("A predicate impact read has no relationship input.", nameof(request));
        var relationship = binding.RelationshipKeys.SingleOrDefault(candidate => candidate.Input == relationshipInput)
            ?? throw new ArgumentException("The impact placement has no exact relationship-key binding.", nameof(request));
        if (relationship.SemanticPath != request.RelationshipReference)
            throw new ArgumentException("The impact relationship reference differs from its exact placement.", nameof(request));
        return new(
            relationshipReference: relationship.SemanticPath,
            sourceSelector: relationship.SourceSelector,
            keys: request.Keys);
    }

    static ImmutableArray<RelationQuerySourceReadField> CreateFields(
        ImmutableArray<RelationQueryFieldInputContract> contracts,
        RelationQuerySourcePlacementBinding binding,
        RelationQueryInputId? relationshipInput)
    {
        var fields = contracts.Select(contract =>
        {
            var placed = binding.Fields.Single(field => field.Input == contract.Input.Id);
            return new RelationQuerySourceReadField(
                input: placed.Input,
                semanticPath: placed.SemanticPath,
                sourceSelector: placed.SourceSelector,
                purpose: RelationQuerySourceReadFieldPurpose.SemanticInput);
        }).ToList();
        if (relationshipInput is not { } relationshipId)
            return [.. fields];
        var relationship = binding.RelationshipKeys.Single(candidate => candidate.Input == relationshipId);
        var existing = fields.FindIndex(field =>
            field.SemanticPath == relationship.SemanticPath
            && string.Equals(field.SourceSelector, relationship.SourceSelector, StringComparison.Ordinal));
        if (existing >= 0)
        {
            var field = fields[existing];
            fields[existing] = new(
                input: field.Input,
                semanticPath: field.SemanticPath,
                sourceSelector: field.SourceSelector,
                purpose: RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation);
        }
        else
        {
            fields.Add(new(
                input: null,
                semanticPath: relationship.SemanticPath,
                sourceSelector: relationship.SourceSelector,
                purpose: RelationQuerySourceReadFieldPurpose.Correlation));
        }
        return [.. fields];
    }

    readonly record struct InputContract(
        QualifiedShapeId Shape,
        ImmutableArray<RelationQueryFieldInputContract> Fields);
}

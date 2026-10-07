using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Execution;

/// <summary>Evidence from a complete native prefix followed by an independently attributed remaining evaluation.</summary>
/// <remarks>Separate backend reads do not establish a distributed snapshot. Prefix row identities are evaluation-local
/// occurrences, not entity identities. The original-to-derived relationship is retained by <see cref="Plan"/>.</remarks>
public sealed class RelationQuerySubplanOutcome
{
    internal RelationQuerySubplanOutcome(RelationQuerySubplan plan, ImmutableArray<ObservationValue> prefixRows,
        RelationQueryEvaluationOutcome remainder)
    { Plan = plan; PrefixRows = prefixRows; Remainder = remainder; }
    /// <summary>Exact original, prefix and remainder compilation evidence.</summary>
    public RelationQuerySubplan Plan { get; }
    /// <summary>Complete prefix rowset; absence and explicit null remain distinct.</summary>
    public ImmutableArray<ObservationValue> PrefixRows { get; }
    /// <summary>Remaining physical reads, interpretation, completeness and diagnostics.</summary>
    public RelationQueryEvaluationOutcome Remainder { get; }
    /// <summary>Whether the remaining evaluation succeeded; typed projection additionally checks terminal completeness.</summary>
    public bool IsSuccessful => Remainder.IsSuccessful;
}

/// <summary>Capability contract of an already evaluated, bounded projection rowset.</summary>
public static class RelationQueryProjectedRowset
{
    /// <summary>Only complete enumeration, field projection and occurrence identity are offered; no domain lookup is implied.</summary>
    public static RelationQueryTargetCapabilityProfile Profile { get; } = new(
        new("cohesive.relations/projected-rowset"), new("cohesive.relations/projected-rowset/v1"),
        [RelationQueryDocument.CurrentSchemaVersion], [RelationQueryCompilationProvenance.CurrentCompilerProfile],
        [.. new[] { RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.FieldProjection, RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead }
            .Select(kind => new RelationQueryTargetCapabilityEvidence(new("projected-rowset/" + kind), new PrimitiveRelationQueryCapability(kind)))]);
}

/// <summary>Runs a prepared native projection once, then the existing physical executor for the remaining graph.</summary>
/// <typeparam name="TInput">The query's single invocation parameter.</typeparam>
/// <typeparam name="TResult">The original query's public result.</typeparam>
/// <remarks>Retain at host lifetime. Preparation is immutable; rowsets, readers and evidence are invocation-local.
/// No retry, ambient row cache, domain-identity invention or cross-source snapshot guarantee is introduced.</remarks>
public sealed class RelationQuerySubplanReader<TInput, TResult> : IRelationQueryReader<TInput, TResult>
{
    readonly IRelationQueryRowsReader prefix;
    readonly RelationQuerySourcePlacement placement;
    readonly RelationQueryPhysicalPlanningResult physical;
    readonly RelationQueryRealizationReport realization;
    readonly ImmutableArray<IRelationQuerySourceReader> readers;
    readonly RelationQuerySourcePlacementBinding cutBinding;
    readonly RelationQuerySourceReaderDescriptor cutDescriptor;
    readonly RelationQuery<TInput, TResult> remainderResult;
    readonly ShapeGraph cutGraph;
    readonly Shape cutShape;

    /// <summary>Prepares the remaining physical plan and its native source readers without performing IO.</summary>
    /// <param name="definition">Original typed declaration.</param>
    /// <param name="plan">Validated closed projection cut derived from that exact declaration.</param>
    /// <param name="prefix">Prepared complete-row reader implementing exactly the derived prefix.</param>
    /// <param name="placement">Remaining placement; the cut source must use the projected-rowset profile.</param>
    /// <param name="policy">Explicit row, fan-out, batch and concurrency limits.</param>
    /// <param name="remainingReaders">Creates provider readers once from the exact prepared remaining physical plan.</param>
    /// <param name="logicalPartition">Explicit host-attested scope shared by both phases; the host must enforce it in the native prefix.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Plan, placement, reader or partition affinity is invalid.</exception>
    public RelationQuerySubplanReader(RelationQuery<TInput, TResult> definition, RelationQuerySubplan plan,
        IRelationQueryRowsReader prefix, RelationQuerySourcePlacement placement, RelationQueryPhysicalPlanningPolicy policy,
        Func<CompiledRelationQueryPhysicalPlan, IEnumerable<IRelationQuerySourceReader>> remainingReaders,
        RelationQueryLogicalPartitionIdentity logicalPartition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(remainingReaders);
        ArgumentNullException.ThrowIfNull(logicalPartition);
        if (!ReferenceEquals(definition.CompilationRequest, plan.Original.Request)
            || !prefix.Plan.GetMismatchedComponents(plan.Prefix.Plan!).IsDefaultOrEmpty)
            throw new ArgumentException("Subplan execution requires exact original and native-prefix plan affinity.");
        Definition = definition;
        Plan = plan;
        this.prefix = prefix;
        this.placement = placement;
        var residual = plan.Remainder.Plan!;
        var contract = residual.InputContract.Sources.Single(source => source.Node == plan.Cut.Id);
        cutBinding = placement.Bindings.Single(binding => binding.Input == contract.Input.Id);
        var source = placement.SourceInstances.Single(source => source.Id == cutBinding.Source);
        if (!source.TargetProfile.HasSameSemantics(RelationQueryProjectedRowset.Profile)
            || placement.Bindings.Count(binding => binding.Source == source.Id) != 1
            || cutBinding.Acquisition != RelationQuerySourceAcquisitionKind.BoundedEnumeration
            || cutBinding.Partition is not null
            || cutBinding.Identity?.SemanticPath is not null)
            throw new ArgumentException("The projected rowset requires its own bounded-enumeration source, occurrence identity and no physical partition selector.", nameof(placement));
        cutDescriptor = new(source.Id, source.ExecutionDomain, source.TargetProfile, logicalPartition);
        realization = RelationQueryInMemoryInterpreter.Default.Realize(residual);
        physical = RelationQueryPhysicalPlanner.Compile(residual, realization, placement, policy);
        if (!physical.IsSuccessful)
            throw new ArgumentException("Remaining physical plan is invalid: " + string.Join("; ", physical.Diagnostics.Select(d => d.Message)));
        readers = [.. remainingReaders(physical.Plan!)];
        if (readers.Any(reader => reader is null || reader.Descriptor.Source == source.Id || reader.Descriptor.LogicalPartition != logicalPartition)
            || readers.Select(reader => reader.Descriptor.Source).Distinct().Count() != readers.Length)
            throw new ArgumentException("Remaining readers must have distinct non-prefix sources and the same explicit logical partition.", nameof(remainingReaders));
        var expectedSources = placement.Bindings.Where(binding => binding.Source != source.Id)
            .Select(binding => binding.Source).ToHashSet();
        if (!expectedSources.SetEquals(readers.Select(reader => reader.Descriptor.Source)))
            throw new ArgumentException("Remaining readers must exactly cover the remaining sources.", nameof(remainingReaders));
        foreach (var binding in placement.Bindings.Where(binding => binding.Source != source.Id))
        {
            var expected = placement.SourceInstances.Single(candidate => candidate.Id == binding.Source);
            var reader = readers.SingleOrDefault(candidate => candidate.Descriptor.Source == binding.Source);
            if (reader is null || reader.Descriptor.ExecutionDomain != expected.ExecutionDomain
                || !reader.Descriptor.TargetProfile.HasSameSemantics(expected.TargetProfile))
                throw new ArgumentException("A remaining reader is missing or has incompatible source/domain/profile affinity.", nameof(remainingReaders));
        }
        cutGraph = plan.Prefix.Request.ShapeDocuments.Single(document => document.Graph.Id == plan.Cut.ResultShape.GraphId).Graph;
        cutShape = cutGraph.GetShape(plan.Cut.ResultShape);
        remainderResult = new(plan.Remainder.Request, definition.Parameter, definition.Project);
    }

    /// <inheritdoc />
    public RelationQuery<TInput, TResult> Definition { get; }
    /// <summary>Original-to-prefix/remainder preparation evidence.</summary>
    public RelationQuerySubplan Plan { get; }

    /// <summary>Executes one native prefix and retains the complete remaining evaluation, including failures.</summary>
    /// <param name="input">Invocation-local parameter value.</param>
    /// <param name="evaluationId">Caller-assigned execution identity.</param>
    /// <param name="cancellationToken">Cancels native IO, remaining acquisition and interpretation.</param>
    /// <returns>Native rows and remaining evidence; no failed or partial outcome is converted into success.</returns>
    /// <exception cref="ArgumentException">Invocation parameters violate the canonical contract.</exception>
    /// <exception cref="InvalidOperationException">The native result violates shape or bounds.</exception>
    /// <exception cref="OperationCanceledException">Execution is canceled.</exception>
    /// <remarks>Provider failures propagate; no retries occur. Native completion does not imply remaining success.</remarks>
    public async Task<RelationQuerySubplanOutcome> EvaluateAsync(TInput input, RelationQueryEvaluationId evaluationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = ObservationValue.FromObject(input);
        _ = Definition.CompilationRequest.Evaluate(evaluationId).Set(Definition.Parameter, value).Build();
        var evaluation = Plan.Remainder.Request.Evaluate(evaluationId).Set(Definition.Parameter, value).Build();
        var parameters = Plan.Prefix.Plan!.InputContract.Parameters.ToDictionary(parameter => parameter.Definition.Id, _ => value);
        var rows = await prefix.ReadAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (rows.IsDefault || rows.Length > Math.Min(physical.Plan!.Policy.MaximumBufferedRows,
                placement.SourceInstances.Single(source => source.Id == cutBinding.Source).Limits.MaximumBufferedRows))
            throw new InvalidOperationException("Native subplan exceeded the remaining rowset bound.");
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ObservationValidator.TryValidateAgainstShape(row, cutShape, out _, cutGraph))
                throw new InvalidOperationException("Native subplan returned a row outside its declared projection shape.");
        }
        var rowReader = new ProjectedRowsReader(rows, cutDescriptor, cutBinding, physical.Plan!, Plan.Prefix.Request.DefinitionDocument.DefinitionFingerprint.Value);
        var executor = new RelationQueryPhysicalExecutor([rowReader, .. readers]);
        var residual = Plan.Remainder.Plan!;
        var parameterInputs = residual.InputContract.Parameters.Select(parameter => parameter.Input.Id).ToHashSet();
        var execution = await executor.ExecuteAsync(new(residual, physical.Plan!, realization, evaluationId,
            parameters: [.. evaluation.Parameters.Where(parameter => parameterInputs.Contains(parameter.Input))],
            capabilities: RelationQueryRealizationRuntimeEvidence.ProjectCapabilities(residual, realization)), cancellationToken).ConfigureAwait(false);
        return new(Plan, rows, new(evaluation, Plan.Remainder, realization, placement, physical, execution));
    }

    /// <inheritdoc />
    public async Task<TResult> ReadAsync(TInput input, CancellationToken cancellationToken = default)
    {
        var outcome = await EvaluateAsync(input, new("subplan/" + Guid.NewGuid().ToString("N")), cancellationToken).ConfigureAwait(false);
        return remainderResult.Project(outcome.Remainder);
    }

    sealed class ProjectedRowsReader(ImmutableArray<ObservationValue> rows, RelationQuerySourceReaderDescriptor descriptor,
        RelationQuerySourcePlacementBinding binding, CompiledRelationQueryPhysicalPlan physical, string fingerprint) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor => descriptor;
        public ValueTask<RelationQuerySourceReadResult> ReadAsync(RelationQuerySourceReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.PhysicalPlan != physical.Fingerprint || request.PlacementBinding != binding.Id || request.Source != descriptor.Source || request.Shape != binding.Shape
                || request.Constraint is not RelationQueryBoundedEnumeration enumeration
                || rows.Length > enumeration.MaximumRows || rows.Length > request.MaximumBufferedRows)
                throw new InvalidOperationException("Projected rowset acquisition requires exact affinity and complete bounded enumeration.");
            var observations = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(rows.Length);
            for (var index = 0; index < rows.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(request.Fields.Length);
                foreach (var field in request.Fields)
                {
                    var found = rows[index].TryGetField(field.SemanticPath, out var value);
                    fields.Add(!found || value.Kind == ObservationValueKind.Undefined
                        ? new(field, RelationQuerySourceReadFieldState.Missing)
                        : value.Kind == ObservationValueKind.Null ? new(field, RelationQuerySourceReadFieldState.Null)
                        : new(field, RelationQuerySourceReadFieldState.Value, value));
                }
                observations.Add(new(index.ToString("D10", CultureInfo.InvariantCulture), request.Shape, fields.MoveToImmutable()));
            }
            return ValueTask.FromResult(new RelationQuerySourceReadResult(RelationQuerySourceReadState.Complete,
                observations.MoveToImmutable(), "native-subplan/" + fingerprint));
        }
    }
}

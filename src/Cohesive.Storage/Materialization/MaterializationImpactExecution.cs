using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Storage.Materialization;

/// <summary>Authoritative current state of one materialized relation root.</summary>
public enum MaterializationRootState
{
    /// <summary>The canonical root observation is currently present.</summary>
    Present = 0,

    /// <summary>The canonical root observation is authoritatively absent.</summary>
    Absent = 1
}

/// <summary>One canonical relation root selected by impact resolution.</summary>
public sealed record MaterializationAffectedRoot
{
    /// <summary>Creates one affected-root reference.</summary>
    /// <param name="input">Canonical Relations root input.</param>
    /// <param name="identity">Stable root observation identity.</param>
    /// <param name="state">Whether current canonical root state is present or absent.</param>
    /// <param name="observation">Current complete root observation when <paramref name="state"/> is present.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or state and observation evidence conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    public MaterializationAffectedRoot(
        RelationQueryInputId input,
        string identity,
        MaterializationRootState state,
        RelationQuerySourceReadObservation? observation)
    {
        MaterializationContract.RequireDefinedIdentity(input.Value, nameof(input));
        Identity = MaterializationContract.RequireUnicodeIdentity(identity, nameof(identity));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported materialization root state.");
        if ((state == MaterializationRootState.Present) != (observation is not null))
            throw new ArgumentException("Present roots require an observation and absent roots must omit it.", nameof(observation));
        if (observation is not null && !string.Equals(observation.Identity, Identity, StringComparison.Ordinal))
            throw new ArgumentException("An affected-root observation must retain the exact root identity.", nameof(observation));

        Input = input;
        State = state;
        Observation = observation;
    }

    /// <summary>Canonical Relations root input.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Stable root observation identity.</summary>
    public string Identity { get; }

    /// <summary>Whether current canonical root state is present or absent.</summary>
    public MaterializationRootState State { get; }

    /// <summary>Current complete root observation, or <see langword="null"/> for authoritative absence.</summary>
    public RelationQuerySourceReadObservation? Observation { get; }
}

/// <summary>Current zero-or-one canonical materialized output for one affected root.</summary>
public sealed record MaterializationRootProjection
{
    /// <summary>Creates one root-grouped materialization projection.</summary>
    /// <param name="root">Exact affected root.</param>
    /// <param name="row">Current complete selected output, or <see langword="null"/> when the root emits no item.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The row conflicts with authoritative absence, is incomplete, or does not retain the v1 root identity as one
    /// concrete string.
    /// </exception>
    public MaterializationRootProjection(
        MaterializationAffectedRoot root,
        RelationQueryOutputRow? row)
    {
        Root = Guard.RequireNotNull(root);
        if (root.State == MaterializationRootState.Absent && row is not null)
        {
            throw new ArgumentException(
                "An authoritatively absent root cannot retain a materialized output row.",
                nameof(row));
        }
        if (row is not null)
        {
            if (!row.IsComplete)
                throw new ArgumentException("A materialized root projection cannot retain unresolved Relations gaps.", nameof(row));
            if (row.Identity is not { Kind: ObservationValueKind.String, String: { } identity }
                || !string.Equals(identity, root.Identity, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The v1 one-per-root interpretation requires the selected output identity to equal its string root identity.",
                    nameof(row));
            }
        }

        Row = row;
    }

    /// <summary>Exact affected root.</summary>
    public MaterializationAffectedRoot Root { get; }

    /// <summary>Current selected output, or <see langword="null"/> when the prior item must be absent.</summary>
    public RelationQueryOutputRow? Row { get; }
}

/// <summary>
/// Shared one-root-to-zero-or-one-output invariant used by baseline and incremental interpretations.
/// </summary>
internal static class MaterializationRootProjectionSemantics
{
    /// <summary>Validates exact runtime projections against the requested roots and selected output shape.</summary>
    /// <param name="requestedRoots">Canonical roots supplied to hydration.</param>
    /// <param name="expectedOutputShape">Exact selected materialization output shape.</param>
    /// <param name="projections">Runtime projections to validate.</param>
    /// <param name="parameterName">Public boundary parameter attributed by validation failures.</param>
    /// <returns>The validated projections without rematerialization.</returns>
    /// <exception cref="ArgumentException">
    /// A projection is missing, reordered, substitutes another root, violates absence, or has another shape.
    /// </exception>
    internal static ImmutableArray<MaterializationRootProjection> ValidateHydration(
        ImmutableArray<MaterializationAffectedRoot> requestedRoots,
        QualifiedShapeId expectedOutputShape,
        ImmutableArray<MaterializationRootProjection> projections,
        string parameterName)
    {
        var normalized = projections.IsDefault ? [] : projections;
        if (normalized.Length != requestedRoots.Length)
        {
            throw new ArgumentException(
                "Canonical hydration must return exactly one ordered zero-or-one projection for every requested root.",
                parameterName);
        }

        for (var index = 0; index < requestedRoots.Length; index++)
        {
            var projection = normalized[index];
            var requested = requestedRoots[index];
            if (projection is null || !ReferenceEquals(projection.Root, requested))
            {
                throw new ArgumentException(
                    "Canonical hydration must retain the exact requested root object and state in request order.",
                    parameterName);
            }
            if (requested.State == MaterializationRootState.Absent && projection.Row is not null)
            {
                throw new ArgumentException(
                    "Canonical hydration cannot emit a row for an authoritatively absent root.",
                    parameterName);
            }
            if (projection.Row is { } row && row.Shape != expectedOutputShape)
            {
                throw new ArgumentException(
                    "Canonical hydration emitted a row with a shape other than the selected materialization output.",
                    parameterName);
            }
        }

        return normalized;
    }

    /// <summary>Projects one baseline root page through the shared zero-or-one invariant.</summary>
    /// <param name="rootInput">Canonical Relations input enumerated by the baseline page.</param>
    /// <param name="observations">Distinct present root observations from the source page.</param>
    /// <param name="expectedOutputShape">Exact selected materialization output shape.</param>
    /// <param name="rows">Complete canonical Relations rows returned for the page.</param>
    /// <returns>Exactly one ordered zero-or-one projection for every source observation.</returns>
    /// <exception cref="ArgumentException">
    /// A row is incomplete, has another shape, lacks its string root identity, repeats a root, or names no page root.
    /// </exception>
    internal static ImmutableArray<MaterializationRootProjection> FromBaselinePage(
        RelationQueryInputId rootInput,
        ImmutableArray<RelationQuerySourceReadObservation> observations,
        QualifiedShapeId expectedOutputShape,
        ImmutableArray<RelationQueryOutputRow> rows)
    {
        var normalizedRows = rows.IsDefault ? [] : rows;
        Dictionary<string, RelationQueryOutputRow> rowsByRoot = new(
            capacity: normalizedRows.Length,
            comparer: StringComparer.Ordinal);
        foreach (var row in normalizedRows)
        {
            if (row is null)
                throw new ArgumentException("Baseline hydration cannot retain a null output row.", nameof(rows));
            if (row.Identity is not { Kind: ObservationValueKind.String, String: { } identity })
            {
                throw new ArgumentException(
                    "The v1 one-per-root interpretation requires every output identity to be its string root identity.",
                    nameof(rows));
            }
            if (!rowsByRoot.TryAdd(identity, row))
                throw new ArgumentException("Baseline hydration emitted more than one row for one root.", nameof(rows));
        }

        var roots = ImmutableArray.CreateBuilder<MaterializationAffectedRoot>(observations.Length);
        var projections = ImmutableArray.CreateBuilder<MaterializationRootProjection>(observations.Length);
        foreach (var observation in observations)
        {
            var root = new MaterializationAffectedRoot(
                input: rootInput,
                identity: observation.Identity,
                state: MaterializationRootState.Present,
                observation: observation);
            roots.Add(root);
            rowsByRoot.Remove(observation.Identity, out var row);
            projections.Add(new(root, row));
        }
        if (rowsByRoot.Count != 0)
            throw new ArgumentException("Baseline hydration emitted a row for a root absent from the source page.", nameof(rows));

        return ValidateHydration(
            requestedRoots: roots.MoveToImmutable(),
            expectedOutputShape: expectedOutputShape,
            projections: projections.MoveToImmutable(),
            parameterName: nameof(rows));
    }
}

/// <summary>One non-direct route-resolution request interpreted through the exact impact plan.</summary>
public sealed record MaterializationImpactRootResolutionRequest
{
    /// <summary>Creates a route-resolution request.</summary>
    /// <param name="plan">Definition-linked impact plan.</param>
    /// <param name="route">Exact route selected by the changed source input.</param>
    /// <param name="change">Canonical delivered change.</param>
    /// <param name="generation">Generation whose contributor lineage, when required, is authoritative.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The route, change, plan, or generation affinity is invalid.</exception>
    public MaterializationImpactRootResolutionRequest(
        MaterializationImpactPlan plan,
        MaterializationImpactRoute route,
        MaterializationChangeEnvelope change,
        MaterializationGenerationId generation)
    {
        Plan = Guard.RequireNotNull(plan);
        Route = Guard.RequireNotNull(route);
        Change = Guard.RequireNotNull(change);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (!plan.TryGetRoute(change.Scope.Input, out var selected)
            || selected != route
            || route.ChangeShape != change.Shape)
        {
            throw new ArgumentException("Impact resolution must use the exact plan route for the delivered change.", nameof(route));
        }
        if (route.Strategy is MaterializationDirectRootImpactStrategy)
            throw new ArgumentException("Direct roots are resolved without a physical inverse-impact operation.", nameof(route));

        Generation = generation;
    }

    /// <summary>Definition-linked impact plan.</summary>
    public MaterializationImpactPlan Plan { get; }

    /// <summary>Exact selected impact route.</summary>
    public MaterializationImpactRoute Route { get; }

    /// <summary>Canonical delivered change.</summary>
    public MaterializationChangeEnvelope Change { get; }

    /// <summary>Generation whose durable contributor lineage is authoritative.</summary>
    public MaterializationGenerationId Generation { get; }
}

/// <summary>One coalesced canonical Relations hydration request.</summary>
public sealed record MaterializationImpactHydrationRequest
{
    /// <summary>Creates one root hydration request.</summary>
    /// <param name="evaluation">Stable Relations evaluation identity.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition containing every affected root.</param>
    /// <param name="roots">Distinct current roots in canonical input and identity order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logicalPartition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The evaluation is default or roots are empty, null, repeated, or unordered.</exception>
    public MaterializationImpactHydrationRequest(
        RelationQueryEvaluationId evaluation,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        ImmutableArray<MaterializationAffectedRoot> roots)
    {
        MaterializationContract.RequireDefinedIdentity(evaluation.Value, nameof(evaluation));
        var normalized = roots.IsDefault ? [] : roots;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static root => root is null))
            throw new ArgumentException("Impact hydration requires non-null affected roots.", nameof(roots));
        var canonical = normalized
            .OrderBy(static root => root.Input.Value, StringComparer.Ordinal)
            .ThenBy(static root => root.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
        if (canonical.Select(static root => (root.Input, root.Identity)).Distinct().Count() != canonical.Length)
            throw new ArgumentException("Impact hydration cannot repeat one root identity in one input.", nameof(roots));
        if (!normalized.SequenceEqual(canonical))
            throw new ArgumentException("Impact hydration roots must already be in canonical order.", nameof(roots));

        Evaluation = evaluation;
        LogicalPartition = Guard.RequireNotNull(logicalPartition);
        Roots = normalized;
    }

    /// <summary>Stable Relations evaluation identity.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>Provider-neutral logical partition containing every affected root.</summary>
    public RelationQueryLogicalPartitionIdentity LogicalPartition { get; }

    /// <summary>Distinct current roots in canonical input and identity order.</summary>
    public ImmutableArray<MaterializationAffectedRoot> Roots { get; }
}

/// <summary>Executes one explicitly bound non-direct affected-root resolution strategy.</summary>
/// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
/// <param name="request">Exact route, change, generation, and impact-plan request.</param>
/// <returns>Complete affected roots before cross-delivery coalescing.</returns>
/// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
/// <exception cref="InvalidOperationException">Resolution is partial, inconclusive, or exceeds a physical boundary.</exception>
/// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
public delegate ValueTask<ImmutableArray<MaterializationAffectedRoot>> MaterializationImpactRootResolver(
    OperationContext context,
    MaterializationImpactRootResolutionRequest request);

/// <summary>Physical impact and hydration operations used by the canonical impact-plan interpreter.</summary>
public interface IMaterializationImpactRuntime
{
    /// <summary>Exact impact plan implemented by this runtime binding.</summary>
    MaterializationImpactPlanFingerprint ImpactPlan { get; }

    /// <summary>Resolves one non-direct contributor change to its complete bounded current root set.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="request">Exact route and change to resolve.</param>
    /// <returns>Complete affected roots before cross-delivery coalescing.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Resolution is partial, inconclusive, or exceeds a compiled bound.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
        OperationContext context,
        MaterializationImpactRootResolutionRequest request);

    /// <summary>Hydrates coalesced roots through the exact canonical Relations realization.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="request">Canonical distinct root set.</param>
    /// <returns>Exactly one zero-or-one output projection per requested root.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Hydration is partial, inconclusive, or returns inexact roots.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
        OperationContext context,
        MaterializationImpactHydrationRequest request);
}

/// <summary>
/// Production impact runtime that hydrates affected roots through one exact canonical Relations physical plan.
/// </summary>
/// <remarks>
/// Direct-root selection remains owned by <see cref="MaterializationImpactPlanInterpreter"/>. Non-direct routes must
/// bind their physical inverse-resolution operation explicitly through <see cref="MaterializationImpactRootResolver"/>;
/// the runtime never infers backend reads from the semantic impact IR.
/// </remarks>
public sealed class RelationQueryMaterializationImpactRuntime : IMaterializationImpactRuntime
{
    readonly RelationQueryMaterializationHydration hydration;
    readonly ImmutableDictionary<RelationQueryInputId, RelationQuerySourceInputContract> roots;
    readonly MaterializationImpactRootResolver? rootResolver;
    readonly RelationOutputMode outputMode;

    /// <summary>Creates one definition-linked incremental hydration runtime.</summary>
    /// <param name="impactPlan">Persisted impact plan implemented by the runtime.</param>
    /// <param name="definition">Canonical materialization definition that produced <paramref name="impactPlan"/>.</param>
    /// <param name="physicalPlan">Exact physical plan whose relation roots are supplied for bounded hydration.</param>
    /// <param name="realization">Exact successful realization report cited by <paramref name="physicalPlan"/>.</param>
    /// <param name="sourceReaders">Readers for non-root Relations hydration inputs.</param>
    /// <param name="rootResolver">
    /// Optional explicit physical resolver for inverse routes; omit when only direct-root routes are executed.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference or collection is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The impact plan is stale or foreign, the semantic-to-physical chain differs, the selected output is not a v1
    /// one-per-root materialization, or a relation-root placement is not supplied.
    /// </exception>
    /// <exception cref="InvalidOperationException">Canonical content cannot be fingerprinted.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    public RelationQueryMaterializationImpactRuntime(
        MaterializationImpactPlan impactPlan,
        MaterializationDefinition definition,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQueryRealizationReport realization,
        IEnumerable<IRelationQuerySourceReader> sourceReaders,
        MaterializationImpactRootResolver? rootResolver = null)
    {
        var linkage = MaterializationImpactPlanLinker.Link(impactPlan, definition);
        if (linkage.RelationPlan.Definition is not RelationDefinition relation
            || definition.Relation.Output.Kind != RelationQueryOutputReferenceKind.Relation
            || definition.Relation.Output.Relation != relation.Id
            || relation.Output.Mode is RelationOutputMode.ManyPerRoot or RelationOutputMode.Set)
        {
            throw new ArgumentException(
                "Incremental materialization hydration requires one complete one-per-root canonical Relation output.",
                nameof(definition));
        }

        hydration = new(
            linkage.RelationPlan,
            physicalPlan,
            realization,
            definition.Relation.Output,
            sourceReaders);
        var builder = ImmutableDictionary.CreateBuilder<RelationQueryInputId, RelationQuerySourceInputContract>();
        foreach (var root in hydration.RelationRoots.Values)
        {
            hydration.RequireSuppliedRoot(root.Input.Id, nameof(physicalPlan));
            builder.Add(root.Input.Id, root);
        }
        if (builder.Count == 0)
            throw new ArgumentException("Incremental materialization hydration requires at least one relation root.", nameof(definition));

        roots = builder.ToImmutable();
        this.rootResolver = rootResolver;
        outputMode = relation.Output.Mode;
        ImpactPlan = impactPlan.Fingerprint;
    }

    /// <inheritdoc />
    public MaterializationImpactPlanFingerprint ImpactPlan { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The request names another impact plan.
    /// </exception>
    public async ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
        OperationContext context,
        MaterializationImpactRootResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        if (request.Plan.Fingerprint != ImpactPlan)
            throw new ArgumentException("Root resolution must use the exact impact plan bound to this runtime.", nameof(request));
        if (rootResolver is null)
        {
            throw new InvalidOperationException(
                $"Impact route '{request.Route.ChangeInput.Value}' requires an explicit physical affected-root resolver.");
        }

        var resolved = await rootResolver(context, request).ConfigureAwait(false);
        context.ThrowIfCancellationRequested();
        return resolved.IsDefault ? [] : resolved;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// The request names a non-root input or supplies an observation with another canonical shape.
    /// </exception>
    public async ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
        OperationContext context,
        MaterializationImpactHydrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();

        Dictionary<RelationQueryInputId, List<RelationQuerySourceReadObservation>> observationsByInput = new(
            capacity: roots.Count);
        foreach (var input in roots.Keys)
            observationsByInput.Add(input, []);
        foreach (var root in request.Roots)
        {
            if (!roots.TryGetValue(root.Input, out var contract))
                throw new ArgumentException("Impact hydration named an input that is not a canonical relation root.", nameof(request));
            if (root.Observation is { } observation)
            {
                if (observation.Shape != contract.Shape)
                    throw new ArgumentException("Impact hydration root evidence has another canonical shape.", nameof(request));
                observationsByInput[root.Input].Add(observation);
            }
        }

        var supplied = ImmutableArray.CreateBuilder<RelationQuerySuppliedSourceInput>(roots.Count);
        foreach (var root in roots.Values.OrderBy(static candidate => candidate.Input.Id.Value, StringComparer.Ordinal))
        {
            supplied.Add(new(
                input: root.Input.Id,
                logicalPartition: request.LogicalPartition,
                completeness: RelationQueryEvidenceCompleteness.Complete,
                observations: [.. observationsByInput[root.Input.Id]],
                evidenceReference: request.Evaluation.Value));
        }
        var rows = await hydration.HydrateAsync(
                context,
                request.Evaluation,
                supplied.MoveToImmutable())
            .ConfigureAwait(false);

        Dictionary<(ValueBindingId Binding, string Identity), MaterializationAffectedRoot> requestedByOccurrence = new(
            capacity: request.Roots.Length);
        foreach (var root in request.Roots)
        {
            var contract = roots[root.Input];
            requestedByOccurrence.Add((contract.Binding, root.Identity), root);
        }
        Dictionary<MaterializationAffectedRoot, RelationQueryOutputRow> rowByRoot = new(
            capacity: rows.Length,
            comparer: ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            if (row.Root?.ObservationIdentity is not { } identity
                || !requestedByOccurrence.TryGetValue((row.Root.Binding, identity), out var root))
            {
                throw new InvalidOperationException(
                    "Canonical Relations hydration emitted a row outside the exact affected-root request.");
            }
            if (!rowByRoot.TryAdd(root, row))
                throw new InvalidOperationException("Canonical Relations hydration emitted more than one row for one root.");
        }

        var projections = ImmutableArray.CreateBuilder<MaterializationRootProjection>(request.Roots.Length);
        foreach (var root in request.Roots)
        {
            rowByRoot.Remove(root, out var row);
            if (root.State == MaterializationRootState.Present
                && outputMode == RelationOutputMode.OnePerRoot
                && row is null)
            {
                throw new InvalidOperationException(
                    $"One-per-root Relations hydration emitted no output for present root '{root.Identity}'.");
            }
            projections.Add(new(root, row));
        }
        if (rowByRoot.Count != 0)
            throw new InvalidOperationException("Canonical Relations hydration retained an uncorrelated output row.");
        return projections.MoveToImmutable();
    }
}

/// <summary>Signals that physical impact resolution exceeded its compiled affected-root bound.</summary>
public sealed class MaterializationAffectedRootBoundExceededException : InvalidOperationException
{
    /// <summary>Creates one precise compiled-bound failure.</summary>
    /// <param name="changeInput">Changed Relations input whose route exceeded its bound.</param>
    /// <param name="maximumAffectedRoots">Positive compiled maximum affected-root count.</param>
    /// <param name="actualAffectedRoots">Observed affected-root count greater than the compiled maximum.</param>
    /// <exception cref="ArgumentException"><paramref name="changeInput"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A count is invalid or <paramref name="actualAffectedRoots"/> does not exceed the compiled maximum.
    /// </exception>
    public MaterializationAffectedRootBoundExceededException(
        RelationQueryInputId changeInput,
        long maximumAffectedRoots,
        long actualAffectedRoots)
        : base(
            $"Impact route '{changeInput.Value}' returned {actualAffectedRoots} roots across its "
            + $"{maximumAffectedRoots}-root compiled bound.")
    {
        MaterializationContract.RequireDefinedIdentity(changeInput.Value, nameof(changeInput));
        if (maximumAffectedRoots <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAffectedRoots),
                maximumAffectedRoots,
                "A compiled affected-root bound must be positive.");
        }
        if (actualAffectedRoots <= maximumAffectedRoots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actualAffectedRoots),
                actualAffectedRoots,
                "An affected-root bound failure must exceed its compiled maximum.");
        }

        ChangeInput = changeInput;
        MaximumAffectedRoots = maximumAffectedRoots;
        ActualAffectedRoots = actualAffectedRoots;
    }

    /// <summary>Changed Relations input whose route exceeded its bound.</summary>
    public RelationQueryInputId ChangeInput { get; }

    /// <summary>Positive compiled maximum affected-root count.</summary>
    public long MaximumAffectedRoots { get; }

    /// <summary>Observed affected-root count.</summary>
    public long ActualAffectedRoots { get; }
}

/// <summary>Canonical route-selection, root-coalescing, and hydration interpreter for one impact plan.</summary>
public sealed class MaterializationImpactPlanInterpreter
{
    readonly MaterializationImpactPlanLinkage linkage;
    readonly IMaterializationImpactRuntime runtime;
    readonly ImmutableDictionary<RelationQueryInputId, QualifiedShapeId> rootInputs;

    /// <summary>Creates an exact definition-linked impact interpretation.</summary>
    /// <param name="plan">Persisted impact plan.</param>
    /// <param name="definition">Canonical materialization definition that produced the plan.</param>
    /// <param name="runtime">Physical inverse-read and Relations hydration realization.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan cannot be reproduced or the runtime implements another plan.</exception>
    public MaterializationImpactPlanInterpreter(
        MaterializationImpactPlan plan,
        MaterializationDefinition definition,
        IMaterializationImpactRuntime runtime)
    {
        linkage = MaterializationImpactPlanLinker.Link(plan, definition);
        this.runtime = Guard.RequireNotNull(runtime);
        if (runtime.ImpactPlan != plan.Fingerprint)
            throw new ArgumentException("The physical impact runtime must implement the exact persisted plan.", nameof(runtime));
        rootInputs = linkage.RelationPlan.InputContract.Sources
            .Where(static source => source.Role == RelationQuerySourceInputRole.RelationRoot)
            .ToImmutableDictionary(
                static source => source.Input.Id,
                static source => source.Shape);
    }

    /// <summary>Exact impact-plan fingerprint implemented by this interpreter.</summary>
    public MaterializationImpactPlanFingerprint Plan => linkage.Plan.Fingerprint;

    /// <summary>Resolves and hydrates one bounded change page, coalescing each affected root exactly once.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="feed">Exact persisted physical change feed.</param>
    /// <param name="generation">Candidate or active generation receiving the work.</param>
    /// <param name="page">Bounded source-ordered change page.</param>
    /// <returns>Canonical zero-or-one output projections for the coalesced affected roots.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Feed, page, route, root, or hydration evidence is inexact.</exception>
    /// <exception cref="MaterializationAffectedRootBoundExceededException">
    /// A compiled affected-root bound is exceeded.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<ImmutableArray<MaterializationRootProjection>> InterpretAsync(
        OperationContext context,
        MaterializationChangeFeedPlan feed,
        MaterializationGenerationId generation,
        MaterializationChangePage page)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(page);
        context.ThrowIfCancellationRequested();
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (page.ThroughPosition.Scope != feed.Scope)
            throw new ArgumentException("A change page must belong to the exact persisted feed scope.", nameof(page));
        if (!linkage.Plan.TryGetRoute(feed.Scope.Input, out var route) || route!.ChangeShape != feed.Scope.Shape)
            throw new ArgumentException("The persisted change feed does not realize an exact impact route.", nameof(feed));

        Dictionary<(RelationQueryInputId Input, string Identity), MaterializationAffectedRoot> roots = [];
        foreach (var delivery in page.Deliveries)
        {
            ImmutableArray<MaterializationAffectedRoot> resolved = route.Strategy switch
            {
                MaterializationDirectRootImpactStrategy direct =>
                [DirectRoot(direct, delivery.Change)],
                _ => await runtime.ResolveRootsAsync(
                        context,
                        new(linkage.Plan, route, delivery.Change, generation))
                    .ConfigureAwait(false)
            };
            if (resolved.Length > route.MaximumAffectedRoots)
            {
                throw new MaterializationAffectedRootBoundExceededException(
                    changeInput: route.ChangeInput,
                    maximumAffectedRoots: route.MaximumAffectedRoots,
                    actualAffectedRoots: resolved.Length);
            }
            foreach (var root in resolved)
            {
                if (root is null || !rootInputs.TryGetValue(root.Input, out var expectedRootShape))
                    throw new ArgumentException("Impact resolution returned a null or non-root Relations input.", nameof(page));
                if (root.Observation is { } observation && observation.Shape != expectedRootShape)
                {
                    throw new ArgumentException(
                        "Impact resolution returned a root observation with another canonical input shape.",
                        nameof(page));
                }
                roots[(root.Input, root.Identity)] = root;
            }
        }

        if (roots.Count == 0)
            return [];
        var canonicalRoots = roots.Values
            .OrderBy(static root => root.Input.Value, StringComparer.Ordinal)
            .ThenBy(static root => root.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
        var evaluation = new RelationQueryEvaluationId(
            $"cohesive.storage/materialization-impact-evaluation/v2/{MaterializationStableIdentity.Digest(
                "cohesive.storage/materialization-impact-evaluation/v2",
                linkage.Plan.Fingerprint.Value,
                generation.Value,
                MaterializationChannelSemantics.ToChannelScopeId(feed.Scope).Value,
                feed.Id.Value,
                page.ThroughPosition.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                page.ThroughPosition.Value)}");
        var projections = await runtime.HydrateAsync(
                context,
                new(
                    evaluation,
                    feed.Scope.LogicalPartition,
                    canonicalRoots))
            .ConfigureAwait(false);
        return MaterializationRootProjectionSemantics.ValidateHydration(
            requestedRoots: canonicalRoots,
            expectedOutputShape: linkage.Definition.Relation.Output.Shape,
            projections: projections,
            parameterName: nameof(page));
    }

    static MaterializationAffectedRoot DirectRoot(
        MaterializationDirectRootImpactStrategy strategy,
        MaterializationChangeEnvelope change) =>
        new(
            input: strategy.RootInput,
            identity: change.SubjectIdentity,
            state: change.Kind == MaterializationChangeKind.Delete
                ? MaterializationRootState.Absent
                : MaterializationRootState.Present,
            observation: change.Kind == MaterializationChangeKind.Delete ? null : change.After);
}

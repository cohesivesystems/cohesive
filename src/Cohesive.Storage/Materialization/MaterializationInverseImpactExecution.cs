using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>Closed provider-neutral read operations required by inverse-impact execution.</summary>
public enum MaterializationImpactObservationReadKind
{
    /// <summary>Reads the current observations having exact stable identities.</summary>
    IdentityLookup = 0,

    /// <summary>Reads current observations whose canonical relationship reference matches one of the supplied keys.</summary>
    RelationshipPredicateLookup = 1
}

/// <summary>One bounded provider-neutral observation read requested by compiled inverse-impact execution.</summary>
public sealed record MaterializationImpactObservationReadRequest
{
    /// <summary>Creates one exact identity or relationship-predicate read.</summary>
    /// <param name="kind">Closed read operation.</param>
    /// <param name="input">Canonical Relations acquisition input being read.</param>
    /// <param name="shape">Exact result shape for <paramref name="input"/>.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition containing the read.</param>
    /// <param name="keys">Distinct identity or relationship keys in canonical order.</param>
    /// <param name="maximumRows">Hard maximum observations admitted from the read.</param>
    /// <param name="maximumBytes">Hard maximum encoded result bytes admitted from the read.</param>
    /// <param name="relationshipInput">
    /// Canonical relationship-input identity for a predicate lookup; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="relationshipReference">
    /// Canonical source-reference path for a predicate lookup; otherwise <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">An identity, shape, key, or operation-specific relationship value is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The read kind or a hard bound is unsupported.</exception>
    public MaterializationImpactObservationReadRequest(
        MaterializationImpactObservationReadKind kind,
        RelationQueryInputId input,
        QualifiedShapeId shape,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        ImmutableArray<string> keys,
        long maximumRows,
        long maximumBytes,
        RelationQueryInputId? relationshipInput = null,
        FieldPath? relationshipReference = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported materialization impact read kind.");
        MaterializationContract.RequireDefinedIdentity(input.Value, nameof(input));
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An impact observation read requires a graph-qualified shape.", nameof(shape));
        LogicalPartition = Guard.RequireNotNull(logicalPartition);
        var normalizedKeys = keys.IsDefault ? [] : keys;
        if (normalizedKeys.IsDefaultOrEmpty || normalizedKeys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("An impact observation read requires non-empty keys.", nameof(keys));
        if (normalizedKeys.Distinct(StringComparer.Ordinal).Count() != normalizedKeys.Length)
            throw new ArgumentException("An impact observation read cannot repeat a key.", nameof(keys));
        if (maximumRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRows), maximumRows, "An impact read row bound must be positive.");
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), maximumBytes, "An impact read byte bound must be positive.");
        var isPredicate = kind == MaterializationImpactObservationReadKind.RelationshipPredicateLookup;
        if (isPredicate != (relationshipInput is not null) || isPredicate != (relationshipReference is not null))
        {
            throw new ArgumentException(
                "A relationship-predicate read requires one relationship input and reference path; an identity read permits neither.",
                nameof(relationshipInput));
        }
        if (relationshipInput is { } relationship)
            MaterializationContract.RequireDefinedIdentity(relationship.Value, nameof(relationshipInput));
        if (relationshipReference is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("A relationship-predicate read requires a non-empty reference path.", nameof(relationshipReference));

        Kind = kind;
        Input = input;
        Shape = shape;
        Keys = [.. normalizedKeys.Order(StringComparer.Ordinal)];
        MaximumRows = MaterializationContract.RequirePortablePositiveBound(maximumRows, nameof(maximumRows));
        MaximumBytes = MaterializationContract.RequirePortablePositiveBound(maximumBytes, nameof(maximumBytes));
        RelationshipInput = relationshipInput;
        RelationshipReference = relationshipReference;
    }

    /// <summary>Closed read operation.</summary>
    public MaterializationImpactObservationReadKind Kind { get; }

    /// <summary>Canonical Relations acquisition input being read.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Exact result shape for <see cref="Input"/>.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Provider-neutral logical partition containing the read.</summary>
    public RelationQueryLogicalPartitionIdentity LogicalPartition { get; }

    /// <summary>Distinct identity or relationship keys in canonical order.</summary>
    public ImmutableArray<string> Keys { get; }

    /// <summary>Hard maximum observations admitted from the read.</summary>
    public long MaximumRows { get; }

    /// <summary>Hard maximum encoded result bytes admitted from the read.</summary>
    public long MaximumBytes { get; }

    /// <summary>Canonical relationship-input identity for a predicate lookup.</summary>
    public RelationQueryInputId? RelationshipInput { get; }

    /// <summary>Canonical source-reference path for a predicate lookup.</summary>
    public FieldPath? RelationshipReference { get; }
}

/// <summary>Executes one exact bounded observation read for compiled inverse-impact resolution.</summary>
/// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
/// <param name="request">Exact provider-neutral read request.</param>
/// <returns>Complete current observations or authoritative absence for the requested boundary.</returns>
/// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
/// <exception cref="InvalidOperationException">The provider cannot return complete bounded evidence.</exception>
/// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
public delegate ValueTask<RelationQuerySourceReadResult> MaterializationImpactObservationReader(
    OperationContext context,
    MaterializationImpactObservationReadRequest request);

/// <summary>Provider-neutral interpreter for the inverse-traversal strategies retained in one impact plan.</summary>
/// <remarks>
/// The persisted strategy remains semantic authority. This interpreter only sequences its closed operations and
/// delegates exact physical identity and predicate reads to a bound provider runtime.
/// </remarks>
public sealed class MaterializationInverseTraversalExecutor
{
    readonly MaterializationImpactPlanLinkage linkage;
    readonly MaterializationImpactObservationReader reader;
    readonly RelationQuerySourceInputContract root;
    readonly ImmutableDictionary<RelationQueryInputId, InputContract> inputs;
    readonly ImmutableDictionary<RelationQueryInputId, RelationQueryTraversalInputContract> relationships;

    /// <summary>Creates one exact inverse-impact executor.</summary>
    /// <param name="plan">Persisted impact plan whose inverse strategies are interpreted.</param>
    /// <param name="definition">Canonical materialization definition that produced <paramref name="plan"/>.</param>
    /// <param name="reader">Provider runtime for complete bounded identity and predicate reads.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan is stale or the canonical relation does not have exactly one root.</exception>
    public MaterializationInverseTraversalExecutor(
        MaterializationImpactPlan plan,
        MaterializationDefinition definition,
        MaterializationImpactObservationReader reader)
    {
        linkage = MaterializationImpactPlanLinker.Link(plan, definition);
        this.reader = Guard.RequireNotNull(reader);
        var roots = linkage.RelationPlan.InputContract.Sources
            .Where(static source => source.Role == RelationQuerySourceInputRole.RelationRoot)
            .ToArray();
        if (roots.Length != 1)
            throw new ArgumentException("Inverse-impact execution currently requires exactly one canonical relation root.", nameof(definition));
        root = roots[0];

        var builder = ImmutableDictionary.CreateBuilder<RelationQueryInputId, InputContract>();
        foreach (var source in linkage.RelationPlan.InputContract.Sources)
            builder.Add(source.Input.Id, new(source.Shape, source.Fields));
        foreach (var traversal in linkage.RelationPlan.InputContract.Traversals)
            builder.Add(traversal.Input.Id, new(traversal.ResultShape, traversal.Fields));
        inputs = builder.ToImmutable();
        relationships = linkage.RelationPlan.InputContract.Traversals.ToImmutableDictionary(
            static traversal => traversal.Input.Id);
    }

    /// <summary>Exact impact-plan fingerprint implemented by this executor.</summary>
    public MaterializationImpactPlanFingerprint ImpactPlan => linkage.Plan.Fingerprint;

    /// <summary>Resolves one inverse contributor change into complete current root observations.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="request">Exact route, change, generation, and impact-plan request.</param>
    /// <returns>Affected roots in canonical identity order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request names another plan, route, shape, or input.</exception>
    /// <exception cref="InvalidOperationException">The strategy or provider evidence is partial, invalid, or inconclusive.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
        OperationContext context,
        MaterializationImpactRootResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        if (request.Plan.Fingerprint != linkage.Plan.Fingerprint)
            throw new ArgumentException("Inverse-impact resolution requires the exact bound impact plan.", nameof(request));
        if (!linkage.Plan.TryGetRoute(request.Route.ChangeInput, out var route)
            || route != request.Route
            || request.Change.Scope.Input != route.ChangeInput
            || request.Change.Shape != route.ChangeShape)
        {
            throw new ArgumentException("Inverse-impact resolution requires the exact persisted route and change scope.", nameof(request));
        }
        if (route.Strategy is not MaterializationInverseTraversalImpactStrategy strategy)
        {
            throw new InvalidOperationException(
                $"Impact strategy '{route.Strategy.Kind}' is not an inverse-traversal strategy.");
        }

        ImmutableArray<RelationQuerySourceReadObservation> observations = [];
        ImmutableArray<string> keys = [];
        RelationQueryInputId? observationInput = null;
        foreach (var step in strategy.Steps)
        {
            context.ThrowIfCancellationRequested();
            if (!relationships.TryGetValue(step.RelationshipInput, out var relationship))
                throw new InvalidOperationException($"Impact relationship '{step.RelationshipInput.Value}' is absent from the linked Relations plan.");
            if (!inputs.TryGetValue(step.ReferenceSourceInput, out var input))
                throw new InvalidOperationException($"Impact source input '{step.ReferenceSourceInput.Value}' is absent from the linked Relations plan.");

            switch (step.Operation)
            {
                case MaterializationInverseImpactOperationKind.PredicateLookup:
                    keys = keys.IsDefaultOrEmpty
                        ? observations.IsDefaultOrEmpty
                            ? [request.Change.SubjectIdentity]
                            : [.. observations.Select(static observation => observation.Identity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
                        : keys;
                    observations = await ReadAsync(
                            context,
                            new(
                                kind: MaterializationImpactObservationReadKind.RelationshipPredicateLookup,
                                input: step.ReferenceSourceInput,
                                shape: input.Shape,
                                logicalPartition: request.Change.Scope.LogicalPartition,
                                keys: keys,
                                maximumRows: route.MaximumAffectedRoots,
                                maximumBytes: route.MaximumReadBytes,
                                relationshipInput: step.RelationshipInput,
                                relationshipReference: relationship.Definition.SourceReference))
                        .ConfigureAwait(false);
                    observationInput = step.ReferenceSourceInput;
                    keys = [];
                    break;
                case MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction:
                    observations = Present(request.Change.Before, request.Change.After);
                    observationInput = step.ReferenceSourceInput;
                    keys = ExtractReferenceKeys(
                        context,
                        observations,
                        relationship.Definition.SourceReference,
                        route.ChangeInput,
                        route.MaximumAffectedRoots);
                    break;
                case MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction:
                    observations = Present(request.Change.After);
                    observationInput = step.ReferenceSourceInput;
                    keys = ExtractReferenceKeys(
                        context,
                        observations,
                        relationship.Definition.SourceReference,
                        route.ChangeInput,
                        route.MaximumAffectedRoots);
                    break;
                case MaterializationInverseImpactOperationKind.CurrentRelationshipReferenceExtraction:
                    if (observationInput != step.ReferenceSourceInput)
                    {
                        throw new InvalidOperationException(
                            "Current relationship-reference extraction does not consume the preceding predicate result.");
                    }
                    keys = ExtractReferenceKeys(
                        context,
                        observations,
                        relationship.Definition.SourceReference,
                        route.ChangeInput,
                        route.MaximumAffectedRoots);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported inverse-impact operation '{step.Operation}'.");
            }
        }

        var rootIdentities = keys.IsDefaultOrEmpty
            ? observationInput == root.Input.Id
                ? [.. observations.Select(static observation => observation.Identity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
                : []
            : keys;
        if (rootIdentities.IsDefaultOrEmpty)
            return [];
        if (rootIdentities.Length > route.MaximumAffectedRoots)
        {
            throw new MaterializationAffectedRootBoundExceededException(
                changeInput: route.ChangeInput,
                maximumAffectedRoots: route.MaximumAffectedRoots,
                actualAffectedRoots: rootIdentities.Length);
        }

        var currentRoots = await ReadAsync(
                context,
                new(
                    kind: MaterializationImpactObservationReadKind.IdentityLookup,
                    input: root.Input.Id,
                    shape: root.Shape,
                    logicalPartition: request.Change.Scope.LogicalPartition,
                    keys: rootIdentities,
                    maximumRows: route.MaximumAffectedRoots,
                    maximumBytes: route.MaximumReadBytes))
            .ConfigureAwait(false);
        var currentByIdentity = currentRoots.ToImmutableDictionary(
            static observation => observation.Identity,
            StringComparer.Ordinal);
        var roots = ImmutableArray.CreateBuilder<MaterializationAffectedRoot>(rootIdentities.Length);
        foreach (var identity in rootIdentities)
        {
            currentByIdentity.TryGetValue(identity, out var observation);
            roots.Add(new(
                input: root.Input.Id,
                identity: identity,
                state: observation is null ? MaterializationRootState.Absent : MaterializationRootState.Present,
                observation: observation));
        }
        return roots.MoveToImmutable();
    }

    async ValueTask<ImmutableArray<RelationQuerySourceReadObservation>> ReadAsync(
        OperationContext context,
        MaterializationImpactObservationReadRequest request)
    {
        var result = await reader(context, request).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The impact observation reader returned no result.");
        context.ThrowIfCancellationRequested();
        if (result.State is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound))
        {
            throw new InvalidOperationException(
                $"Impact read '{request.Kind}' for input '{request.Input.Value}' returned '{result.State}' instead of complete evidence.");
        }
        if ((long)result.Observations.Length > request.MaximumRows)
            throw new InvalidOperationException("An impact observation read exceeded its hard row bound.");
        foreach (var observation in result.Observations)
        {
            if (observation.Shape != request.Shape)
                throw new InvalidOperationException("An impact observation reader returned another canonical shape.");
            if (request.Kind == MaterializationImpactObservationReadKind.IdentityLookup
                && !request.Keys.Contains(observation.Identity, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("An impact identity lookup returned an unrequested observation.");
            }
        }
        return result.Observations;
    }

    static ImmutableArray<RelationQuerySourceReadObservation> Present(
        params RelationQuerySourceReadObservation?[] candidates) =>
        [.. candidates.Where(static candidate => candidate is not null).Cast<RelationQuerySourceReadObservation>()];

    static ImmutableArray<string> ExtractReferenceKeys(
        OperationContext context,
        ImmutableArray<RelationQuerySourceReadObservation> observations,
        FieldPath relationshipReference,
        RelationQueryInputId changeInput,
        long maximumKeys)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            context.ThrowIfCancellationRequested();
            var matching = observation.Fields
                .Where(field => field.Field.SemanticPath == relationshipReference)
                .ToArray();
            if (matching.Length == 0
                || matching.Any(static field => field.State != RelationQuerySourceReadFieldState.Value))
            {
                throw new InvalidOperationException(
                    $"Impact observation '{observation.Identity}' has no complete relationship reference '{relationshipReference}'.");
            }
            ImmutableArray<string>? extracted = null;
            foreach (var field in matching)
            {
                var state = RelationQueryReferenceKeyExtractor.Extract(
                    value: field.Value!.Value,
                    maximumKeys: maximumKeys,
                    cancellationToken: context.CancellationToken,
                    keys: out var values);
                if (state == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                {
                    throw new MaterializationAffectedRootBoundExceededException(
                        changeInput: changeInput,
                        maximumAffectedRoots: maximumKeys,
                        actualAffectedRoots: checked(maximumKeys + 1));
                }
                if (state != RelationQueryReferenceKeyExtractionState.Success)
                    throw new InvalidOperationException("An impact relationship reference is not a canonical string key or key array.");
                if (extracted is not null && !extracted.Value.SequenceEqual(values))
                    throw new InvalidOperationException("Repeated semantic relationship-reference fields disagree.");
                extracted = values;
            }
            foreach (var value in extracted!.Value)
            {
                if (!keys.Add(value))
                    continue;
                if ((long)keys.Count > maximumKeys)
                {
                    throw new MaterializationAffectedRootBoundExceededException(
                        changeInput: changeInput,
                        maximumAffectedRoots: maximumKeys,
                        actualAffectedRoots: keys.Count);
                }
            }
        }
        return [.. keys.Order(StringComparer.Ordinal)];
    }

    readonly record struct InputContract(
        QualifiedShapeId Shape,
        ImmutableArray<RelationQueryFieldInputContract> Fields);
}

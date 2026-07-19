using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Storage;

/// <summary>
/// Immutable, deterministic catalog of entity-backed canonical relation/query sources.
/// </summary>
/// <remarks>
/// Version 1 admits one active physical source for each exact graph-qualified shape. Selecting among replicas or
/// capability-equivalent placements requires an explicit future placement policy rather than registration order.
/// </remarks>
public sealed class EntityRelationQuerySourceCatalog
{
    readonly ImmutableDictionary<QualifiedShapeId, EntityRelationQuerySourceRegistration> sourcesByShape;

    /// <summary>Creates an immutable source catalog from explicit registrations.</summary>
    /// <param name="sources">Entity-backed source registrations to snapshot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sources"/> contains a null entry, repeats a graph-qualified shape, or repeats a physical
    /// source identity.
    /// </exception>
    public EntityRelationQuerySourceCatalog(IEnumerable<EntityRelationQuerySourceRegistration> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var normalized = sources.ToArray();
        if (normalized.Any(static source => source is null))
            throw new ArgumentException("Entity relation/query source registrations cannot contain null entries.", nameof(sources));
        if (normalized.GroupBy(static source => source.Shape).Any(static group => group.Count() > 1))
            throw new ArgumentException("An entity relation/query source catalog cannot repeat a graph-qualified shape.", nameof(sources));
        if (normalized.GroupBy(static source => source.Source.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("An entity relation/query source catalog cannot repeat a physical source identity.", nameof(sources));

        Sources =
        [
            .. normalized
                .OrderBy(static registration => registration.Shape.GraphId.Value, StringComparer.Ordinal)
                .ThenBy(static registration => registration.Shape.ShapeId.Value, StringComparer.Ordinal)
        ];
        SourceReaders =
        [
            .. Sources
                .Select(static registration => registration.Reader)
                .OrderBy(static reader => reader.Descriptor.Source.Value, StringComparer.Ordinal)
        ];
        sourcesByShape = Sources.ToImmutableDictionary(static source => source.Shape);
    }

    /// <summary>Registered entity sources in deterministic graph-and-shape order.</summary>
    public ImmutableArray<EntityRelationQuerySourceRegistration> Sources { get; }

    /// <summary>Registered source readers in deterministic physical-source order.</summary>
    public ImmutableArray<IRelationQuerySourceReader> SourceReaders { get; }

    /// <summary>Attempts to find the one registered source for an exact graph-qualified shape.</summary>
    /// <param name="shape">Exact graph-qualified shape to resolve.</param>
    /// <param name="source">The registered source when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="shape"/> has one registration; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="shape"/> is incomplete.</exception>
    public bool TryGetSource(
        QualifiedShapeId shape,
        [NotNullWhen(true)] out EntityRelationQuerySourceRegistration? source)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A source lookup requires a graph-qualified shape.", nameof(shape));
        return sourcesByShape.TryGetValue(shape, out source);
    }

    /// <summary>Authors deterministic, plan-affine placement from the registered source snapshot.</summary>
    /// <param name="plan">Exact demand-scoped compiled plan to place.</param>
    /// <returns>A complete authored placement or structured fail-closed placement diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public RelationQueryArtifactAuthoringResult<RelationQueryAuthoredPlacement> Place(
        CompiledRelationQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = RelationQueryPlacement.For(plan);
        Dictionary<RelationQuerySourceInstanceId, RelationQueryPlacementSourceHandle> handles = [];
        List<RelationQueryArtifactAuthoringDiagnostic> selectorDiagnostics = [];

        foreach (var input in plan.InputContract.Sources
                     .Where(static input => input.Role == RelationQuerySourceInputRole.RelationRoot)
                     .OrderBy(static input => input.Input.Id.Value, StringComparer.Ordinal))
        {
            var supplied = SuppliedSource(builder, plan, input);
            builder.Place(input, supplied).FieldsBySemanticPath();
        }

        foreach (var input in plan.InputContract.Sources
                     .Where(static input => input.Role != RelationQuerySourceInputRole.RelationRoot)
                     .OrderBy(static input => input.Input.Id.Value, StringComparer.Ordinal))
        {
            if (!sourcesByShape.TryGetValue(input.Shape, out var registration))
                continue;
            Configure(
                builder.Place(input, Source(builder, registration, handles)),
                registration,
                input.Fields,
                input.Input.Id,
                relationshipReference: null,
                selectorDiagnostics);
        }

        foreach (var input in plan.InputContract.Traversals
                     .OrderBy(static input => input.Input.Id.Value, StringComparer.Ordinal))
        {
            if (!sourcesByShape.TryGetValue(input.ResultShape, out var registration))
                continue;
            Configure(
                builder.Place(input, Source(builder, registration, handles)),
                registration,
                input.Fields,
                input.Input.Id,
                input.Input.Direction == RelationshipTraversalDirection.Inverse
                    ? input.Definition.SourceReference
                    : null,
                selectorDiagnostics);
        }

        var authored = builder.Build();
        if (selectorDiagnostics.Count == 0)
            return authored;
        return new(null, [.. authored.Diagnostics, .. selectorDiagnostics]);
    }

    /// <summary>Resolves a complete plan-affine physical placement.</summary>
    /// <param name="plan">Exact demand-scoped compiled plan to place.</param>
    /// <returns>The complete normalized canonical source placement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">
    /// A required source is unregistered, a source is incompatible with the plan, a selector policy fails, or
    /// placement authoring otherwise produces an error.
    /// </exception>
    public RelationQuerySourcePlacement Resolve(CompiledRelationQueryPlan plan) =>
        Place(plan).RequireValue().Placement;

    /// <summary>Creates the canonical evaluator over this immutable source snapshot.</summary>
    /// <param name="physicalPlanningPolicy">Explicit bounded physical-planning policy.</param>
    /// <param name="interpreter">Canonical interpreter, or <see langword="null"/> for the shared default.</param>
    /// <param name="requirementGapPolicy">
    /// Runtime requirement-gap policy, or <see langword="null"/> for the conventional policy.
    /// </param>
    /// <returns>An evaluator using <see cref="Resolve"/> and exactly <see cref="SourceReaders"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="physicalPlanningPolicy"/> is <see langword="null"/>.
    /// </exception>
    public RelationQueryEvaluator CreateEvaluator(
        RelationQueryPhysicalPlanningPolicy physicalPlanningPolicy,
        IRelationQueryInterpreter? interpreter = null,
        IRelationRequirementGapPolicy? requirementGapPolicy = null) =>
        new(
            Resolve,
            Guard.RequireNotNull(physicalPlanningPolicy),
            SourceReaders,
            interpreter,
            requirementGapPolicy);

    static RelationQueryPlacementSourceHandle Source(
        RelationQueryPlacementBuilder builder,
        EntityRelationQuerySourceRegistration registration,
        IDictionary<RelationQuerySourceInstanceId, RelationQueryPlacementSourceHandle> handles)
    {
        if (handles.TryGetValue(registration.Source.Id, out var existing))
            return existing;

        var instance = registration.Source;
        var handle = builder.Source(
            sourceKey: $"entity/{ShapeKey(registration.Shape)}",
            targetProfile: instance.TargetProfile,
            executionDomain: instance.ExecutionDomain,
            limits: instance.Limits,
            id: instance.Id);
        handles.Add(instance.Id, handle);
        return handle;
    }

    static RelationQueryPlacementSourceHandle SuppliedSource(
        RelationQueryPlacementBuilder builder,
        CompiledRelationQueryPlan plan,
        RelationQuerySourceInputContract input)
    {
        var inputKey = Uri.EscapeDataString(input.Input.Id.Value);
        var profile = new RelationQueryTargetCapabilityProfile(
            new("cohesive.storage.supplied-invocation"),
            new($"cohesive.storage.supplied-invocation/{inputKey}/v1"),
            [plan.Provenance.DefinitionDocument.SchemaVersion],
            [plan.Provenance.CompilerProfile],
            description: "Invocation-supplied canonical relation-root evidence.");
        return builder.Source(
            sourceKey: $"supplied/{inputKey}",
            targetProfile: profile,
            executionDomain: new($"domain/cohesive.storage/supplied/{inputKey}"),
            id: new($"source/cohesive.storage/supplied/{inputKey}"));
    }

    static void Configure(
        RelationQueryPlacementInputBuilder binding,
        EntityRelationQuerySourceRegistration registration,
        ImmutableArray<RelationQueryFieldInputContract> fields,
        RelationQueryInputId input,
        FieldPath? relationshipReference,
        ICollection<RelationQueryArtifactAuthoringDiagnostic> diagnostics)
    {
        binding.Identity(registration.IdentitySourceSelector);
        foreach (var field in fields.OrderBy(static field => field.Input.Id.Value, StringComparer.Ordinal))
        {
            if (TrySelect(
                    registration.FieldSourceSelector,
                    field.Input.Field.Path,
                    input,
                    "field",
                    diagnostics,
                    out var selector))
            {
                binding.Field(field.Input.Field.Path, selector);
            }
        }

        if (relationshipReference is { } reference
            && TrySelect(
                registration.RelationshipKeySourceSelector,
                reference,
                input,
                "relationship-key",
                diagnostics,
                out var relationshipSelector))
        {
            binding.RelationshipKey(relationshipSelector);
        }
    }

    static bool TrySelect(
        RelationQueryPlacementFieldSelector select,
        FieldPath path,
        RelationQueryInputId input,
        string role,
        ICollection<RelationQueryArtifactAuthoringDiagnostic> diagnostics,
        [NotNullWhen(true)] out string? selector)
    {
        try
        {
            selector = select(path);
            if (!string.IsNullOrWhiteSpace(selector))
                return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException
                                          and not StackOverflowException)
        {
            selector = null;
        }

        diagnostics.Add(new(
            RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid,
            DiagnosticSeverity.Error,
            $"Entity source selector policy failed to produce a non-empty {role} selector for semantic path '{path}'.",
            input,
            path,
            $"entity-source/input/{Uri.EscapeDataString(input.Value)}/{role}-selector"));
        return false;
    }

    static string ShapeKey(QualifiedShapeId shape) => string.Concat(
        Uri.EscapeDataString(shape.GraphId.Value),
        "/",
        Uri.EscapeDataString(shape.ShapeId.Value));
}

using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using CanonicalRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Drafts;

/// <summary>Provenance linking an acceptance result to the exact semantic draft revision consumed.</summary>
public sealed record RelationDraftAcceptanceProvenance
{
    /// <summary>Creates draft acceptance provenance.</summary>
    /// <param name="draftId">Lifecycle identity of the consumed draft.</param>
    /// <param name="draftFingerprint">Semantic content fingerprint of the consumed draft revision.</param>
    /// <param name="relationshipCatalogFingerprint">
    /// Fingerprint of the exact relationship catalog supplied to acceptance, if any.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="draftFingerprint"/> is <see langword="null"/>.</exception>
    public RelationDraftAcceptanceProvenance(
        RelationDraftId draftId,
        RelationDraftFingerprint draftFingerprint,
        RelationshipCatalogFingerprint? relationshipCatalogFingerprint = null
        )
    {
        DraftId = draftId;
        DraftFingerprint = Guard.RequireNotNull(draftFingerprint);
        RelationshipCatalogFingerprint = relationshipCatalogFingerprint;
    }

    /// <summary>Lifecycle identity of the consumed draft.</summary>
    public RelationDraftId DraftId { get; init; }

    /// <summary>Semantic content fingerprint of the consumed draft revision.</summary>
    public RelationDraftFingerprint DraftFingerprint { get; init; }

    /// <summary>
    /// Fingerprint of the exact relationship catalog supplied to acceptance, or
    /// <see langword="null"/> when no catalog was supplied.
    /// </summary>
    public RelationshipCatalogFingerprint? RelationshipCatalogFingerprint { get; init; }
}

/// <summary>Structured result of attempting to accept a portable relation draft.</summary>
public sealed class RelationDraftAcceptanceResult
{
    /// <summary>Creates a relation-draft acceptance result.</summary>
    /// <param name="definition">Canonical relation definition, or <see langword="null"/> when acceptance failed.</param>
    /// <param name="validation">Structured acceptance and canonical-validation diagnostics.</param>
    /// <param name="provenance">Exact draft revision consumed by acceptance.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="validation"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    internal RelationDraftAcceptanceResult(
        CanonicalRelationDefinition? definition,
        DocumentValidationResult validation,
        RelationDraftAcceptanceProvenance provenance
        )
    {
        Definition = definition;
        DefinitionFingerprint = definition is null ? null : RelationQueryDefinitionFingerprinter.Compute(definition);
        Validation = Guard.RequireNotNull(validation);
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Accepted canonical relation definition, or <see langword="null"/> when diagnostics contain errors.</summary>
    public CanonicalRelationDefinition? Definition { get; }

    /// <summary>
    /// Canonical semantic fingerprint of <see cref="Definition"/>, or <see langword="null"/> when
    /// acceptance failed.
    /// </summary>
    public RelationQueryDefinitionFingerprint? DefinitionFingerprint { get; }

    /// <summary>Structured acceptance and canonical-validation result.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Structured acceptance and canonical-validation diagnostics.</summary>
    public IReadOnlyList<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Exact draft revision consumed by acceptance, outside canonical relation identity.</summary>
    public RelationDraftAcceptanceProvenance Provenance { get; }

    /// <summary>Whether acceptance produced a canonical relation without error diagnostics.</summary>
    public bool IsAccepted => Definition is not null && Validation.IsValid;
}

/// <summary>
/// Explicitly accepts complete, shape-safe relation drafts into canonical relation/query IR.
/// </summary>
public static class RelationDraftAcceptor
{
    /// <summary>Attempts to accept a draft against exact shape and relationship snapshots.</summary>
    /// <param name="draft">Portable relation draft to accept.</param>
    /// <param name="shapeGraphs">Exact shape-graph snapshots referenced by the draft and relationship catalog.</param>
    /// <param name="relationshipCatalog">
    /// Exact relationship-catalog document required when the draft input contains relationship traversal nodes.
    /// </param>
    /// <returns>
    /// A canonical relation when every target is resolved safely and canonical validation succeeds;
    /// otherwise a result containing actionable diagnostics and no definition.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="draft"/> or <paramref name="shapeGraphs"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The draft or supplied catalog contains a value without a canonical JSON encoding while its
    /// semantic fingerprint is computed or validated.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// The draft or supplied catalog cannot be serialized using its canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The draft or supplied catalog contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public static RelationDraftAcceptanceResult Accept(RelationDraft draft, IEnumerable<ShapeGraph> shapeGraphs, RelationshipCatalogDocument? relationshipCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(shapeGraphs);

        var provenance = new RelationDraftAcceptanceProvenance(
            draft.Id,
            RelationDraftFingerprinter.Compute(draft),
            relationshipCatalog?.CatalogFingerprint
            );
        List<DocumentValidationDiagnostic> diagnostics = [];
        diagnostics.AddRange(RelationDraftValidator.Validate(draft).Diagnostics);

        var graphIndex = IndexGraphs(shapeGraphs, diagnostics);
        var targetShape = ResolveShape(
            draft.Projection?.ResultShape ?? default,
            graphIndex.ById,
            role: "target",
            location: "/draft/projection/resultShape",
            diagnostics
            );
        var selected = ResolveAssignments(draft, targetShape, diagnostics);
        var hasTraversal = draft.Input?.Nodes.OfType<TraverseRelationshipQueryNode>().Any() == true;
        if (hasTraversal && relationshipCatalog is null)
        {
            Add(diagnostics,
                "relationDraft.relationshipCatalog.required",
                "Acceptance requires an explicit relationship-catalog snapshot because the draft contains relationship traversal nodes.",
                "/relationshipCatalog");
        }

        if (draft.Input is null
            || draft.Projection is null
            || targetShape is null
            || selected.IsDefaultOrEmpty
            || draft.RelationId is null
            || draft.Name is null
            || (!draft.Input.Nodes.IsDefault
                && draft.Input.Nodes.Any(static node => node is null))
            || (!draft.Input.Parameters.IsDefault
                && draft.Input.Parameters.Any(static parameter => parameter is null)))
        {
            return Failure(provenance, diagnostics);
        }

        var project = new ProjectQueryNode(
            draft.Projection.Id,
            draft.Projection.Input,
            draft.Projection.ResultBinding,
            draft.Projection.ResultShape,
            [
                .. selected
                    .Select(static assignment => new ProjectionAssignment(
                        assignment.Slot.Id,
                        assignment.Slot.Target,
                        assignment.Candidate.Value))
                    .OrderBy(static assignment => assignment.Id.Value, StringComparer.Ordinal)
            ]);
        var definition = new CanonicalRelationDefinition(
            draft.RelationId,
            draft.Name,
            new LogicalQueryDefinition(
                [.. draft.Input.Nodes, project],
                draft.Input.Parameters),
            draft.RootBinding,
            new RelationOutputDefinition(
                project.Id,
                project.ResultShape,
                draft.OutputMode,
                draft.OutputKey),
            draft.Invariants);

        var catalogDocument = relationshipCatalog
                              ?? RelationshipCatalogDocument.FromCatalog(RelationshipCatalog.Empty);
        if (catalogDocument.Catalog is not null)
        {
            diagnostics.AddRange(
                RelationshipCatalogValidator.Validate(catalogDocument.Catalog, graphIndex.ValidGraphs)
                    .Diagnostics);
        }

        var canonicalValidation = RelationQueryDefinitionValidator.ValidateWithCatalog(
            definition,
            catalogDocument);
        diagnostics.AddRange(canonicalValidation.Diagnostics);

        ValidateReferencedShapes(
            canonicalValidation.BindingShapes,
            graphIndex.ById,
            diagnostics);

        ValidateSelectedAssignments(
            draft,
            selected,
            targetShape!,
            canonicalValidation.BindingShapes,
            graphIndex.ById,
            diagnostics);

        if (HasErrors(diagnostics))
            return Failure(provenance, diagnostics);

        return new(
            definition,
            CreateValidation(diagnostics),
            provenance);
    }

    static ImmutableArray<SelectedAssignment> ResolveAssignments(
        RelationDraft draft,
        Shape? targetShape,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        if (draft.Projection is null || targetShape is null)
            return [];

        var targetFields = targetShape.Fields.ToDictionary(static field => field.Name.Value, StringComparer.Ordinal);
        HashSet<string> coveredTargets = new(StringComparer.Ordinal);
        var selected = ImmutableArray.CreateBuilder<SelectedAssignment>();

        foreach (var slot in draft.Projection.Assignments.IsDefault ? [] : draft.Projection.Assignments)
        {
            if (slot is null)
                continue;

            var location = SlotLocation(slot.Id);
            if (!TryGetTopLevelField(slot.Target, out var targetName))
            {
                Add(
                    diagnostics,
                    "relationDraft.slot.targetNotTopLevel",
                    $"Assignment target '{slot.Target}' is not a top-level target field.",
                    $"{location}/target");
                continue;
            }
            if (!targetFields.TryGetValue(targetName, out var targetField))
            {
                Add(
                    diagnostics,
                    "relationDraft.slot.targetUnknown",
                    $"Assignment target '{slot.Target}' does not exist on target shape '{targetShape.Id.Value}'.",
                    $"{location}/target");
                continue;
            }
            if (targetField.Role == FieldRole.Computed)
            {
                Add(
                    diagnostics,
                    "relationDraft.slot.targetComputed",
                    $"Computed target field '{targetName}' is governed by its shape computation and cannot have a draft assignment slot.",
                    $"{location}/target");
                continue;
            }
            coveredTargets.Add(targetName);

            switch (slot.Resolution)
            {
                case SelectedRelationDraftAssignmentResolution resolution:
                {
                    var candidate = slot.Candidates.FirstOrDefault(candidate =>
                        candidate is not null && candidate.Id == resolution.CandidateId);
                    if (candidate?.Value is not null)
                        selected.Add(new(slot, targetField, candidate));
                    break;
                }
                case OmittedRelationDraftAssignmentResolution:
                    if (targetField.Presence != FieldPresence.Optional)
                    {
                        Add(
                            diagnostics,
                            "relationDraft.resolution.requiredOmitted",
                            $"Required target field '{targetName}' cannot be omitted.",
                            $"{location}/resolution");
                    }
                    break;
                case UnresolvedRelationDraftAssignmentResolution unresolved:
                    Add(
                        diagnostics,
                        "relationDraft.resolution.unresolved",
                        $"Target field '{targetName}' remains unresolved: {string.Join(", ", unresolved.Reasons)}.",
                        $"{location}/resolution");
                    break;
                case AmbiguousRelationDraftAssignmentResolution ambiguous:
                    Add(
                        diagnostics,
                        "relationDraft.resolution.ambiguous",
                        $"Target field '{targetName}' remains ambiguous between {ambiguous.CandidateIds.Length} candidates.",
                        $"{location}/resolution");
                    break;
            }
        }

        foreach (var targetField in targetShape.Fields
                     .Where(static field => field.Role != FieldRole.Computed)
                     .OrderBy(static field => field.Name.Value, StringComparer.Ordinal))
        {
            if (coveredTargets.Contains(targetField.Name.Value))
                continue;

            Add(
                diagnostics,
                "relationDraft.slot.missing",
                $"Target field '{targetField.Name.Value}' has no assignment slot.",
                "/draft/projection/assignments");
        }

        if (selected.Count == 0 && !HasErrors(diagnostics))
        {
            Add(
                diagnostics,
                "relationDraft.projection.assignmentsEmpty",
                "Acceptance cannot produce a canonical projection with no selected assignments.",
                "/draft/projection/assignments");
        }

        return selected.ToImmutable();
    }

    static void ValidateSelectedAssignments(
        RelationDraft draft,
        ImmutableArray<SelectedAssignment> selected,
        Shape targetShape,
        ImmutableArray<RelationQueryBindingShape> bindingShapes,
        IReadOnlyDictionary<GraphId, ShapeGraph> graphs,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var visibleBindings = bindingShapes
            .Where(binding => binding.Node == draft.Projection.Input)
            .ToDictionary(static binding => binding.Binding);

        foreach (var assignment in selected)
        {
            var location = $"{SlotLocation(assignment.Slot.Id)}/candidates/{assignment.Candidate.Id.Value}";
            if (assignment.Candidate.Value is not FieldExpr { Binding: { } binding } field)
            {
                Add(
                    diagnostics,
                    "relationDraft.candidate.expressionUnsupported",
                    "V1 acceptance supports only explicit binding-qualified field expressions.",
                    $"{location}/value");
                continue;
            }
            if (!TryGetTopLevelField(field.Path, out var sourceFieldName))
            {
                Add(
                    diagnostics,
                    "relationDraft.assignment.structureUnsupported",
                    $"V1 acceptance supports only top-level source fields; '{field.Path}' requires structural transformation.",
                    $"{location}/value/path");
                continue;
            }
            if (!visibleBindings.TryGetValue(binding, out var sourceBinding))
            {
                Add(
                    diagnostics,
                    "relationDraft.candidate.bindingMissing",
                    $"Candidate references binding '{binding.Value}' that is not visible at projection input '{draft.Projection.Input.Value}'.",
                    $"{location}/value/binding");
                continue;
            }
            if (sourceBinding.Shape is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.candidate.bindingShapeUnknown",
                    $"The semantic shape of binding '{binding.Value}' cannot be established statically.",
                    $"{location}/value/binding");
                continue;
            }

            var sourceShape = ResolveShape(
                sourceBinding.Shape.Value,
                graphs,
                role: "candidate source",
                location: $"{location}/value/binding",
                diagnostics);
            if (sourceShape is null)
                continue;
            if (!sourceShape.TryGetField(sourceFieldName, out var sourceField))
            {
                Add(
                    diagnostics,
                    "relationDraft.candidate.pathUnknown",
                    $"Candidate source shape '{sourceShape.Id.Value}' does not contain top-level field '{sourceFieldName}'.",
                    $"{location}/value/path");
                continue;
            }

            if (sourceBinding.Availability == RelationQueryBindingAvailability.MayBeAbsent
                && assignment.TargetField.Presence == FieldPresence.Required)
            {
                Add(
                    diagnostics,
                    "relationDraft.assignment.bindingPresenceUnsafe",
                    $"Binding '{binding.Value}' is optional at the projection input and cannot safely populate required target field '{targetShape.Id.Value}.{assignment.TargetField.Name.Value}'.",
                    location);
            }

            foreach (var issue in DirectFieldAssignmentCompatibility.Evaluate(
                         sourceField,
                         sourceBinding.Shape.Value.GraphId,
                         assignment.TargetField,
                         draft.Projection.ResultShape.GraphId))
            {
                Add(
                    diagnostics,
                    issue.Code,
                    $"Source field '{sourceShape.Id.Value}.{sourceFieldName}' cannot safely populate target field '{targetShape.Id.Value}.{assignment.TargetField.Name.Value}': {issue.Message}",
                    location);
            }

        }
    }

    static void ValidateReferencedShapes(
        ImmutableArray<RelationQueryBindingShape> bindingShapes,
        IReadOnlyDictionary<GraphId, ShapeGraph> graphs,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var bindingShape in bindingShapes
                     .Where(static bindingShape => bindingShape.Shape is not null)
                     .DistinctBy(static bindingShape => bindingShape.Shape))
        {
            _ = ResolveShape(
                bindingShape.Shape!.Value,
                graphs,
                role: $"binding '{bindingShape.Binding.Value}'",
                location: $"/draft/input/nodes/{bindingShape.Node.Value}",
                diagnostics);
        }
    }

    static GraphIndex IndexGraphs(
        IEnumerable<ShapeGraph> shapeGraphs,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<GraphId, ShapeGraph> byId = [];
        List<ShapeGraph> valid = [];
        var index = 0;
        foreach (var graph in shapeGraphs)
        {
            if (graph is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.shapeGraph.missing",
                    "A supplied shape-graph snapshot cannot be null.",
                    $"/shapeGraphs/{index}");
            }
            else if (!byId.TryAdd(graph.Id, graph))
            {
                Add(
                    diagnostics,
                    "relationDraft.shapeGraph.duplicateId",
                    $"Multiple supplied shape graphs have id '{graph.Id.Value}'.",
                    $"/shapeGraphs/{index}/id");
            }
            else
            {
                foreach (var graphDiagnostic in graph.Diagnostics.Where(static diagnostic =>
                             diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    Add(
                        diagnostics,
                        "relationDraft.shapeGraph.invalid",
                        $"Shape graph '{graph.Id.Value}' is invalid ({graphDiagnostic.Id.Value}): {graphDiagnostic.Message}",
                        $"/shapeGraphs/{index}");
                }

                if (!graph.HasErrors)
                    valid.Add(graph);
            }
            index++;
        }
        return new(byId, valid);
    }

    static Shape? ResolveShape(
        QualifiedShapeId id,
        IReadOnlyDictionary<GraphId, ShapeGraph> graphs,
        string role,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (!graphs.TryGetValue(id.GraphId, out var graph)
            || !graph.TryGetShape(id.ShapeId, out var shape))
        {
            Add(
                diagnostics,
                "relationDraft.shapeGraph.shapeUnknown",
                $"The {role} shape '{id}' is not present in the supplied shape-graph snapshots.",
                location);
            return null;
        }
        return shape;
    }

    static bool TryGetTopLevelField(FieldPath path, out string fieldName)
    {
        if (path.Segments.Length == 1
            && path.Segments[0] is { Kind: SegmentKind.Field, Segment: { } segment }
            && !string.IsNullOrWhiteSpace(segment))
        {
            fieldName = segment;
            return true;
        }

        fieldName = string.Empty;
        return false;
    }

    static RelationDraftAcceptanceResult Failure(
        RelationDraftAcceptanceProvenance provenance,
        IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        new(
            definition: null,
            CreateValidation(diagnostics),
            provenance);

    static DocumentValidationResult CreateValidation(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        DocumentValidationResult.FromDiagnostics(
            diagnostics
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal));

    static bool HasErrors(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    static string SlotLocation(QueryAssignmentId id) =>
        $"/draft/projection/assignments/{id.Value}";

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, location));

    readonly record struct GraphIndex(
        IReadOnlyDictionary<GraphId, ShapeGraph> ById,
        IReadOnlyList<ShapeGraph> ValidGraphs);

    readonly record struct SelectedAssignment(
        RelationDraftAssignmentSlot Slot,
        FieldDefinition TargetField,
        RelationDraftCandidate Candidate);
}

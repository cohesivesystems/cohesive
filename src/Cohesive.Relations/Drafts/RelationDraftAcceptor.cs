using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Model.Expressions;
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
    /// <remarks>
    /// Selected candidates may be explicit binding-qualified fields or statically keyed object construction
    /// composed from those fields and nested objects. Field paths may navigate single-valued inline or named
    /// structures; ancestor presence and nullability are preserved. Object children are checked against their
    /// own target contracts, even when the containing target is optional. Whole collection fields can be copied
    /// when contracts match. Collection select establishes an isolated current-item scope and checks each
    /// selector against the target element contract. Its source must be present and non-null, including through
    /// an explicit coalesce with a compatible constant fallback. Portable scalar, null and empty-collection
    /// constants are checked against their target contracts. Named scalar and structural literals are excluded.
    /// Collection join may retain items matching a non-null scalar constant against a resolved item key.
    /// Filtering preserves source element contracts without refining optional children. Conditional candidates
    /// may compare a present resolved source with a non-null scalar constant; both branches must independently
    /// satisfy the target contract. Predicates do not refine either branch.
    /// Implicit element/index traversal, conversions, dynamic object keys and nested target slots are not admitted.
    /// </remarks>
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
            new RelationQueryShapeResolver([.. graphIndex.ValidGraphs]),
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
        RelationQueryShapeResolver shapeResolver,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var visibleBindings = bindingShapes
            .Where(binding => binding.Node == draft.Projection.Input)
            .ToDictionary(static binding => binding.Binding);

        foreach (var assignment in selected)
        {
            ValidateValue(assignment.Candidate.Value, ValueContract.FromField(assignment.TargetField),
                assignment.Slot.Target, $"{SlotLocation(assignment.Slot.Id)}/candidates/{assignment.Candidate.Id.Value}/value");
        }

        void ValidateValue(Expr expression, ValueContract target, FieldPath targetPath, string location,
            (GraphId Graph, ValueContract Contract)? item = null)
        {
            if (expression is ConditionalExpr conditional)
            {
                ValidateConditional(conditional, target, targetPath, location, item);
                return;
            }
            if (expression is ConstantExpr constant)
            {
                ValidateConstant(constant.Value, target, draft.Projection.ResultShape.GraphId, location);
                return;
            }
            if (expression is CallExpr { Function: ExprFunctionNames.Object } construction)
            {
                ValidateObject(construction, target, targetPath, location, item);
                return;
            }
            if (expression is CallExpr { Function: ExprFunctionNames.Select } selection)
            {
                ValidateSelect(selection, target, targetPath, location, item);
                return;
            }
            if (TryResolveValue(expression, location, item, out var source, out var sourceGraph))
            {
                if (expression is FieldExpr { Binding: { } binding }
                    && visibleBindings[binding].Availability == RelationQueryBindingAvailability.MayBeAbsent
                    && target.Presence == FieldPresence.Required)
                    Add(diagnostics, "relationDraft.assignment.bindingPresenceUnsafe",
                        $"Binding '{binding.Value}' is optional and cannot safely populate required target '{targetShape.Id.Value}.{targetPath}'.", location);
                ValidateCompatibility(source, sourceGraph, target, targetPath, location);
            }
        }

        bool TryResolveValue(Expr expression, string location,
            (GraphId Graph, ValueContract Contract)? item, out ValueContract contract, out GraphId graph)
        {
            contract = null!;
            graph = default;
            if (expression is CallExpr { Function: ExprFunctionNames.Select } projection)
            {
                if (!TryResolveSelectSource(projection, location, item, out var sourceItem, out graph))
                    return false;
                if (!TryResolveValue(projection.Arguments[1], $"{location}/arguments/1", (graph, sourceItem), out var element, out var elementGraph))
                    return false;
                if (element.Presence != FieldPresence.Required || element.Nullability != FieldNullability.NonNullable)
                {
                    Add(diagnostics, "relationDraft.select.elementMayBeAbsent", "Selected elements must be present and non-null; declare any default explicitly.", location);
                    return false;
                }
                contract = new(new ArrayTypeRef(element.GetEffectiveType()!));
                graph = elementGraph;
                if (projection.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                    && projection.ReturnType != contract.GetEffectiveType())
                    Add(diagnostics, "relationDraft.select.returnTypeMismatch", "Declared select result must retain its element type.", $"{location}/returnType");
                return true;
            }
            if (expression is CallExpr { Function: ExprFunctionNames.Single or ExprFunctionNames.ParseDecimal } conversion)
            {
                if (conversion.Arguments.IsDefault || conversion.Arguments.Length != 1)
                {
                    Add(diagnostics, "relationDraft.conversion.argumentsInvalid", "Conversion requires exactly one source argument.", location);
                    return false;
                }
                if (!TryResolveValue(conversion.Arguments[0], $"{location}/arguments/0", item, out var source, out graph))
                    return false;
                if (source.Presence != FieldPresence.Required || source.Nullability != FieldNullability.NonNullable)
                {
                    Add(diagnostics, "relationDraft.conversion.sourceMayBeAbsent", "Conversion requires a present, non-null source; declare any default explicitly.", location);
                    return false;
                }
                var type = source.GetEffectiveType();
                if (conversion.Function == ExprFunctionNames.Single && type is ArrayTypeRef array)
                    contract = new(array.ElementType);
                else if (conversion.Function == ExprFunctionNames.ParseDecimal && type is ScalarTypeRef { Kind: ScalarTypeKind.String })
                    contract = new(new ScalarTypeRef(ScalarTypeKind.Decimal));
                else
                {
                    Add(diagnostics, "relationDraft.conversion.sourceUnsupported", "Single requires a collection; parseDecimal requires text.", location);
                    return false;
                }
                if (conversion.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                    && conversion.ReturnType != contract.GetEffectiveType())
                    Add(diagnostics, "relationDraft.conversion.returnTypeMismatch", "Declared conversion result must retain the operation's result type.", $"{location}/returnType");
                return true;
            }
            if (expression is CallExpr { Function: ExprFunctionNames.Join } join)
            {
                if (join.Arguments.IsDefault || join.Arguments.Length != 3)
                {
                    Add(diagnostics, "relationDraft.join.argumentsInvalid", "Collection join requires a left key, item key and source collection.", location);
                    return false;
                }
                if (!TryResolveValue(join.Arguments[2], $"{location}/arguments/2", item, out var source, out graph))
                    return false;
                if (source.GetEffectiveType() is not ArrayTypeRef array)
                {
                    Add(diagnostics, "relationDraft.join.sourceUnsupported", "Collection join requires a statically known collection.", location);
                    return false;
                }
                if (source.Presence != FieldPresence.Required || source.Nullability != FieldNullability.NonNullable)
                {
                    Add(diagnostics, "relationDraft.join.sourceMayBeAbsent", "Collection join requires a present, non-null source; declare any default explicitly.", location);
                    return false;
                }
                if (join.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                    && join.ReturnType != source.GetEffectiveType())
                    Add(diagnostics, "relationDraft.join.returnTypeMismatch", "Declared join result type must retain the source collection type.", $"{location}/returnType");
                if (join.Arguments[0] is not ConstantExpr left
                    || left.Value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined or ObservationValueKind.Array or ObservationValueKind.Object)
                {
                    Add(diagnostics, "relationDraft.join.keyUnsupported", "Draft collection joins require an explicit non-null scalar constant left key.", $"{location}/arguments/0");
                    return false;
                }
                if (!TryResolveValue(join.Arguments[1], $"{location}/arguments/1", (graph, new(array.ElementType)), out var key, out var keyGraph)
                    || !ValidateConstant(left.Value, new(key.Type, key.Shape, key.Cardinality), keyGraph, $"{location}/arguments/0"))
                    return false;
                // Filtering does not refine child presence/nullability or change graph-local type identity.
                contract = new(source.Type, source.Shape, source.Cardinality);
                return true;
            }
            if (expression is CallExpr { Function: ExprFunctionNames.Coalesce } coalesce)
            {
                if (coalesce.Arguments.IsDefault || coalesce.Arguments.Length != 2)
                {
                    Add(diagnostics, "relationDraft.default.argumentsInvalid", "Coalesce requires a source value and one fallback.", location);
                    return false;
                }
                if (coalesce.Arguments[1] is not ConstantExpr fallback)
                {
                    Add(diagnostics, "relationDraft.default.fallbackUnsupported", "Draft defaults require an explicit portable constant fallback.", $"{location}/arguments/1");
                    return false;
                }
                if (!TryResolveValue(coalesce.Arguments[0], $"{location}/arguments/0", item, out var source, out graph))
                    return false;
                var fallbackContract = new ValueContract(source.Type, source.Shape, source.Cardinality,
                    nullability: fallback.Value.Kind == ObservationValueKind.Null
                        ? FieldNullability.Nullable : FieldNullability.NonNullable);
                if (coalesce.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                    && coalesce.ReturnType != source.GetEffectiveType())
                    Add(diagnostics, "relationDraft.default.returnTypeMismatch", "Declared default result type must match the source's effective type.", $"{location}/returnType");
                if (!ValidateConstant(fallback.Value, fallbackContract, graph, $"{location}/arguments/1"))
                    return false;
                // Null is an explicit present fallback, not evidence that a non-null value exists.
                // Retain a stronger source guarantee when the fallback cannot be reached.
                var canUseFallback = source.Presence != FieldPresence.Required || source.Nullability != FieldNullability.NonNullable;
                contract = new(source.Type, source.Shape, source.Cardinality,
                    nullability: canUseFallback ? fallbackContract.Nullability : FieldNullability.NonNullable);
                return true;
            }
            if (expression is CurrentItemExpr
                || expression is FieldExpr { Binding: null } itemField
                    && !itemField.Path.Segments.IsDefaultOrEmpty
                    && itemField.Path.Segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
            {
                if (item is null)
                {
                    Add(diagnostics, "relationDraft.candidate.itemScopeMissing", "Current-item reads require a collection selector scope.", location);
                    return false;
                }
                graph = item.Value.Graph;
                var path = expression is FieldExpr fieldRead ? fieldRead.Path.Segments.AsSpan()[1..] : [];
                if (shapeResolver.TryGetValueContract(graph, item.Value.Contract, path, out contract))
                    return true;
                Add(diagnostics, "relationDraft.candidate.pathUnknown", "Current-item path does not resolve through single-valued structural fields.", location);
                return false;
            }
            if (expression is not FieldExpr { Binding: { } binding } field)
            {
                Add(diagnostics, "relationDraft.candidate.expressionUnsupported",
                    "Acceptance supports binding-qualified fields, scoped item reads, explicit defaults and literals, static objects, collection select/join/single and exact decimal parsing.", location);
                return false;
            }
            if (field.Path.Segments.IsDefaultOrEmpty
                || field.Path.Segments.Any(static segment => segment.Kind != SegmentKind.Field))
            {
                Add(diagnostics, "relationDraft.assignment.structureUnsupported",
                    $"Direct field acceptance supports only field-name navigation; '{field.Path}' requires an explicit collection or other structural transformation.",
                    $"{location}/path");
                return false;
            }
            if (!visibleBindings.TryGetValue(binding, out var sourceBinding))
            {
                Add(diagnostics, "relationDraft.candidate.bindingMissing",
                    $"Candidate references binding '{binding.Value}' that is not visible at projection input '{draft.Projection.Input.Value}'.",
                    $"{location}/binding");
                return false;
            }
            if (sourceBinding.Shape is null)
            {
                Add(diagnostics, "relationDraft.candidate.bindingShapeUnknown",
                    $"The semantic shape of binding '{binding.Value}' cannot be established statically.", $"{location}/binding");
                return false;
            }

            var sourceShape = ResolveShape(sourceBinding.Shape.Value, graphs, role: "candidate source",
                location: $"{location}/binding", diagnostics);
            if (sourceShape is null)
                return false;
            if (!shapeResolver.TryGetFieldContract(sourceBinding.Shape.Value, field.Path, out var sourceContract))
            {
                Add(diagnostics, "relationDraft.candidate.pathUnknown",
                    $"Candidate path '{field.Path}' cannot be resolved from source shape '{sourceShape.Id.Value}' through single-valued structural fields.",
                    $"{location}/path");
                return false;
            }

            graph = sourceBinding.Shape.Value.GraphId;
            contract = sourceBinding.Availability == RelationQueryBindingAvailability.MayBeAbsent
                ? new(sourceContract.Type, sourceContract.Shape, sourceContract.Cardinality, FieldPresence.Optional, sourceContract.Nullability)
                : sourceContract;
            return true;
        }

        void ValidateConditional(ConditionalExpr conditional, ValueContract target, FieldPath targetPath,
            string location, (GraphId Graph, ValueContract Contract)? item)
        {
            if (conditional.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                && conditional.ReturnType != target.GetEffectiveType())
                Add(diagnostics, "relationDraft.conditional.returnTypeMismatch",
                    "Declared conditional result type must match the target's effective type.", $"{location}/returnType");
            // Keep admission bounded to explicit code decisions; the canonical evaluator owns equality and laziness.
            if (conditional.Test is not BinaryExpr { Operator: BinaryOperator.Eq, Right: ConstantExpr key } equality
                || key.Value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined or ObservationValueKind.Array or ObservationValueKind.Object)
                Add(diagnostics, "relationDraft.conditional.testUnsupported",
                    "Conditional acceptance requires equality between a resolved source value and a non-null portable scalar constant.", $"{location}/test");
            else if (TryResolveValue(equality.Left, $"{location}/test/left", item, out var source, out var sourceGraph))
            {
                // Canonical equality fails on missing input; null is a concrete nonmatching value.
                if (source.Presence != FieldPresence.Required)
                    Add(diagnostics, "relationDraft.conditional.sourceMayBeAbsent",
                        "Conditional equality requires a present source; declare a missing-value default explicitly.", $"{location}/test/left");
                ValidateConstant(key.Value, new(source.Type, source.Shape, source.Cardinality), sourceGraph, $"{location}/test/right");
            }
            // Neither branch inherits refinements from the predicate. Both must meet the complete target contract.
            ValidateValue(conditional.IfTrue, target, targetPath, $"{location}/ifTrue", item);
            ValidateValue(conditional.IfFalse, target, targetPath, $"{location}/ifFalse", item);
        }

        bool ValidateConstant(ObservationValue value, ValueContract target, GraphId graphId, string location)
        {
            // Undefined has no distinct portable JSON encoding. Never admit it as an authored default.
            graphs.TryGetValue(graphId, out var graph);
            var namedEnum = target.Cardinality == FieldCardinality.Single && target.Type is NamedTypeRef named
                && graph is not null && graph.TryGetType(named.TypeId, out var definition) && definition is TypeDefinition.Enum;
            var supported = value.Kind == ObservationValueKind.Null
                || (value.Kind == ObservationValueKind.Array && value.Array.IsEmpty
                    && target.GetEffectiveType() is ArrayTypeRef)
                || (target.Cardinality == FieldCardinality.Single
                    && (namedEnum || target.Type is ScalarTypeRef or EnumTypeRef or QuantityTypeRef or EntityReferenceTypeRef or JsonTypeRef)
                    && value.Kind is not (ObservationValueKind.Array or ObservationValueKind.Object or ObservationValueKind.Undefined));
            if (!supported)
            {
                Add(diagnostics, "relationDraft.constant.unsupported", "Accepted constants are portable scalars, null or an empty collection; structural values need explicit construction.", location);
                return false;
            }
            if (!target.IsSatisfiedByConstant(value)
                || (namedEnum && value.Kind != ObservationValueKind.Null
                    && !ObservationValidator.TryValidateAgainstType(value, target.Type!, out _, graph)))
            {
                Add(diagnostics, "relationDraft.constant.incompatible", "Constant does not satisfy the target's type, cardinality or nullability contract.", location);
                return false;
            }
            return true;
        }

        void ValidateCompatibility(ValueContract source, GraphId sourceGraph, ValueContract target,
            FieldPath targetPath, string location)
        {
            foreach (var issue in DirectFieldAssignmentCompatibility.Evaluate(
                         source, sourceGraph, target, draft.Projection.ResultShape.GraphId))
                Add(diagnostics, issue.Code,
                    $"Source value cannot safely populate target '{targetShape.Id.Value}.{targetPath}': {issue.Message}", location);
        }

        // Both target-driven assignment admission and inferred nested projections use this source boundary.
        bool TryResolveSelectSource(CallExpr selection, string location,
            (GraphId Graph, ValueContract Contract)? item, out ValueContract sourceItem, out GraphId sourceGraph)
        {
            sourceItem = null!;
            sourceGraph = default;
            if (selection.Arguments.IsDefault || selection.Arguments.Length != 2)
            {
                Add(diagnostics, "relationDraft.select.argumentsInvalid", "Collection select requires a source and one selector expression.", location);
                return false;
            }
            var sourceLocation = $"{location}/arguments/0";
            if (!TryResolveValue(selection.Arguments[0], sourceLocation, item, out var source, out sourceGraph))
                return false;
            if (source.GetEffectiveType() is not ArrayTypeRef sourceArray)
            {
                Add(diagnostics, "relationDraft.select.sourceUnsupported", "Collection select requires a statically known collection source.", sourceLocation);
                return false;
            }
            if (source.Presence != FieldPresence.Required || source.Nullability != FieldNullability.NonNullable)
            {
                Add(diagnostics, "relationDraft.select.sourceMayBeAbsent", "Collection select requires a present, non-null source, including its containing fields and binding.", sourceLocation);
                return false;
            }
            sourceItem = new(sourceArray.ElementType);
            return true;
        }

        void ValidateSelect(CallExpr selection, ValueContract target, FieldPath targetPath, string location,
            (GraphId Graph, ValueContract Contract)? item)
        {
            if (!TryResolveSelectSource(selection, location, item, out var sourceItem, out var sourceGraph))
                return;
            if (target.GetEffectiveType() is not ArrayTypeRef targetArray)
            {
                Add(diagnostics, "relationDraft.select.targetUnsupported", "Collection select requires a collection target.", location);
                return;
            }
            if (selection.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                && selection.ReturnType != target.GetEffectiveType())
                Add(diagnostics, "relationDraft.select.returnTypeMismatch", "Declared select result type does not match the target collection.", $"{location}/returnType");
            ValidateValue(selection.Arguments[1], new(targetArray.ElementType), targetPath,
                $"{location}/arguments/1", (sourceGraph, sourceItem));
        }

        void ValidateObject(CallExpr construction, ValueContract target, FieldPath targetPath, string location,
            (GraphId Graph, ValueContract Contract)? item)
        {
            if (target.Cardinality != FieldCardinality.Single
                || !shapeResolver.TryGetStructuralFields(draft.Projection.ResultShape.GraphId, target.Type, out var children))
            {
                Add(diagnostics, "relationDraft.object.targetUnsupported",
                    $"Object construction for '{targetPath}' requires a single-valued inline or named structural target.", location);
                return;
            }
            if (construction.ReturnType is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" }
                && construction.ReturnType != target.Type)
            {
                Add(diagnostics, "relationDraft.object.returnTypeMismatch",
                    $"Declared object result type does not match target '{targetPath}'.", $"{location}/returnType");
            }
            var arguments = construction.Arguments.IsDefault ? ImmutableArray<Expr>.Empty : construction.Arguments;
            if (arguments.Length % 2 != 0)
            {
                Add(diagnostics, "relationDraft.object.argumentsInvalid",
                    "Object construction requires alternating constant field names and value expressions.", $"{location}/arguments");
                return;
            }

            Dictionary<string, StructuralField> fieldsByName = new(StringComparer.Ordinal);
            foreach (var child in children)
            {
                if (!fieldsByName.TryAdd(child.Name.Value, child))
                {
                    Add(diagnostics, "relationDraft.object.targetInvalid",
                        $"Object target '{targetPath}' has ambiguous child '{child.Name.Value}'.", location);
                    return;
                }
            }
            HashSet<string> supplied = new(StringComparer.Ordinal);
            for (var index = 0; index < arguments.Length; index += 2)
            {
                var keyLocation = $"{location}/arguments/{index}";
                if (arguments[index] is not ConstantExpr { Value.Kind: ObservationValueKind.String } key
                    || string.IsNullOrWhiteSpace(key.Value.String))
                {
                    Add(diagnostics, "relationDraft.object.keyUnsupported",
                        "Accepted object keys must be non-empty constant strings; dynamic keys require further semantics.", keyLocation);
                    continue;
                }
                var name = key.Value.String;
                if (!supplied.Add(name))
                {
                    Add(diagnostics, "relationDraft.object.keyDuplicate", $"Object field '{name}' is declared more than once.", keyLocation);
                    continue;
                }
                if (!fieldsByName.TryGetValue(name, out var child))
                {
                    Add(diagnostics, "relationDraft.object.fieldUnknown", $"Object field '{name}' is not declared on target '{targetPath}'.", keyLocation);
                    continue;
                }
                if (child.Role == FieldRole.Computed)
                {
                    Add(diagnostics, "relationDraft.object.fieldComputed", $"Object field '{name}' is computed and cannot be assigned.", keyLocation);
                    continue;
                }
                // An object call always constructs a present object. Its optional parent never weakens required children.
                ValidateValue(arguments[index + 1], new(child.Type, cardinality: child.Cardinality,
                        presence: child.Presence, nullability: child.Nullability),
                    targetPath.Append(FieldPathSegment.ForField(name)), $"{location}/arguments/{index + 1}", item);
            }
            foreach (var child in children)
            {
                if (child.Role != FieldRole.Computed && child.Presence == FieldPresence.Required && !supplied.Contains(child.Name.Value))
                    Add(diagnostics, "relationDraft.object.fieldRequired",
                        $"Object construction for '{targetPath}' omits required child '{child.Name.Value}'.", location);
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

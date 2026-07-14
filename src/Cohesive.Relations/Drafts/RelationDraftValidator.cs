using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Drafts;

/// <summary>Validates portable relation-draft invariants that do not require shape resolution.</summary>
/// <remarks>
/// Any producer may supply stable slot identifiers explicitly. The built-in convention's
/// reserved slot prefix is validated against the target shape and path. Candidate identity is
/// always derived from its slot and canonical expression, so producer overlays cannot become stale
/// when semantic content changes.
/// </remarks>
public static class RelationDraftValidator
{
    /// <summary>Validates draft-local identities, references, and closed resolution state.</summary>
    /// <param name="draft">Portable relation draft to validate.</param>
    /// <returns>Structured draft-local diagnostics. Unresolved and ambiguous slots are valid persisted draft state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A candidate expression contains a value without a canonical relation/query JSON encoding while
    /// its deterministic semantic identity is verified.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A candidate expression cannot be written using the canonical relation/query wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A candidate expression contains a runtime type unsupported by canonical relation/query serialization.
    /// </exception>
    public static DocumentValidationResult Validate(RelationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        List<DocumentValidationDiagnostic> diagnostics = [];

        if (draft.Input is null)
        {
            Add(diagnostics, "relationDraft.input.missing", "A relation draft must contain a logical input.", "/draft/input");
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }
        
        if (draft.Projection is null)
        {
            Add(diagnostics, "relationDraft.projection.missing", "A relation draft must contain a terminal projection.", "/draft/projection");
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        ValidateIdentifier(
            draft.Id.Value,
            "relationDraft.id.missing",
            "A relation draft must have a non-empty lifecycle id.",
            "/draft/id",
            diagnostics);
        
        if (draft.RelationId is null || string.IsNullOrWhiteSpace(draft.RelationId.Value))
        {
            Add(diagnostics,
                "relationDraft.relationId.missing",
                "A relation draft must identify the canonical relation it will produce.",
                "/draft/relationId");
        }
        
        if (draft.Name is null || string.IsNullOrWhiteSpace(draft.Name.Value))
        {
            Add(diagnostics,
                "relationDraft.name.missing",
                "A relation draft must have a non-empty relation name.",
                "/draft/name");
        }
        
        ValidateIdentifier(
            draft.RootBinding.Value,
            "relationDraft.rootBinding.idMissing",
            "A relation draft must have a non-empty root binding.",
            "/draft/rootBinding",
            diagnostics
            );
        ValidateIdentifier(
            draft.Projection.Id.Value,
            "relationDraft.projection.idMissing",
            "A draft projection must have a non-empty node id.",
            "/draft/projection/id",
            diagnostics
            );
        ValidateIdentifier(
            draft.Projection.Input.Value,
            "relationDraft.projection.inputIdMissing",
            "A draft projection must reference a non-empty input node id.",
            "/draft/projection/input",
            diagnostics
            );
        ValidateIdentifier(
            draft.Projection.ResultBinding.Value,
            "relationDraft.projection.resultBindingIdMissing",
            "A draft projection must have a non-empty result binding.",
            "/draft/projection/resultBinding",
            diagnostics
            );
        
        if (string.IsNullOrWhiteSpace(draft.Projection.ResultShape.GraphId.Value) || string.IsNullOrWhiteSpace(draft.Projection.ResultShape.ShapeId.Value))
        {
            Add(diagnostics,
                "relationDraft.projection.resultShapeMissing",
                "A draft projection must identify a graph-qualified result shape.",
                "/draft/projection/resultShape");
        }

        var nodes = draft.Input.Nodes.IsDefault ? [] : draft.Input.Nodes;
        Dictionary<QueryNodeId, LogicalQueryNode> nodesById = [];
        foreach (var node in nodes)
        {
            if (node is null)
            {
                Add(diagnostics, "relationDraft.input.nodeMissing", "A logical input node cannot be null.", "/draft/input/nodes");
                continue;
            }
            
            if (!nodesById.TryAdd(node.Id, node))
            {
                Add(diagnostics,
                    "relationDraft.input.nodeDuplicateId",
                    $"Logical input node id '{node.Id.Value}' is declared more than once.",
                    $"/draft/input/nodes/{node.Id.Value}");
            }
        }

        if (!nodesById.ContainsKey(draft.Projection.Input))
        {
            Add(diagnostics,
                "relationDraft.projection.inputMissing",
                $"Draft projection references unknown input node '{draft.Projection.Input.Value}'.",
                "/draft/projection/input");
        }
        if (nodesById.ContainsKey(draft.Projection.Id))
        {
            Add(
                diagnostics,
                "relationDraft.projection.idCollision",
                $"Draft projection id '{draft.Projection.Id.Value}' collides with an input node id.",
                "/draft/projection/id");
        }
        if (!nodes.OfType<SourceQueryNode>().Any(source => source.Binding == draft.RootBinding))
        {
            Add(
                diagnostics,
                "relationDraft.rootBinding.missing",
                $"Root binding '{draft.RootBinding.Value}' is not declared by a source node.",
                "/draft/rootBinding");
        }
        if (!Enum.IsDefined(draft.OutputMode))
        {
            Add(
                diagnostics,
                "relationDraft.outputMode.invalid",
                $"Relation output mode '{draft.OutputMode}' is unsupported.",
                "/draft/outputMode");
        }

        ValidateInvariants(draft, diagnostics);
        ValidateAssignments(draft, diagnostics);
        ValidateLogicalSemantics(draft, nodes, diagnostics);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateInvariants(
        RelationDraft draft,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        var invariants = draft.Invariants.IsDefault ? [] : draft.Invariants;
        for (var index = 0; index < invariants.Length; index++)
        {
            var invariant = invariants[index];
            if (invariant is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.invariant.missing",
                    "A relation invariant cannot be null.",
                    $"/draft/invariants/{index}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(invariant.Name))
            {
                Add(
                    diagnostics,
                    "relationDraft.invariant.nameMissing",
                    "A relation invariant must have a non-empty name.",
                    $"/draft/invariants/{index}/name");
            }
            else if (!names.Add(invariant.Name))
            {
                Add(
                    diagnostics,
                    "relationDraft.invariant.duplicateName",
                    $"Relation invariant name '{invariant.Name}' is declared more than once.",
                    $"/draft/invariants/{index}/name");
            }
            if (invariant.Expression is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.invariant.expressionMissing",
                    $"Relation invariant '{invariant.Name}' must contain an expression.",
                    $"/draft/invariants/{index}/expression");
            }
        }
    }

    static void ValidateAssignments(
        RelationDraft draft,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        HashSet<QueryAssignmentId> slotIds = [];
        HashSet<FieldPath> targets = [];
        HashSet<RelationDraftCandidateId> candidateIds = [];
        var assignments = draft.Projection.Assignments.IsDefault ? [] : draft.Projection.Assignments;
        if (assignments.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                "relationDraft.projection.assignmentsEmpty",
                "A relation draft projection requires at least one assignment slot.",
                "/draft/projection/assignments");
        }

        foreach (var slot in assignments)
        {
            if (slot is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.slot.missing",
                    "A projection assignment slot cannot be null.",
                    "/draft/projection/assignments");
                continue;
            }

            var location = $"/draft/projection/assignments/{slot.Id.Value}";
            ValidateIdentifier(
                slot.Id.Value,
                "relationDraft.slot.idMissing",
                "A projection assignment slot must have a non-empty id.",
                $"{location}/id",
                diagnostics);
            if (!slotIds.Add(slot.Id))
            {
                Add(
                    diagnostics,
                    "relationDraft.slot.duplicateId",
                    $"Projection assignment slot id '{slot.Id.Value}' is declared more than once.",
                    location);
            }
            if (slot.Target.Segments.IsDefaultOrEmpty)
            {
                Add(
                    diagnostics,
                    "relationDraft.slot.targetMissing",
                    $"Projection assignment slot '{slot.Id.Value}' must declare a target path.",
                    $"{location}/target");
            }
            else
            {
                if (!targets.Add(slot.Target))
                {
                    Add(
                        diagnostics,
                        "relationDraft.slot.duplicateTarget",
                        $"Target path '{slot.Target}' is assigned by more than one slot.",
                        $"{location}/target");
                }

                if (RelationDraftIdentityConvention.IsConventionAssignmentSlotId(slot.Id)
                    && !string.IsNullOrWhiteSpace(draft.Projection.ResultShape.GraphId.Value)
                    && !string.IsNullOrWhiteSpace(draft.Projection.ResultShape.ShapeId.Value))
                {
                    var expectedSlotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(
                        draft.Projection.ResultShape,
                        slot.Target);
                    if (slot.Id != expectedSlotId)
                    {
                        Add(
                            diagnostics,
                            "relationDraft.slot.idMismatch",
                            $"Convention slot id '{slot.Id.Value}' does not match its target-derived identity '{expectedSlotId.Value}'.",
                            $"{location}/id");
                    }
                }

            }

            Dictionary<RelationDraftCandidateId, RelationDraftCandidate> candidates = [];
            foreach (var candidate in slot.Candidates.IsDefault ? [] : slot.Candidates)
            {
                if (candidate is null)
                {
                    Add(
                        diagnostics,
                        "relationDraft.candidate.missing",
                        $"Projection assignment slot '{slot.Id.Value}' contains a null candidate.",
                        $"{location}/candidates");
                    continue;
                }

                var candidateLocation = $"{location}/candidates/{candidate.Id.Value}";
                ValidateIdentifier(
                    candidate.Id.Value,
                    "relationDraft.candidate.idMissing",
                    "A relation draft candidate must have a non-empty id.",
                    $"{candidateLocation}/id",
                    diagnostics);
                if (!candidates.TryAdd(candidate.Id, candidate))
                {
                    Add(
                        diagnostics,
                        "relationDraft.candidate.duplicateId",
                        $"Candidate id '{candidate.Id.Value}' is declared more than once in slot '{slot.Id.Value}'.",
                        candidateLocation);
                }
                if (!candidateIds.Add(candidate.Id))
                {
                    Add(
                        diagnostics,
                        "relationDraft.candidate.idNotUnique",
                        $"Candidate id '{candidate.Id.Value}' must be unique across the relation draft.",
                        candidateLocation);
                }
                if (candidate.Value is null)
                {
                    Add(
                        diagnostics,
                        "relationDraft.candidate.valueMissing",
                        $"Candidate '{candidate.Id.Value}' must contain an expression.",
                        $"{candidateLocation}/value");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(slot.Id.Value))
                {
                    var expectedCandidateId = RelationDraftIdentityConvention.CreateCandidateId(
                        slot.Id,
                        candidate.Value);
                    if (candidate.Id != expectedCandidateId)
                    {
                        Add(
                            diagnostics,
                            "relationDraft.candidate.idMismatch",
                            $"Candidate id '{candidate.Id.Value}' does not match its slot-and-expression-derived semantic identity '{expectedCandidateId.Value}'.",
                            $"{candidateLocation}/id");
                    }
                }
            }

            ValidateResolution(slot, candidates, diagnostics, location);
        }
    }

    static void ValidateLogicalSemantics(
        RelationDraft draft,
        IReadOnlyCollection<LogicalQueryNode> inputNodes,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var nodes = inputNodes.Where(static node => node is not null).ToArray();
        var slots = draft.Projection.Assignments.IsDefault
            ? []
            : draft.Projection.Assignments;
        var candidates = slots
            .Where(static slot => slot is not null)
            .SelectMany(static slot => slot.Candidates.IsDefault ? [] : slot.Candidates)
            .Where(static candidate => candidate?.Value is not null)
            .ToArray();

        RelationQueryDefinition probe;
        try
        {
            var probeAssignments = candidates.Length == 0
                ?
                [
                    new ProjectionAssignment(
                        new QueryAssignmentId("relation-draft-validation-placeholder"),
                        FieldPath.FromField("placeholder"),
                        Expr.Null())
                ]
                : candidates
                    .Select((candidate, index) => new ProjectionAssignment(
                        new QueryAssignmentId($"relation-draft-candidate-{index}"),
                        FieldPath.FromField($"candidate{index}"),
                        candidate.Value))
                    .ToImmutableArray();
            var projection = new ProjectQueryNode(
                draft.Projection.Id,
                draft.Projection.Input,
                draft.Projection.ResultBinding,
                draft.Projection.ResultShape,
                probeAssignments);
            var body = new LogicalQueryDefinition([.. nodes, projection], draft.Input.Parameters);
            probe = new Cohesive.Relations.IR.RelationDefinition(
                draft.RelationId,
                draft.Name,
                body,
                draft.RootBinding,
                new RelationOutputDefinition(
                    draft.Projection.Id,
                    draft.Projection.ResultShape,
                    draft.OutputMode,
                    draft.OutputKey),
                draft.Invariants);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Add(
                diagnostics,
                "relationDraft.logical.invalid",
                $"The draft logical graph could not be validated: {exception.Message}",
                "/draft/input");
            return;
        }

        foreach (var diagnostic in RelationQueryDefinitionValidator.Validate(probe).Diagnostics)
            diagnostics.Add(diagnostic);
    }

    static void ValidateIdentifier(
        string? value,
        string code,
        string message,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(diagnostics, code, message, location);
    }

    static void ValidateResolution(
        RelationDraftAssignmentSlot slot,
        IReadOnlyDictionary<RelationDraftCandidateId, RelationDraftCandidate> candidates,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        switch (slot.Resolution)
        {
            case null:
                Add(
                    diagnostics,
                    "relationDraft.resolution.missing",
                    $"Projection assignment slot '{slot.Id.Value}' must declare a resolution.",
                    $"{location}/resolution");
                break;
            case SelectedRelationDraftAssignmentResolution selected
                when !candidates.ContainsKey(selected.CandidateId):
                Add(
                    diagnostics,
                    "relationDraft.resolution.selectedCandidateUnknown",
                    $"Selected candidate '{selected.CandidateId.Value}' is not declared by slot '{slot.Id.Value}'.",
                    $"{location}/resolution/candidateId");
                break;
            case UnresolvedRelationDraftAssignmentResolution unresolved:
            {
                if (unresolved.Reasons.IsDefaultOrEmpty)
                {
                    Add(
                        diagnostics,
                        "relationDraft.resolution.unresolvedReasonsEmpty",
                        $"Unresolved slot '{slot.Id.Value}' must record at least one structured reason.",
                        $"{location}/resolution/reasons");
                    break;
                }

                HashSet<RelationDraftUnresolvedReason> seen = [];
                foreach (var reason in unresolved.Reasons)
                {
                    if (!Enum.IsDefined(reason))
                    {
                        Add(
                            diagnostics,
                            "relationDraft.resolution.unresolvedReasonInvalid",
                            $"Unresolved slot '{slot.Id.Value}' declares unsupported reason '{reason}'.",
                            $"{location}/resolution/reasons");
                    }
                    else if (!seen.Add(reason))
                    {
                        Add(
                            diagnostics,
                            "relationDraft.resolution.unresolvedReasonDuplicate",
                            $"Unresolved slot '{slot.Id.Value}' declares reason '{reason}' more than once.",
                            $"{location}/resolution/reasons");
                    }
                }
                break;
            }
            case AmbiguousRelationDraftAssignmentResolution ambiguous:
            {
                if (ambiguous.CandidateIds.Length < 2)
                {
                    Add(
                        diagnostics,
                        "relationDraft.resolution.ambiguousCandidateCountInvalid",
                        $"Ambiguous slot '{slot.Id.Value}' must reference at least two candidates.",
                        $"{location}/resolution/candidateIds");
                }

                HashSet<RelationDraftCandidateId> seen = [];
                foreach (var candidateId in ambiguous.CandidateIds)
                {
                    if (!seen.Add(candidateId))
                    {
                        Add(
                            diagnostics,
                            "relationDraft.resolution.ambiguousCandidateDuplicate",
                            $"Ambiguous slot '{slot.Id.Value}' references candidate '{candidateId.Value}' more than once.",
                            $"{location}/resolution/candidateIds");
                    }
                    if (!candidates.ContainsKey(candidateId))
                    {
                        Add(
                            diagnostics,
                            "relationDraft.resolution.ambiguousCandidateUnknown",
                            $"Ambiguous slot '{slot.Id.Value}' references undeclared candidate '{candidateId.Value}'.",
                            $"{location}/resolution/candidateIds");
                    }
                }
                break;
            }
        }
    }

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, location));
}

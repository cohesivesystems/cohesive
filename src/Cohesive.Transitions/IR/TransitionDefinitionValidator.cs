using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Transitions.IR;

/// <summary>Stable diagnostic codes emitted by <see cref="TransitionDefinitionValidator"/>.</summary>
public static class TransitionDefinitionDiagnosticCodes
{
    /// <summary>A required Transition IR member is missing.</summary>
    public const string RequiredMemberMissing = "transitions.ir.requiredMemberMissing";

    /// <summary>A stable execution-node identity is default or empty.</summary>
    public const string NodeIdentityMissing = "transitions.ir.nodeIdentityMissing";

    /// <summary>A stable execution-node identity occurs more than once in one definition.</summary>
    public const string NodeIdentityDuplicate = "transitions.ir.nodeIdentityDuplicate";

    /// <summary>A node object is reachable through its own active descendant chain.</summary>
    public const string NodeCycle = "transitions.ir.nodeCycle";

    /// <summary>A Transition node is outside the closed v1 node union.</summary>
    public const string NodeUnsupported = "transitions.ir.nodeUnsupported";

    /// <summary>A required finite sequence has no steps.</summary>
    public const string SequenceEmpty = "transitions.ir.sequenceEmpty";

    /// <summary>A persisted Transition enum value is not recognized.</summary>
    public const string EnumUnsupported = "transitions.ir.enumUnsupported";

    /// <summary>A Choice node has no declared predicate cases.</summary>
    public const string ChoiceCasesEmpty = "transitions.ir.choiceCasesEmpty";

    /// <summary>A Match node has no declared exact-value cases.</summary>
    public const string MatchCasesEmpty = "transitions.ir.matchCasesEmpty";

    /// <summary>A branch completeness declaration and fallback shape disagree.</summary>
    public const string FallbackContractInvalid = "transitions.ir.fallbackContractInvalid";

    /// <summary>A lexical binding has no stable identity.</summary>
    public const string BindingIdentityMissing = "transitions.ir.bindingIdentityMissing";

    /// <summary>A Match pattern does not use the node's declared value contract.</summary>
    public const string MatchPatternContractMismatch = "transitions.ir.matchPatternContractMismatch";

    /// <summary>An aggregate-relative patch path is default or empty.</summary>
    public const string PatchPathInvalid = "transitions.ir.patchPathInvalid";

    /// <summary>A sparse patch is outside the closed v1 patch union.</summary>
    public const string PatchUnsupported = "transitions.ir.patchUnsupported";

    /// <summary>An exact emission-contract reference is missing or incomplete.</summary>
    public const string EmissionContractInvalid = "transitions.ir.emissionContractInvalid";
}

/// <summary>
/// Validates canonical Transition IR v1 structure and portable leaf semantics without executing it.
/// </summary>
/// <remarks>
/// This boundary intentionally performs structural validation only. Expression scope and result typing,
/// exhaustiveness proof, path completion, duplicate realized writes, and must/may access analysis belong to
/// the Transition compiler. Tree construction makes ordinary control-flow cycles unrepresentable; the validator
/// also detects reference cycles in deliberately malformed in-memory object graphs.
/// </remarks>
public static class TransitionDefinitionValidator
{
    /// <summary>Validates a canonical Transition definition.</summary>
    /// <param name="definition">Definition to validate.</param>
    /// <param name="graph">
    /// Optional shared shape graph used to resolve named types and graph-qualified shapes.
    /// </param>
    /// <returns>Every structural and portability diagnostic in deterministic document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(
        TransitionDefinition definition,
        ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var context = new ValidationContext(graph);
        context.ValidateDefinition(definition);
        return context.ToResult();
    }

    sealed class ValidationContext(ShapeGraph? graph)
    {
        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly Dictionary<ExecutionNodeId, string> nodeLocations = [];
        readonly HashSet<TransitionNode> activeNodes = new(ReferenceEqualityComparer.Instance);

        public DocumentValidationResult ToResult()
        {
            diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        public void ValidateDefinition(TransitionDefinition definition)
        {
            ValidateContract(definition.Input, "/input");
            ValidateContract(definition.Observation, "/observation");
            ValidateContract(definition.Outcome, "/outcome");

            for (var index = 0; index < definition.Preconditions.Length; index++)
            {
                var location = $"/preconditions/{index}";
                var precondition = definition.Preconditions[index];
                if (precondition is null)
                {
                    Missing(location, "A Transition precondition cannot be null.");
                    continue;
                }

                RegisterNodeId(precondition.Id, Child(location, "id"));
                ValidateExpression(precondition.Predicate, Child(location, "predicate"));
                ValidateExpression(precondition.Rejection, Child(location, "rejection"));
            }

            ValidateNode(definition.Body, "/body");

            for (var index = 0; index < definition.Invariants.Length; index++)
            {
                var location = $"/invariants/{index}";
                var invariant = definition.Invariants[index];
                if (invariant is null)
                {
                    Missing(location, "A Transition invariant cannot be null.");
                    continue;
                }

                RegisterNodeId(invariant.Id, Child(location, "id"));
                ValidateExpression(invariant.Predicate, Child(location, "predicate"));
            }
        }

        void ValidateNode(TransitionNode? node, string location)
        {
            if (node is null)
            {
                Missing(location, "A Transition node cannot be null.");
                return;
            }

            RegisterNodeId(node.Id, Child(location, "id"));
            if (!activeNodes.Add(node))
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.NodeCycle,
                    "A Transition node cannot contain itself through a descendant path.",
                    location);
                return;
            }

            try
            {
                switch (node)
                {
                    case SequenceTransitionNode sequence:
                        ValidateSequence(sequence, location);
                        break;
                    case LetTransitionNode let:
                        ValidateLet(let, location);
                        break;
                    case ChoiceTransitionNode choice:
                        ValidateChoice(choice, location);
                        break;
                    case MatchTransitionNode match:
                        ValidateMatch(match, location);
                        break;
                    case UpdateTransitionNode update:
                        ValidateUpdate(update, location);
                        break;
                    case EmitTransitionNode emit:
                        ValidateEmit(emit, location);
                        break;
                    case OutcomeTransitionNode outcome:
                        ValidateOutcome(outcome, location);
                        break;
                    default:
                        Error(
                            TransitionDefinitionDiagnosticCodes.NodeUnsupported,
                            $"Transition node '{node.GetType().FullName}' is outside the closed v1 node union.",
                            location);
                        break;
                }
            }
            finally
            {
                activeNodes.Remove(node);
            }
        }

        void ValidateSequence(SequenceTransitionNode sequence, string location)
        {
            if (sequence.Steps.IsDefaultOrEmpty)
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.SequenceEmpty,
                    "A Transition sequence requires at least one step.",
                    Child(location, "steps"));
                return;
            }

            for (var index = 0; index < sequence.Steps.Length; index++)
            {
                ValidateNode(sequence.Steps[index], $"{location}/steps/{index}");
            }
        }

        void ValidateLet(LetTransitionNode let, string location)
        {
            if (string.IsNullOrWhiteSpace(let.Binding.Value))
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.BindingIdentityMissing,
                    "A Let node requires a non-default value-binding identity.",
                    Child(location, "binding"));
            }

            ValidateContract(let.Contract, Child(location, "contract"));
            ValidateExpression(let.Value, Child(location, "value"));
        }

        void ValidateChoice(ChoiceTransitionNode choice, string location)
        {
            if (!IsSupportedEnum(choice.Selection))
            {
                UnsupportedEnum(choice.Selection, Child(location, "selection"));
            }
            if (!IsSupportedEnum(choice.Completeness))
            {
                UnsupportedEnum(choice.Completeness, Child(location, "completeness"));
            }

            ValidateFallbackContract(choice.Completeness, choice.Fallback, location);
            if (choice.Cases.IsDefaultOrEmpty)
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.ChoiceCasesEmpty,
                    "A Choice node requires at least one predicate case.",
                    Child(location, "cases"));
            }
            else
            {
                for (var index = 0; index < choice.Cases.Length; index++)
                {
                    var caseLocation = $"{location}/cases/{index}";
                    var choiceCase = choice.Cases[index];
                    if (choiceCase is null)
                    {
                        Missing(caseLocation, "A Choice case cannot be null.");
                        continue;
                    }

                    RegisterNodeId(choiceCase.Id, Child(caseLocation, "id"));
                    ValidateExpression(choiceCase.Predicate, Child(caseLocation, "predicate"));
                    ValidateNode(choiceCase.Body, Child(caseLocation, "body"));
                }
            }

            ValidateFallback(choice.Fallback, Child(location, "fallback"));
        }

        void ValidateMatch(MatchTransitionNode match, string location)
        {
            if (!IsSupportedEnum(match.Selection))
            {
                UnsupportedEnum(match.Selection, Child(location, "selection"));
            }
            if (!IsSupportedEnum(match.Completeness))
            {
                UnsupportedEnum(match.Completeness, Child(location, "completeness"));
            }

            ValidateExpression(match.Value, Child(location, "value"));
            ValidateContract(match.Contract, Child(location, "contract"));
            ValidateFallbackContract(match.Completeness, match.Fallback, location);
            if (match.Cases.IsDefaultOrEmpty)
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.MatchCasesEmpty,
                    "A Match node requires at least one exact-value case.",
                    Child(location, "cases"));
            }
            else
            {
                for (var index = 0; index < match.Cases.Length; index++)
                {
                    var caseLocation = $"{location}/cases/{index}";
                    var matchCase = match.Cases[index];
                    if (matchCase is null)
                    {
                        Missing(caseLocation, "A Match case cannot be null.");
                        continue;
                    }

                    RegisterNodeId(matchCase.Id, Child(caseLocation, "id"));
                    ValidatePortableValue(matchCase.Pattern, Child(caseLocation, "pattern"));
                    if (match.Contract is not null
                        && matchCase.Pattern is not null
                        && matchCase.Pattern.Contract != match.Contract)
                    {
                        Error(
                            TransitionDefinitionDiagnosticCodes.MatchPatternContractMismatch,
                            "A Match case pattern must use the Match node's exact value contract.",
                            Child(caseLocation, "pattern/contract"));
                    }
                    ValidateNode(matchCase.Body, Child(caseLocation, "body"));
                }
            }

            ValidateFallback(match.Fallback, Child(location, "fallback"));
        }

        void ValidateFallbackContract(
            TransitionBranchCompleteness completeness,
            TransitionFallback? fallback,
            string location)
        {
            if (!IsSupportedEnum(completeness))
            {
                return;
            }

            if (completeness == TransitionBranchCompleteness.Fallback && fallback is null)
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.FallbackContractInvalid,
                    "Fallback completeness requires an explicit fallback branch.",
                    Child(location, "fallback"));
            }
            else if (completeness == TransitionBranchCompleteness.Exhaustive && fallback is not null)
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.FallbackContractInvalid,
                    "Exhaustive completeness cannot also declare a fallback branch.",
                    Child(location, "fallback"));
            }
        }

        void ValidateFallback(TransitionFallback? fallback, string location)
        {
            if (fallback is null)
            {
                return;
            }

            RegisterNodeId(fallback.Id, Child(location, "id"));
            ValidateNode(fallback.Body, Child(location, "body"));
        }

        void ValidateUpdate(UpdateTransitionNode update, string location)
        {
            ValidatePath(update.Path, Child(location, "path"));
            var operationLocation = Child(location, "operation");
            if (update.Operation is null)
            {
                Missing(operationLocation, "An Update node requires a sparse patch operation.");
                return;
            }

            switch (update.Operation)
            {
                case SetTransitionPatch set:
                    ValidateExpression(set.Value, Child(operationLocation, "value"));
                    break;
                case RemoveTransitionPatch:
                    break;
                case IncrementTransitionPatch increment:
                    ValidateExpression(increment.Amount, Child(operationLocation, "amount"));
                    break;
                case AddToSetTransitionPatch addToSet:
                    ValidateExpression(addToSet.Value, Child(operationLocation, "value"));
                    break;
                case AppendTransitionPatch append:
                    ValidateExpression(append.Value, Child(operationLocation, "value"));
                    break;
                case UpsertOwnedChildTransitionPatch upsert:
                    ValidatePath(upsert.IdentityPath, Child(operationLocation, "identityPath"));
                    ValidateExpression(upsert.Identity, Child(operationLocation, "identity"));
                    ValidateExpression(upsert.Value, Child(operationLocation, "value"));
                    break;
                case RemoveOwnedChildTransitionPatch removeChild:
                    ValidatePath(removeChild.IdentityPath, Child(operationLocation, "identityPath"));
                    ValidateExpression(removeChild.Identity, Child(operationLocation, "identity"));
                    break;
                default:
                    Error(
                        TransitionDefinitionDiagnosticCodes.PatchUnsupported,
                        $"Sparse patch '{update.Operation.GetType().FullName}' is outside the closed v1 patch union.",
                        operationLocation);
                    break;
            }
        }

        void ValidateEmit(EmitTransitionNode emit, string location)
        {
            var contractLocation = Child(location, "contract");
            var contract = emit.Contract;
            if (contract is null
                || string.IsNullOrWhiteSpace(contract.DefinitionId.Value)
                || string.IsNullOrWhiteSpace(contract.RevisionId.Value)
                || contract.Fingerprint is null
                || string.IsNullOrWhiteSpace(contract.Fingerprint.Algorithm)
                || string.IsNullOrWhiteSpace(contract.Fingerprint.Canonicalization)
                || string.IsNullOrWhiteSpace(contract.Fingerprint.Value))
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.EmissionContractInvalid,
                    "An Emit node requires a complete exact definition, revision, and fingerprint reference.",
                    contractLocation);
            }

            ValidateExpression(emit.Payload, Child(location, "payload"));
        }

        void ValidateOutcome(OutcomeTransitionNode outcome, string location)
        {
            if (!IsSupportedEnum(outcome.Disposition))
            {
                UnsupportedEnum(outcome.Disposition, Child(location, "disposition"));
            }

            ValidateExpression(outcome.Value, Child(location, "value"));
        }

        void ValidateContract(ValueContract? contract, string location)
        {
            if (contract is null)
            {
                Missing(location, "A portable value contract is required.");
                return;
            }

            AddPortableDiagnostics(PortableExecutionValidator.Validate(contract, graph), location);
        }

        void ValidateExpression(Expr? expression, string location)
        {
            if (expression is null)
            {
                Missing(location, "A portable expression is required.");
                return;
            }

            AddPortableDiagnostics(PortableExecutionValidator.Validate(expression, graph), location);
        }

        void ValidatePortableValue(PortableValue? value, string location)
        {
            if (value is null)
            {
                Missing(location, "A portable Match pattern is required.");
                return;
            }

            AddPortableDiagnostics(PortableExecutionValidator.Validate(value, graph), location);
        }

        void AddPortableDiagnostics(DocumentValidationResult validation, string prefix)
        {
            foreach (var diagnostic in validation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Location = Prefix(prefix, diagnostic.Location)
                });
            }
        }

        void RegisterNodeId(ExecutionNodeId id, string location)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.NodeIdentityMissing,
                    "A canonical Transition construct requires a non-default execution-node identity.",
                    location);
                return;
            }

            if (nodeLocations.TryGetValue(id, out var firstLocation))
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.NodeIdentityDuplicate,
                    $"Execution-node identity '{id.Value}' is already declared at '{firstLocation}'.",
                    location);
                return;
            }

            nodeLocations.Add(id, location);
        }

        void ValidatePath(FieldPath path, string location)
        {
            if (path.Segments.IsDefaultOrEmpty)
            {
                Error(
                    TransitionDefinitionDiagnosticCodes.PatchPathInvalid,
                    "A sparse patch requires a non-default aggregate-relative semantic field path.",
                    location);
                return;
            }

            for (var index = 0; index < path.Segments.Length; index++)
            {
                var segment = path.Segments[index];
                if (!Enum.IsDefined(segment.Kind)
                    || segment.Kind == SegmentKind.Field && string.IsNullOrWhiteSpace(segment.Segment)
                    || segment.Kind == SegmentKind.Element && segment.Segment is not null)
                {
                    Error(
                        TransitionDefinitionDiagnosticCodes.PatchPathInvalid,
                        "A sparse patch path contains an invalid semantic path segment.",
                        $"{location}/segments/{index}");
                }
            }
        }

        void UnsupportedEnum<TEnum>(TEnum value, string location)
            where TEnum : struct, Enum =>
            Error(
                TransitionDefinitionDiagnosticCodes.EnumUnsupported,
                $"Transition enum value '{Convert.ToInt64(value)}' is not recognized for '{typeof(TEnum).Name}'.",
                location);

        static bool IsSupportedEnum<TEnum>(TEnum value)
            where TEnum : struct, Enum =>
            Enum.IsDefined(value) && Convert.ToInt64(value) != 0;

        void Missing(string location, string message) =>
            Error(TransitionDefinitionDiagnosticCodes.RequiredMemberMissing, message, location);

        void Error(string code, string message, string location) =>
            diagnostics.Add(new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: message,
                Location: location));

        static string Child(string parent, string segment) => $"{parent}/{segment}";

        static string Prefix(string prefix, string? location)
        {
            if (string.IsNullOrEmpty(location) || location == "$")
            {
                return prefix;
            }

            return location[0] == '/'
                ? prefix + location
                : $"{prefix}/{location}";
        }
    }
}

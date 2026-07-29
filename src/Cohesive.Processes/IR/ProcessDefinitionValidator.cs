using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;

namespace Cohesive.Processes.IR;

/// <summary>Stable diagnostic codes emitted by <see cref="ProcessDefinitionValidator"/>.</summary>
public static class ProcessDefinitionDiagnosticCodes
{
    /// <summary>A required Process IR member is missing.</summary>
    public const string RequiredMemberMissing = "processes.ir.requiredMemberMissing";

    /// <summary>A stable node or nested construct identity is missing.</summary>
    public const string NodeIdentityMissing = "processes.ir.nodeIdentityMissing";

    /// <summary>A stable node or nested construct identity is duplicated.</summary>
    public const string NodeIdentityDuplicate = "processes.ir.nodeIdentityDuplicate";

    /// <summary>A stable control-flow edge identity is missing.</summary>
    public const string EdgeIdentityMissing = "processes.ir.edgeIdentityMissing";

    /// <summary>A stable control-flow edge identity is duplicated.</summary>
    public const string EdgeIdentityDuplicate = "processes.ir.edgeIdentityDuplicate";

    /// <summary>The declared Process entry does not identify a node.</summary>
    public const string EntryUnresolved = "processes.ir.entryUnresolved";

    /// <summary>A control-flow edge targets no declared Process node.</summary>
    public const string EdgeTargetUnresolved = "processes.ir.edgeTargetUnresolved";

    /// <summary>A Process node or AwaitMatch clause lies outside its closed v1 union.</summary>
    public const string NodeUnsupported = "processes.ir.nodeUnsupported";

    /// <summary>A required Process enum value is unspecified or unsupported.</summary>
    public const string EnumUnsupported = "processes.ir.enumUnsupported";

    /// <summary>A Choice or Match completeness declaration disagrees with its fallback.</summary>
    public const string FallbackContractInvalid = "processes.ir.fallbackContractInvalid";

    /// <summary>A Choice or Match declares no cases.</summary>
    public const string BranchCasesEmpty = "processes.ir.branchCasesEmpty";

    /// <summary>A declared exhaustive Choice or Match is statically known to leave an input uncovered.</summary>
    public const string ExhaustivenessDisproven = "processes.ir.exhaustivenessDisproven";

    /// <summary>The restricted Process proof model cannot prove a declared Choice or Match exhaustive.</summary>
    public const string ExhaustivenessUnknown = "processes.ir.exhaustivenessUnknown";

    /// <summary>A Match pattern does not agree with the Match value contract.</summary>
    public const string MatchPatternContractMismatch = "processes.ir.matchPatternContractMismatch";

    /// <summary>A Match pattern is not an observable exact state.</summary>
    public const string MatchPatternStateInvalid = "processes.ir.matchPatternStateInvalid";

    /// <summary>A continuation output binding has no stable identity.</summary>
    public const string BindingIdentityMissing = "processes.ir.bindingIdentityMissing";

    /// <summary>A binding has more than one producer in one Process definition.</summary>
    public const string BindingProducerDuplicate = "processes.ir.bindingProducerDuplicate";

    /// <summary>A continuation output contract differs from the referenced result contract.</summary>
    public const string OutputContractMismatch = "processes.ir.outputContractMismatch";

    /// <summary>A construct that produces no value declares a continuation output.</summary>
    public const string OutputNotAllowed = "processes.ir.outputNotAllowed";

    /// <summary>An exact Transition or Relation/Query reference is missing or malformed.</summary>
    public const string DefinitionReferenceInvalid = "processes.ir.definitionReferenceInvalid";

    /// <summary>An exact Transition or Relation/Query reference is absent from supplied linker evidence.</summary>
    public const string DefinitionReferenceUnresolved = "processes.ir.definitionReferenceUnresolved";

    /// <summary>A linked definition belongs to the wrong semantic family.</summary>
    public const string DefinitionReferenceKindMismatch = "processes.ir.definitionReferenceKindMismatch";

    /// <summary>An exact interaction reference is missing or absent from the supplied catalog.</summary>
    public const string InteractionReferenceUnresolved = "processes.ir.interactionReferenceUnresolved";

    /// <summary>An interaction reference's typed family differs from the exact catalog definition.</summary>
    public const string InteractionReferenceKindMismatch = "processes.ir.interactionReferenceKindMismatch";

    /// <summary>A Request declares no terminal-outcome continuations.</summary>
    public const string RequestOutcomesEmpty = "processes.ir.requestOutcomesEmpty";

    /// <summary>A Request terminal outcome identity is missing or duplicated.</summary>
    public const string RequestOutcomeInvalid = "processes.ir.requestOutcomeInvalid";

    /// <summary>A Request branch selects an outcome absent from the exact Request contract.</summary>
    public const string RequestOutcomeUnknown = "processes.ir.requestOutcomeUnknown";

    /// <summary>A Request omits a terminal outcome required by the exact Request contract.</summary>
    public const string RequestOutcomeMissing = "processes.ir.requestOutcomeMissing";

    /// <summary>A Fork declares no branch tokens.</summary>
    public const string ForkBranchesEmpty = "processes.ir.forkBranchesEmpty";

    /// <summary>A Fork does not identify a declared Join.</summary>
    public const string ForkJoinUnresolved = "processes.ir.forkJoinUnresolved";

    /// <summary>A Join does not identify a declared Fork.</summary>
    public const string JoinForkUnresolved = "processes.ir.joinForkUnresolved";

    /// <summary>A Fork and Join do not reference one another.</summary>
    public const string ForkJoinNotReciprocal = "processes.ir.forkJoinNotReciprocal";

    /// <summary>
    /// A Fork branch crosses a foreign Join, contains a free-activation cycle, has no structural path to its reciprocal
    /// Join, or has another finite exit that does not converge there.
    /// </summary>
    public const string ForkBranchDoesNotConverge = "processes.ir.forkBranchDoesNotConverge";

    /// <summary>A reachable Join ingress is not owned by a branch of its reciprocal Fork.</summary>
    public const string JoinIngressNotOwned = "processes.ir.joinIngressNotOwned";

    /// <summary>A required-count Join threshold is incompatible with its reciprocal Fork.</summary>
    public const string JoinRequiredCountInvalid = "processes.ir.joinRequiredCountInvalid";

    /// <summary>An AwaitMatch declares no eligible clauses.</summary>
    public const string AwaitClausesEmpty = "processes.ir.awaitClausesEmpty";

    /// <summary>An AwaitMatch clause identity is missing or duplicated.</summary>
    public const string AwaitClauseIdentityDuplicate = "processes.ir.awaitClauseIdentityDuplicate";

    /// <summary>An AwaitMatch retention horizon is not positive.</summary>
    public const string AwaitRetentionInvalid = "processes.ir.awaitRetentionInvalid";

    /// <summary>An AwaitMatch received-value contract differs from its exact interaction contract.</summary>
    public const string AwaitInputContractMismatch = "processes.ir.awaitInputContractMismatch";

    /// <summary>An AwaitMatch Request clause omits an obligation binding or a non-Request clause declares one.</summary>
    public const string AwaitRequestObligationInvalid = "processes.ir.awaitRequestObligationInvalid";

    /// <summary>An inbound Request-obligation binding has no stable identity.</summary>
    public const string RequestObligationIdentityMissing = "processes.ir.requestObligationIdentityMissing";

    /// <summary>An inbound Request-obligation binding has more than one producer.</summary>
    public const string RequestObligationProducerDuplicate = "processes.ir.requestObligationProducerDuplicate";

    /// <summary>A Reply does not identify a known, definitely visible inbound Request obligation.</summary>
    public const string ReplyRequestObligationUnresolved = "processes.ir.replyRequestObligationUnresolved";

    /// <summary>A Reply contract does not discharge the exact Request contract retained by its target obligation.</summary>
    public const string ReplyRequestContractMismatch = "processes.ir.replyRequestContractMismatch";

    /// <summary>A nonterminal, non-durable Process node has no continuation.</summary>
    public const string NonTerminalDeadEnd = "processes.ir.nonTerminalDeadEnd";

    /// <summary>A declared Process node is not reachable from the entry through the complete graph.</summary>
    public const string NodeUnreachable = "processes.ir.nodeUnreachable";

    /// <summary>A control-flow cycle can execute without crossing a durable boundary.</summary>
    public const string FreeActivationCycle = "processes.ir.freeActivationCycle";
}

/// <summary>Validates canonical finite Process IR without executing or physically planning it.</summary>
/// <remarks>
/// Validation is deterministic and fail-closed. It checks the fixed portable expression closure, exact semantic
/// links, conservative exhaustiveness proof, definite value and Request-obligation flow, graph integrity,
/// Fork-token ownership and Join convergence, AwaitMatch policy, and the requirement that every activation
/// terminate or reach Request, AwaitMatch, Timer, or an explicit durable cut. Physical capabilities, checkpoint
/// layout, scheduling, effect dispatch, and storage realization belong to later interpretations.
/// </remarks>
public static class ProcessDefinitionValidator
{
    /// <summary>Validates a canonical Process definition without optional external linking evidence.</summary>
    /// <param name="definition">Definition to validate.</param>
    /// <returns>Every structural, portability, type-flow, and finite-activation diagnostic in deterministic order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ValidationContext(definition, context: null).Validate();
    }

    /// <summary>Validates a canonical Process definition using exact definition and interaction linking evidence.</summary>
    /// <param name="definition">Definition to validate.</param>
    /// <param name="context">External exact-reference and shape evidence.</param>
    /// <returns>Every structural, linking, portability, type-flow, and finite-activation diagnostic.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        ProcessDefinition definition,
        ProcessDefinitionValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        return new ValidationContext(definition, context).Validate();
    }

    sealed class ValidationContext(
        ProcessDefinition definition,
        ProcessDefinitionValidationContext? context)
    {
        static readonly ValueContract InstantContract = new(new ScalarTypeRef(ScalarTypeKind.Instant));

        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly Dictionary<ExecutionNodeId, NodeInfo> nodes = [];
        readonly Dictionary<ExecutionNodeId, string> constructLocations = [];
        readonly Dictionary<ProcessEdgeId, string> edgeLocations = [];
        readonly List<EdgeInfo> edges = [];
        readonly Dictionary<ValueBindingId, BindingInfo> bindings = [];
        readonly List<ExpressionInfo> expressions = [];
        readonly Dictionary<RequestObligationBindingId, RequestObligationInfo> requestObligations = [];
        readonly List<ReplyRequestInfo> replyRequests = [];
        readonly Dictionary<ExecutionNodeId, ForkJoinInfo> forkJoinsByJoin = [];

        public DocumentValidationResult Validate()
        {
            ValidateDefinition();
            ValidateEdgeTargets();
            ValidateGraph();
            ValidateBindingFlowAndExpressions();
            ValidateRequestObligationFlow();
            diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        void ValidateDefinition()
        {
            ValidateContract(definition.Input, "/input");
            ValidateContract(definition.Result, "/result");
            ValidateEnum(definition.RecoveryPolicy, "/recoveryPolicy");

            if (definition.Nodes.IsDefaultOrEmpty)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.RequiredMemberMissing,
                    "A Process definition requires at least one graph node.",
                    "/nodes");
            }

            for (var index = 0; index < definition.Nodes.Length; index++)
            {
                var node = definition.Nodes[index];
                var location = $"/nodes/{index.ToString(CultureInfo.InvariantCulture)}";
                if (node is null)
                {
                    Missing(location, "A Process node cannot be null.");
                    continue;
                }

                var idLocation = Child(location, "id");
                if (!RegisterConstructId(node.Id, idLocation))
                    continue;
                nodes.Add(node.Id, new(node, location));
            }

            if (string.IsNullOrWhiteSpace(definition.Entry.Value)
                || !nodes.ContainsKey(definition.Entry))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.EntryUnresolved,
                    "The Process entry must identify one declared node.",
                    "/entry",
                    subject: definition.Entry.Value);
            }

            if (definition.Input is not null)
            {
                bindings.Add(
                    ProcessBindingIds.Input,
                    new(definition.Input, "/input", ProducerNode: null));
            }

            foreach (var node in nodes.Values.OrderBy(static info => info.Location, StringComparer.Ordinal))
                ValidateNode(node);
        }

        void ValidateNode(NodeInfo info)
        {
            var node = info.Node;
            var location = info.Location;
            switch (node)
            {
                case InvokeTransitionProcessNode invocation:
                    ValidateInvocation(invocation, location);
                    break;
                case EvaluateRelationProcessNode evaluation:
                    ValidateEvaluation(evaluation, location);
                    break;
                case RequestProcessNode request:
                    ValidateRequest(request, location);
                    break;
                case EmitEventProcessNode emission:
                    ValidateEmitEvent(emission, location);
                    break;
                case SendSignalProcessNode signal:
                    ValidateSendSignal(signal, location);
                    break;
                case ChoiceProcessNode choice:
                    ValidateChoice(choice, location);
                    break;
                case MatchProcessNode match:
                    ValidateMatch(match, location);
                    break;
                case ForkProcessNode fork:
                    ValidateFork(fork, location);
                    break;
                case JoinProcessNode join:
                    ValidateJoin(join, location);
                    break;
                case AwaitMatchProcessNode awaitMatch:
                    ValidateAwaitMatch(awaitMatch, location);
                    break;
                case TimerProcessNode timer:
                    AddExpression(timer.Id, timer.DueAt, Child(location, "dueAt"), InstantContract);
                    RegisterEdge(timer.Next, Child(location, "next"), timer.Id);
                    break;
                case ReplyProcessNode reply:
                    ValidateReply(reply, location);
                    break;
                case DurableCutProcessNode durableCut:
                    RegisterEdge(durableCut.Resume, Child(location, "resume"), durableCut.Id);
                    break;
                case ReturnProcessNode terminal:
                    AddExpression(terminal.Id, terminal.Result, Child(location, "result"), definition.Result);
                    break;
                case FailProcessNode failure:
                    AddExpression(failure.Id, failure.Result, Child(location, "result"), definition.Result);
                    break;
                default:
                    Error(
                        ProcessDefinitionDiagnosticCodes.NodeUnsupported,
                        $"Process node '{node.GetType().FullName}' is outside the closed v1 node union.",
                        location,
                        subject: node.Id.Value);
                    break;
            }
        }

        void ValidateInvocation(InvokeTransitionProcessNode invocation, string location)
        {
            var link = ResolveDefinition(
                invocation.Transition,
                ProcessDefinitionLinkKind.Transition,
                Child(location, "transition"));
            AddExpression(invocation.Id, invocation.Subject, Child(location, "subject"));
            AddExpression(invocation.Id, invocation.Input, Child(location, "input"), link?.Input);
            RegisterContinuation(
                invocation.Continuation,
                Child(location, "continuation"),
                invocation.Id,
                link?.Result);
        }

        void ValidateEvaluation(EvaluateRelationProcessNode evaluation, string location)
        {
            var link = ResolveDefinition(
                evaluation.Relation,
                ProcessDefinitionLinkKind.RelationQuery,
                Child(location, "relation"));
            AddExpression(evaluation.Id, evaluation.Input, Child(location, "input"), link?.Input);
            RegisterContinuation(
                evaluation.Continuation,
                Child(location, "continuation"),
                evaluation.Id,
                link?.Result);
        }

        void ValidateRequest(RequestProcessNode request, string location)
        {
            var contract = ResolveInteraction<RequestContractDefinition>(
                request.Contract,
                Child(location, "contract"));
            AddExpression(request.Id, request.Payload, Child(location, "payload"), contract?.Payload.Contract);

            if (request.Outcomes.IsDefaultOrEmpty)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.RequestOutcomesEmpty,
                    "A Request node requires a continuation for every terminal outcome.",
                    Child(location, "outcomes"),
                    subject: request.Id.Value);
                return;
            }

            Dictionary<RequestTerminalOutcomeId, string> outcomes = [];
            HashSet<RequestTerminalOutcomeId> observed = [];
            for (var index = 0; index < request.Outcomes.Length; index++)
            {
                var branch = request.Outcomes[index];
                var branchLocation = $"{location}/outcomes/{index.ToString(CultureInfo.InvariantCulture)}";
                if (branch is null)
                {
                    Missing(branchLocation, "A Request outcome branch cannot be null.");
                    continue;
                }

                RegisterConstructId(branch.Id, Child(branchLocation, "id"));
                if (string.IsNullOrWhiteSpace(branch.Outcome.Value))
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.RequestOutcomeInvalid,
                        "A Request branch requires a stable terminal outcome identity.",
                        Child(branchLocation, "outcome"),
                        subject: branch.Id.Value);
                }
                else if (!outcomes.TryAdd(branch.Outcome, Child(branchLocation, "outcome")))
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.RequestOutcomeInvalid,
                        $"Request outcome '{branch.Outcome.Value}' is declared more than once.",
                        Child(branchLocation, "outcome"),
                        subject: branch.Outcome.Value,
                        relatedLocations: [outcomes[branch.Outcome]]);
                }

                var expected = contract?.Response.Find(branch.Outcome)?.Schema.Contract;
                if (contract is not null && expected is null)
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.RequestOutcomeUnknown,
                        $"Outcome '{branch.Outcome.Value}' is absent from the exact Request contract.",
                        Child(branchLocation, "outcome"),
                        subject: branch.Outcome.Value);
                }
                else if (expected is not null)
                {
                    observed.Add(branch.Outcome);
                }

                RegisterContinuation(
                    branch.Continuation,
                    Child(branchLocation, "continuation"),
                    request.Id,
                    expected);
            }

            if (contract is null)
                return;

            foreach (var required in contract.Response.TerminalOutcomes)
            {
                if (observed.Contains(required.Id))
                    continue;
                Error(
                    ProcessDefinitionDiagnosticCodes.RequestOutcomeMissing,
                    $"The exact Request contract requires terminal outcome '{required.Id.Value}'.",
                    Child(location, "outcomes"),
                    subject: request.Id.Value,
                    expected: required.Id.Value);
            }
        }

        void ValidateEmitEvent(EmitEventProcessNode emission, string location)
        {
            var contract = ResolveInteraction<DomainEventContractDefinition>(
                emission.Contract,
                Child(location, "contract"));
            AddExpression(emission.Id, emission.Payload, Child(location, "payload"), contract?.Payload.Contract);
            RegisterEdge(emission.Next, Child(location, "next"), emission.Id);
        }

        void ValidateSendSignal(SendSignalProcessNode signal, string location)
        {
            var contract = ResolveInteraction<SignalContractDefinition>(
                signal.Contract,
                Child(location, "contract"));
            AddExpression(signal.Id, signal.Target, Child(location, "target"));
            AddExpression(signal.Id, signal.Payload, Child(location, "payload"), contract?.Payload.Contract);
            RegisterEdge(signal.Next, Child(location, "next"), signal.Id);
        }

        void ValidateChoice(ChoiceProcessNode choice, string location)
        {
            ValidateEnum(choice.Selection, Child(location, "selection"));
            ValidateEnum(choice.Completeness, Child(location, "completeness"));
            ValidateFallback(choice.Completeness, choice.Fallback, location);
            if (choice.Cases.IsDefaultOrEmpty)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.BranchCasesEmpty,
                    "A Process Choice requires at least one ordered predicate case.",
                    Child(location, "cases"),
                    subject: choice.Id.Value);
            }

            for (var index = 0; index < choice.Cases.Length; index++)
            {
                var choiceCase = choice.Cases[index];
                var caseLocation = $"{location}/cases/{index.ToString(CultureInfo.InvariantCulture)}";
                if (choiceCase is null)
                {
                    Missing(caseLocation, "A Process Choice case cannot be null.");
                    continue;
                }
                RegisterConstructId(choiceCase.Id, Child(caseLocation, "id"));
                AddExpression(choice.Id, choiceCase.Predicate, Child(caseLocation, "predicate"), expectedBoolean: true);
                RegisterEdge(choiceCase.Next, Child(caseLocation, "next"), choice.Id);
            }

            if (choice.Fallback is { } fallback)
            {
                RegisterConstructId(fallback.Id, Child(location, "fallback/id"));
                RegisterEdge(fallback.Next, Child(location, "fallback/next"), choice.Id);
            }

            ValidateChoiceExhaustiveness(choice, location);
        }

        void ValidateMatch(MatchProcessNode match, string location)
        {
            ValidateEnum(match.Selection, Child(location, "selection"));
            ValidateEnum(match.Completeness, Child(location, "completeness"));
            ValidateContract(match.Contract, Child(location, "contract"));
            AddExpression(match.Id, match.Value, Child(location, "value"), match.Contract);
            ValidateFallback(match.Completeness, match.Fallback, location);
            if (match.Cases.IsDefaultOrEmpty)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.BranchCasesEmpty,
                    "A Process Match requires at least one ordered exact-value case.",
                    Child(location, "cases"),
                    subject: match.Id.Value);
            }

            for (var index = 0; index < match.Cases.Length; index++)
            {
                var matchCase = match.Cases[index];
                var caseLocation = $"{location}/cases/{index.ToString(CultureInfo.InvariantCulture)}";
                if (matchCase is null)
                {
                    Missing(caseLocation, "A Process Match case cannot be null.");
                    continue;
                }
                RegisterConstructId(matchCase.Id, Child(caseLocation, "id"));
                if (matchCase.Pattern is null)
                {
                    Missing(Child(caseLocation, "pattern"), "A Process Match pattern cannot be null.");
                }
                else
                {
                    AppendValidation(
                        PortableExecutionValidator.Validate(matchCase.Pattern, context?.ShapeGraph),
                        Child(caseLocation, "pattern"));
                    if (matchCase.Pattern.Contract != match.Contract)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.MatchPatternContractMismatch,
                            "A Match pattern must use the Match node's exact value contract.",
                            Child(caseLocation, "pattern/contract"),
                            subject: matchCase.Id.Value);
                    }
                    if (matchCase.Pattern.State is PortableValueState.Missing
                        or PortableValueState.Unknown
                        or PortableValueState.Failed)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.MatchPatternStateInvalid,
                            "A Match pattern must be an exact observable value state.",
                            Child(caseLocation, "pattern/state"),
                            subject: matchCase.Id.Value);
                    }
                }
                RegisterEdge(matchCase.Next, Child(caseLocation, "next"), match.Id);
            }

            if (match.Fallback is { } fallback)
            {
                RegisterConstructId(fallback.Id, Child(location, "fallback/id"));
                RegisterEdge(fallback.Next, Child(location, "fallback/next"), match.Id);
            }

            ValidateMatchExhaustiveness(match, location);
        }

        void ValidateChoiceExhaustiveness(ChoiceProcessNode choice, string location)
        {
            if (choice.Completeness != BranchCompleteness.Exhaustive || choice.Cases.IsDefaultOrEmpty)
                return;

            var hasUnknown = false;
            foreach (var choiceCase in choice.Cases)
            {
                if (choiceCase is null)
                    continue;
                if (!TryGetBooleanConstant(choiceCase.Predicate, out var predicate))
                {
                    hasUnknown = true;
                    continue;
                }
                if (predicate)
                    return;
            }

            Error(
                hasUnknown
                    ? ProcessDefinitionDiagnosticCodes.ExhaustivenessUnknown
                    : ProcessDefinitionDiagnosticCodes.ExhaustivenessDisproven,
                hasUnknown
                    ? "The restricted Process proof model cannot prove arbitrary data-dependent Choice predicates exhaustive."
                    : "Every Choice predicate is statically false, so the declared exhaustive branch leaves an execution path uncovered.",
                Child(location, "completeness"),
                subject: choice.Id.Value,
                expected: "proved exhaustive branch coverage",
                observed: hasUnknown ? "unknown" : "disproven");
        }

        void ValidateMatchExhaustiveness(MatchProcessNode match, string location)
        {
            if (match.Completeness != BranchCompleteness.Exhaustive || match.Cases.IsDefaultOrEmpty)
                return;

            if (!TryGetConstant(match.Value, out var value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.ExhaustivenessUnknown,
                    "Exact Process Match patterns cannot prove coverage of a dynamic value domain; add an explicit fallback.",
                    Child(location, "completeness"),
                    subject: match.Id.Value,
                    expected: "proved exhaustive branch coverage",
                    observed: "unknown");
                return;
            }

            if (match.Cases.Any(matchCase => matchCase is not null && PatternMatches(matchCase.Pattern, value)))
                return;

            Error(
                ProcessDefinitionDiagnosticCodes.ExhaustivenessDisproven,
                "The statically known Match value is not covered by any declared exact pattern.",
                Child(location, "completeness"),
                subject: match.Id.Value,
                expected: "a case matching the statically known value",
                observed: value.ToString());
        }

        void ValidateFork(ForkProcessNode fork, string location)
        {
            if (fork.Branches.IsDefaultOrEmpty)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.ForkBranchesEmpty,
                    "A Process Fork requires at least one branch token.",
                    Child(location, "branches"),
                    subject: fork.Id.Value);
            }

            for (var index = 0; index < fork.Branches.Length; index++)
            {
                var branch = fork.Branches[index];
                var branchLocation = $"{location}/branches/{index.ToString(CultureInfo.InvariantCulture)}";
                if (branch is null)
                {
                    Missing(branchLocation, "A Process Fork branch cannot be null.");
                    continue;
                }
                RegisterConstructId(branch.Id, Child(branchLocation, "id"));
                RegisterEdge(branch.Start, Child(branchLocation, "start"), fork.Id, branch.Id);
            }

            if (string.IsNullOrWhiteSpace(fork.Join.Value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.ForkJoinUnresolved,
                    "A Process Fork requires a stable reciprocal Join identity.",
                    Child(location, "join"),
                    subject: fork.Id.Value);
            }
        }

        void ValidateJoin(JoinProcessNode join, string location)
        {
            if (string.IsNullOrWhiteSpace(join.Fork.Value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.JoinForkUnresolved,
                    "A Process Join requires a stable reciprocal Fork identity.",
                    Child(location, "fork"),
                    subject: join.Id.Value);
            }

            if (join.Policy is null)
            {
                Missing(Child(location, "policy"), "A Process Join requires an explicit policy.");
            }
            else
            {
                ValidateEnum(join.Policy.Mode, Child(location, "policy/mode"));
                ValidateEnum(join.Policy.Failure, Child(location, "policy/failure"));
                ValidateEnum(join.Policy.Cancellation, Child(location, "policy/cancellation"));
                ValidateEnum(join.Policy.CompletionOrder, Child(location, "policy/completionOrder"));
                ValidateEnum(join.Policy.TieBreak, Child(location, "policy/tieBreak"));
            }
            RegisterEdge(join.Next, Child(location, "next"), join.Id);
        }

        void ValidateAwaitMatch(AwaitMatchProcessNode awaitMatch, string location)
        {
            ValidateEnum(awaitMatch.Arbitration, Child(location, "arbitration"));
            ValidateEnum(awaitMatch.LateInput, Child(location, "lateInput"));
            ValidateEnum(awaitMatch.StaleInput, Child(location, "staleInput"));
            ValidateEnum(awaitMatch.DuplicateInput, Child(location, "duplicateInput"));
            ValidateEnum(awaitMatch.MissingTarget, Child(location, "missingTarget"));
            if (awaitMatch.RetentionHorizon <= TimeSpan.Zero)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.AwaitRetentionInvalid,
                    "An AwaitMatch retention horizon must be positive.",
                    Child(location, "retentionHorizon"),
                    subject: awaitMatch.Id.Value);
            }
            if (awaitMatch.Clauses.IsDefaultOrEmpty)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.AwaitClausesEmpty,
                    "An AwaitMatch requires at least one typed interaction or timer clause.",
                    Child(location, "clauses"),
                    subject: awaitMatch.Id.Value);
                return;
            }

            Dictionary<ExecutionNodeId, string> clauseLocations = [];
            for (var index = 0; index < awaitMatch.Clauses.Length; index++)
            {
                var clause = awaitMatch.Clauses[index];
                var clauseLocation = $"{location}/clauses/{index.ToString(CultureInfo.InvariantCulture)}";
                if (clause is null)
                {
                    Missing(clauseLocation, "An AwaitMatch clause cannot be null.");
                    continue;
                }

                var idLocation = Child(clauseLocation, "id");
                if (string.IsNullOrWhiteSpace(clause.Id.Value))
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.NodeIdentityMissing,
                        "An AwaitMatch clause requires a stable identity.",
                        idLocation);
                }
                else if (!clauseLocations.TryAdd(clause.Id, idLocation))
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.AwaitClauseIdentityDuplicate,
                        $"AwaitMatch clause identity '{clause.Id.Value}' is duplicated.",
                        idLocation,
                        subject: clause.Id.Value,
                        relatedLocations: [clauseLocations[clause.Id]]);
                }
                else
                {
                    RegisterConstructId(clause.Id, idLocation);
                }

                switch (clause)
                {
                    case ProcessAwaitInteractionClause interaction:
                        ValidateAwaitInteraction(awaitMatch, interaction, clauseLocation);
                        break;
                    case ProcessAwaitTimerClause timer:
                        AddExpression(
                            awaitMatch.Id,
                            timer.DueAt,
                            Child(clauseLocation, "dueAt"),
                            InstantContract);
                        RegisterContinuation(
                            timer.Continuation,
                            Child(clauseLocation, "continuation"),
                            awaitMatch.Id,
                            expectedOutput: null,
                            outputAllowed: false);
                        break;
                    default:
                        Error(
                            ProcessDefinitionDiagnosticCodes.NodeUnsupported,
                            $"AwaitMatch clause '{clause.GetType().FullName}' is outside the closed v1 clause union.",
                            clauseLocation,
                            subject: clause.Id.Value);
                        break;
                }
            }
        }

        void ValidateAwaitInteraction(
            AwaitMatchProcessNode awaitMatch,
            ProcessAwaitInteractionClause interaction,
            string location)
        {
            var definitionContract = ResolveInteraction(
                interaction.Contract,
                Child(location, "contract"));
            var expected = GetInteractionInputContract(definitionContract);
            if (interaction.Input is null)
            {
                Missing(Child(location, "input"), "An interaction AwaitMatch clause requires a typed input binding.");
            }
            else
            {
                ValidateContract(interaction.Input.Contract, Child(location, "input/contract"));
                if (expected is not null && interaction.Input.Contract != expected)
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.AwaitInputContractMismatch,
                        "The AwaitMatch input binding must use the exact interaction payload contract.",
                        Child(location, "input/contract"),
                        subject: interaction.Id.Value);
                }
            }

            var continuation = RegisterContinuation(
                interaction.Continuation,
                Child(location, "continuation"),
                awaitMatch.Id,
                expected ?? interaction.Input?.Contract);
            if (interaction.Input is not null)
            {
                RegisterBinding(
                    interaction.Input,
                    Child(location, "input"),
                    awaitMatch.Id,
                    continuation);
            }

            if (interaction.Contract is RequestContractReference requestContract)
            {
                if (interaction.RequestObligation is null)
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.AwaitRequestObligationInvalid,
                        "An AwaitMatch Request clause must retain the admitted logical Request obligation for a later Reply.",
                        Child(location, "requestObligation"),
                        subject: interaction.Id.Value);
                }
                else
                {
                    RegisterRequestObligation(
                        interaction.RequestObligation,
                        requestContract,
                        Child(location, "requestObligation"),
                        awaitMatch.Id,
                        continuation);
                }
            }
            else if (interaction.RequestObligation is not null)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.AwaitRequestObligationInvalid,
                    "Only an AwaitMatch Request clause can produce a Request-obligation binding.",
                    Child(location, "requestObligation"),
                    subject: interaction.Id.Value);
            }

            if (interaction.Guard is not null)
            {
                AddExpression(
                    awaitMatch.Id,
                    interaction.Guard,
                    Child(location, "guard"),
                    expectedBoolean: true,
                    localBinding: interaction.Input);
            }
        }

        void ValidateReply(ReplyProcessNode reply, string location)
        {
            var contract = ResolveInteraction<ReplyContractDefinition>(
                reply.Contract,
                Child(location, "contract"));
            if (string.IsNullOrWhiteSpace(reply.Request.Value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.ReplyRequestObligationUnresolved,
                    "A Reply must identify a stable inbound Request-obligation binding.",
                    Child(location, "request"),
                    subject: reply.Id.Value);
            }
            else
            {
                replyRequests.Add(new(reply.Id, reply.Request, Child(location, "request"), contract?.Request));
            }
            AddExpression(reply.Id, reply.Payload, Child(location, "payload"), GetReplyPayloadContract(contract));
            RegisterEdge(reply.Next, Child(location, "next"), reply.Id);
        }

        void ValidateFallback(
            BranchCompleteness completeness,
            ProcessFallback? fallback,
            string location)
        {
            if (completeness == BranchCompleteness.Fallback && fallback is null)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.FallbackContractInvalid,
                    "Fallback completeness requires an explicit fallback branch.",
                    Child(location, "fallback"));
            }
            else if (completeness == BranchCompleteness.Exhaustive && fallback is not null)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.FallbackContractInvalid,
                    "Exhaustive completeness cannot also declare a fallback branch.",
                    Child(location, "fallback"));
            }
        }

        EdgeInfo? RegisterContinuation(
            ProcessContinuation? continuation,
            string location,
            ExecutionNodeId source,
            ValueContract? expectedOutput,
            bool outputAllowed = true)
        {
            if (continuation is null)
            {
                Missing(location, "A Process continuation cannot be null.");
                return null;
            }

            var edge = RegisterEdge(continuation.Edge, Child(location, "edge"), source);
            if (continuation.Output is null)
                return edge;

            var outputLocation = Child(location, "output");
            if (!outputAllowed)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.OutputNotAllowed,
                    "This Process continuation produces no semantic value and cannot declare an output binding.",
                    outputLocation,
                    subject: source.Value);
            }
            if (expectedOutput is not null && continuation.Output.Contract != expectedOutput)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.OutputContractMismatch,
                    "The continuation output contract differs from the exact operation result contract.",
                    Child(outputLocation, "contract"),
                    subject: continuation.Output.Binding.Value,
                    expected: Describe(expectedOutput),
                    observed: Describe(continuation.Output.Contract));
            }

            RegisterBinding(continuation.Output, outputLocation, source, edge);
            return edge;
        }

        EdgeInfo? RegisterEdge(
            ProcessEdge? edge,
            string location,
            ExecutionNodeId source,
            ExecutionNodeId? forkBranch = null)
        {
            if (edge is null)
            {
                Missing(location, "A Process control-flow edge cannot be null.");
                return null;
            }

            var idLocation = Child(location, "id");
            if (string.IsNullOrWhiteSpace(edge.Id.Value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.EdgeIdentityMissing,
                    "A Process edge requires a stable identity.",
                    idLocation,
                    subject: source.Value);
            }
            else if (!edgeLocations.TryAdd(edge.Id, idLocation))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.EdgeIdentityDuplicate,
                    $"Process edge identity '{edge.Id.Value}' is duplicated.",
                    idLocation,
                    subject: edge.Id.Value,
                    relatedLocations: [edgeLocations[edge.Id]]);
            }

            var info = new EdgeInfo(edge, location, source, forkBranch);
            edges.Add(info);
            return info;
        }

        void RegisterBinding(
            ProcessOutputBinding output,
            string location,
            ExecutionNodeId producerNode,
            EdgeInfo? edge)
        {
            ValidateContract(output.Contract, Child(location, "contract"));
            var bindingLocation = Child(location, "binding");
            if (string.IsNullOrWhiteSpace(output.Binding.Value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.BindingIdentityMissing,
                    "A Process output requires a stable binding identity.",
                    bindingLocation,
                    subject: producerNode.Value);
                return;
            }

            if (!bindings.TryAdd(output.Binding, new(output.Contract, bindingLocation, producerNode)))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.BindingProducerDuplicate,
                    $"Binding '{output.Binding.Value}' has more than one producer.",
                    bindingLocation,
                    subject: output.Binding.Value,
                    relatedLocations: [bindings[output.Binding].Location]);
                return;
            }

            edge?.ProducedBindings.Add(output.Binding);
        }

        void RegisterRequestObligation(
            ProcessRequestObligationBinding obligation,
            RequestContractReference contract,
            string location,
            ExecutionNodeId producerNode,
            EdgeInfo? edge)
        {
            var bindingLocation = Child(location, "binding");
            if (string.IsNullOrWhiteSpace(obligation.Binding.Value))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.RequestObligationIdentityMissing,
                    "A Request obligation requires a stable binding identity.",
                    bindingLocation,
                    subject: producerNode.Value);
                return;
            }

            if (!requestObligations.TryAdd(
                    obligation.Binding,
                    new(contract, bindingLocation, producerNode)))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.RequestObligationProducerDuplicate,
                    $"Request-obligation binding '{obligation.Binding.Value}' has more than one producer.",
                    bindingLocation,
                    subject: obligation.Binding.Value,
                    relatedLocations: [requestObligations[obligation.Binding].Location]);
                return;
            }

            edge?.ProducedRequestObligations.Add(obligation.Binding);
        }

        bool RegisterConstructId(
            ExecutionNodeId id,
            string location,
            string missingCode = ProcessDefinitionDiagnosticCodes.NodeIdentityMissing,
            string duplicateCode = ProcessDefinitionDiagnosticCodes.NodeIdentityDuplicate)
        {
            if (string.IsNullOrWhiteSpace(id.Value))
            {
                Error(missingCode, "A Process construct requires a stable identity.", location);
                return false;
            }
            if (constructLocations.TryAdd(id, location))
                return true;

            Error(
                duplicateCode,
                $"Process construct identity '{id.Value}' is duplicated.",
                location,
                subject: id.Value,
                relatedLocations: [constructLocations[id]]);
            return false;
        }

        ProcessDefinitionLink? ResolveDefinition(
            ExecutionDefinitionReference? reference,
            ProcessDefinitionLinkKind expectedKind,
            string location)
        {
            if (reference is null)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.DefinitionReferenceInvalid,
                    "A Process operation requires an exact definition reference.",
                    location);
                return null;
            }
            if (context is null)
                return null;
            if (!context.TryResolve(reference, out var link))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.DefinitionReferenceUnresolved,
                    $"Definition '{reference.DefinitionId.Value}' revision '{reference.RevisionId.Value}' is not linked.",
                    location,
                    subject: reference.DefinitionId.Value);
                return null;
            }
            if (link.Kind == expectedKind)
                return link;

            Error(
                ProcessDefinitionDiagnosticCodes.DefinitionReferenceKindMismatch,
                $"The linked definition is '{link.Kind}', but this node requires '{expectedKind}'.",
                location,
                subject: reference.DefinitionId.Value,
                expected: expectedKind.ToString(),
                observed: link.Kind.ToString());
            return null;
        }

        TDefinition? ResolveInteraction<TDefinition>(
            InteractionContractReference? reference,
            string location)
            where TDefinition : InteractionContractDefinition
        {
            var resolved = ResolveInteraction(reference, location);
            if (resolved is null)
                return null;
            if (resolved is TDefinition typed)
                return typed;

            Error(
                ProcessDefinitionDiagnosticCodes.InteractionReferenceKindMismatch,
                $"This Process node requires interaction family '{typeof(TDefinition).Name}', but the exact contract is '{resolved.GetType().Name}'.",
                location,
                subject: reference?.Definition.DefinitionId.Value,
                expected: typeof(TDefinition).Name,
                observed: resolved.GetType().Name);
            return null;
        }

        InteractionContractDefinition? ResolveInteraction(
            InteractionContractReference? reference,
            string location)
        {
            if (reference is null)
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.InteractionReferenceUnresolved,
                    "A Process interaction requires an exact typed contract reference.",
                    location);
                return null;
            }
            var catalog = context?.InteractionContracts;
            if (catalog is null)
                return null;
            if (catalog.TryResolve(reference, out var resolved))
                return resolved;
            if (catalog.TryResolve(reference.Definition, out var exact))
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.InteractionReferenceKindMismatch,
                    "The typed interaction reference family differs from the exact catalog contract.",
                    location,
                    subject: reference.Definition.DefinitionId.Value,
                    observed: exact.GetType().Name);
            }
            else
            {
                Error(
                    ProcessDefinitionDiagnosticCodes.InteractionReferenceUnresolved,
                    $"Interaction contract '{reference.Definition.DefinitionId.Value}' revision '{reference.Definition.RevisionId.Value}' is not linked.",
                    location,
                    subject: reference.Definition.DefinitionId.Value);
            }
            return null;
        }

        ValueContract? GetInteractionInputContract(InteractionContractDefinition? contract) => contract switch
        {
            DomainEventContractDefinition domainEvent => domainEvent.Payload.Contract,
            RequestContractDefinition request => request.Payload.Contract,
            SignalContractDefinition signal => signal.Payload.Contract,
            ReplyContractDefinition reply => GetReplyPayloadContract(reply),
            _ => null
        };

        ValueContract? GetReplyPayloadContract(ReplyContractDefinition? reply)
        {
            if (reply is null || context?.InteractionContracts is not { } catalog)
                return null;
            return catalog.TryResolve(reply.Request, out var requestDefinition)
                   && requestDefinition is RequestContractDefinition request
                ? request.Response.Find(reply.Outcome)?.Schema.Contract
                : null;
        }

        void ValidateContract(ValueContract? contract, string location)
        {
            if (contract is null)
            {
                Missing(location, "A Process value contract cannot be null.");
                return;
            }
            AppendValidation(
                PortableExecutionValidator.Validate(contract, context?.ShapeGraph),
                location);
        }

        void AddExpression(
            ExecutionNodeId owner,
            Expr? expression,
            string location,
            ValueContract? expected = null,
            bool expectedBoolean = false,
            ProcessOutputBinding? localBinding = null)
        {
            if (expression is null)
            {
                Missing(location, "A Process expression cannot be null.");
                return;
            }
            AppendValidation(
                PortableExecutionValidator.Validate(expression, context?.ShapeGraph),
                location);
            expressions.Add(new(owner, expression, location, expected, expectedBoolean, localBinding));
        }

        void ValidateEnum<TEnum>(TEnum value, string location)
            where TEnum : struct, Enum
        {
            if (Enum.IsDefined(value)
                && Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0)
            {
                return;
            }

            Error(
                ProcessDefinitionDiagnosticCodes.EnumUnsupported,
                $"Process enum value '{value}' is unspecified or unsupported.",
                location,
                observed: Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }

        void ValidateEdgeTargets()
        {
            foreach (var edge in edges)
            {
                if (!string.IsNullOrWhiteSpace(edge.Edge.Target.Value)
                    && nodes.ContainsKey(edge.Edge.Target))
                {
                    continue;
                }

                Error(
                    ProcessDefinitionDiagnosticCodes.EdgeTargetUnresolved,
                    $"Process edge '{edge.Edge.Id.Value}' must target one declared node.",
                    Child(edge.Location, "target"),
                    subject: edge.Edge.Id.Value,
                    observed: edge.Edge.Target.Value);
            }
        }

        void ValidateGraph()
        {
            var outgoing = edges
                .GroupBy(static edge => edge.Source)
                .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
            var reachable = FindReachable(outgoing);
            foreach (var node in nodes)
            {
                if (reachable.Contains(node.Key))
                    continue;
                Error(
                    ProcessDefinitionDiagnosticCodes.NodeUnreachable,
                    $"Process node '{node.Key.Value}' is unreachable from the declared entry.",
                    node.Value.Location,
                    subject: node.Key.Value);
            }

            foreach (var node in nodes.Values)
            {
                if (IsTerminal(node.Node) || IsDurableBoundary(node.Node))
                    continue;
                if (outgoing.TryGetValue(node.Node.Id, out var nodeEdges) && nodeEdges.Length > 0)
                    continue;
                Error(
                    ProcessDefinitionDiagnosticCodes.NonTerminalDeadEnd,
                    "Every nonterminal Process node must continue or reach a durable boundary.",
                    node.Location,
                    subject: node.Node.Id.Value);
            }

            ValidateForkJoin(outgoing, reachable);
            ValidateActivationCycles(outgoing, reachable);
        }

        HashSet<ExecutionNodeId> FindReachable(
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing)
        {
            HashSet<ExecutionNodeId> reachable = [];
            if (!nodes.ContainsKey(definition.Entry))
                return reachable;

            Queue<ExecutionNodeId> pending = new();
            pending.Enqueue(definition.Entry);
            while (pending.TryDequeue(out var current))
            {
                if (!reachable.Add(current) || !outgoing.TryGetValue(current, out var currentEdges))
                    continue;
                foreach (var edge in currentEdges)
                {
                    if (nodes.ContainsKey(edge.Edge.Target))
                        pending.Enqueue(edge.Edge.Target);
                }
            }
            return reachable;
        }

        void ValidateForkJoin(
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing,
            IReadOnlySet<ExecutionNodeId> reachable)
        {
            foreach (var pair in nodes)
            {
                if (pair.Value.Node is ForkProcessNode fork)
                {
                    if (!nodes.TryGetValue(fork.Join, out var joinInfo)
                        || joinInfo.Node is not JoinProcessNode join)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.ForkJoinUnresolved,
                            $"Fork '{fork.Id.Value}' does not identify a declared Join.",
                            Child(pair.Value.Location, "join"),
                            subject: fork.Id.Value,
                            observed: fork.Join.Value);
                        continue;
                    }
                    if (join.Fork != fork.Id)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.ForkJoinNotReciprocal,
                            $"Fork '{fork.Id.Value}' and Join '{join.Id.Value}' do not reference one another.",
                            Child(pair.Value.Location, "join"),
                            subject: fork.Id.Value,
                            relatedLocations: [Child(joinInfo.Location, "fork")]);
                    }

                    ValidateJoinThreshold(join, joinInfo.Location, fork.Branches.Length);
                    var branchAnalyses = ImmutableArray.CreateBuilder<BranchLineageInfo>(fork.Branches.Length);
                    var ownedIngress = new HashSet<EdgeInfo>();
                    for (var index = 0; index < fork.Branches.Length; index++)
                    {
                        var branch = fork.Branches[index];
                        if (branch?.Start is null || !nodes.ContainsKey(branch.Start.Target))
                            continue;

                        var startEdge = edges.FirstOrDefault(edge =>
                            edge.Source == fork.Id
                            && edge.ForkBranch == branch.Id
                            && edge.Edge.Id == branch.Start.Id);
                        if (startEdge is null)
                            continue;

                        var analysis = AnalyzeForkBranch(branch, startEdge, join.Id, outgoing);
                        branchAnalyses.Add(analysis);
                        ownedIngress.UnionWith(analysis.JoinIngress);
                        if (analysis.Converges)
                            continue;

                        var reason = analysis.ForeignJoin is { } foreignJoin
                            ? $" crosses foreign Join '{foreignJoin.Value}'"
                            : analysis.HasFreeCycle
                                ? " contains a control-flow cycle that does not cross a durable boundary"
                                : analysis.JoinIngress.IsDefaultOrEmpty
                                    ? $" has no structural exit to Join '{join.Id.Value}'"
                                    : analysis.HasClosedRegion
                                        ? $" contains a closed region with no structural exit to Join '{join.Id.Value}'"
                                        : $" has a finite exit that does not converge on Join '{join.Id.Value}'";
                        var relatedLocations = analysis.ForeignJoin is { } foreign
                            && nodes.TryGetValue(foreign, out var foreignInfo)
                                ? ImmutableArray.Create(joinInfo.Location, foreignInfo.Location)
                                : ImmutableArray.Create(joinInfo.Location);
                        Error(
                            ProcessDefinitionDiagnosticCodes.ForkBranchDoesNotConverge,
                            $"Fork branch '{branch.Id.Value}'{reason}.",
                            $"{pair.Value.Location}/branches/{index.ToString(CultureInfo.InvariantCulture)}/start",
                            subject: branch.Id.Value,
                            relatedLocations: relatedLocations);
                    }

                    var ingressIsOwned = true;
                    var ownedLineageNodes = branchAnalyses
                        .SelectMany(static branch => branch.Nodes)
                        .ToHashSet();
                    var ownedBranchStarts = branchAnalyses
                        .Select(static branch => branch.Start)
                        .ToHashSet();
                    foreach (var lineageIngress in edges.Where(edge =>
                                 ownedLineageNodes.Contains(edge.Edge.Target)
                                 && reachable.Contains(edge.Source)))
                    {
                        if (ownedLineageNodes.Contains(lineageIngress.Source)
                            || ownedBranchStarts.Contains(lineageIngress))
                        {
                            continue;
                        }

                        ingressIsOwned = false;
                        Error(
                            ProcessDefinitionDiagnosticCodes.JoinIngressNotOwned,
                            $"Reachable edge '{lineageIngress.Edge.Id.Value}' enters the branch lineage of Join '{join.Id.Value}' outside its reciprocal Fork.",
                            lineageIngress.Location,
                            subject: lineageIngress.Edge.Id.Value,
                            relatedLocations: [pair.Value.Location, joinInfo.Location]);
                    }

                    foreach (var ingress in edges.Where(edge =>
                                 edge.Edge.Target == join.Id
                                 && reachable.Contains(edge.Source)))
                    {
                        if (ownedIngress.Contains(ingress))
                            continue;

                        ingressIsOwned = false;
                        Error(
                            ProcessDefinitionDiagnosticCodes.JoinIngressNotOwned,
                            $"Reachable edge '{ingress.Edge.Id.Value}' enters Join '{join.Id.Value}' outside the reciprocal Fork's branch lineage.",
                            ingress.Location,
                            subject: ingress.Edge.Id.Value,
                            relatedLocations: [pair.Value.Location, joinInfo.Location]);
                    }

                    if (join.Fork == fork.Id)
                    {
                        var isSound = fork.Branches.Length > 0
                                      && branchAnalyses.Count == fork.Branches.Length
                                      && branchAnalyses.All(static branch => branch.Converges)
                                      && ingressIsOwned;
                        forkJoinsByJoin[join.Id] = new(
                            fork,
                            join,
                            branchAnalyses.Count == branchAnalyses.Capacity
                                ? branchAnalyses.MoveToImmutable()
                                : branchAnalyses.ToImmutable(),
                            isSound);
                    }
                }
                else if (pair.Value.Node is JoinProcessNode join)
                {
                    if (!nodes.TryGetValue(join.Fork, out var forkInfo)
                        || forkInfo.Node is not ForkProcessNode reciprocalFork)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.JoinForkUnresolved,
                            $"Join '{join.Id.Value}' does not identify a declared Fork.",
                            Child(pair.Value.Location, "fork"),
                            subject: join.Id.Value,
                            observed: join.Fork.Value);
                    }
                    else if (reciprocalFork.Join != join.Id)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.ForkJoinNotReciprocal,
                            $"Join '{join.Id.Value}' and Fork '{reciprocalFork.Id.Value}' do not reference one another.",
                            Child(pair.Value.Location, "fork"),
                            subject: join.Id.Value,
                            relatedLocations: [Child(forkInfo.Location, "join")]);
                    }
                }
            }
        }

        void ValidateJoinThreshold(JoinProcessNode join, string location, int branchCount)
        {
            if (join.Policy is null)
                return;
            var valid = join.Policy.Mode switch
            {
                ProcessJoinMode.All or ProcessJoinMode.Any => join.Policy.RequiredCount == 0,
                ProcessJoinMode.RequiredCount => join.Policy.RequiredCount > 0
                                                 && join.Policy.RequiredCount <= branchCount,
                _ => true
            };
            if (valid)
                return;
            Error(
                ProcessDefinitionDiagnosticCodes.JoinRequiredCountInvalid,
                "A Join required count must be zero for All/Any, or between one and the reciprocal branch count for RequiredCount.",
                Child(location, "policy/requiredCount"),
                subject: join.Id.Value,
                expected: join.Policy.Mode == ProcessJoinMode.RequiredCount
                    ? $"1..{branchCount.ToString(CultureInfo.InvariantCulture)}"
                    : "0",
                observed: join.Policy.RequiredCount.ToString(CultureInfo.InvariantCulture));
        }

        BranchLineageInfo AnalyzeForkBranch(
            ProcessForkBranch branch,
            EdgeInfo start,
            ExecutionNodeId target,
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing)
        {
            HashSet<EdgeInfo> joinIngress = [];
            HashSet<ExecutionNodeId> lineageNodes = [];
            ExecutionNodeId? foreignJoin = null;
            var hasInvalidExit = false;
            if (start.Edge.Target == target)
            {
                joinIngress.Add(start);
            }
            else
            {
                Queue<ExecutionNodeId> pending = new();
                pending.Enqueue(start.Edge.Target);
                while (pending.TryDequeue(out var current))
                {
                    if (!lineageNodes.Add(current))
                        continue;
                    if (!nodes.TryGetValue(current, out var node)
                        || IsTerminal(node.Node)
                        || !outgoing.TryGetValue(current, out var currentEdges)
                        || currentEdges.IsDefaultOrEmpty)
                    {
                        hasInvalidExit = true;
                        continue;
                    }

                    if (node.Node is JoinProcessNode)
                    {
                        foreignJoin ??= current;
                        hasInvalidExit = true;
                        continue;
                    }

                    foreach (var edge in currentEdges)
                    {
                        if (edge.Edge.Target == target)
                            joinIngress.Add(edge);
                        else
                            pending.Enqueue(edge.Edge.Target);
                    }
                }
            }

            var hasClosedRegion = HasClosedForkRegion(lineageNodes, joinIngress, outgoing);
            var hasFreeCycle = HasFreeForkCycle(lineageNodes, outgoing);
            var converges = !hasInvalidExit
                            && foreignJoin is null
                            && joinIngress.Count > 0
                            && !hasClosedRegion
                            && !hasFreeCycle;

            return new(
                branch,
                start,
                [.. lineageNodes.OrderBy(static id => id.Value, StringComparer.Ordinal)],
                [.. joinIngress.OrderBy(static edge => edge.Edge.Id.Value, StringComparer.Ordinal)],
                converges,
                foreignJoin,
                hasClosedRegion,
                hasFreeCycle);
        }

        static bool HasClosedForkRegion(
            IReadOnlySet<ExecutionNodeId> lineageNodes,
            IReadOnlySet<EdgeInfo> joinIngress,
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing)
        {
            if (lineageNodes.Count == 0)
                return false;

            Dictionary<ExecutionNodeId, List<ExecutionNodeId>> predecessors = [];
            foreach (var source in lineageNodes)
            {
                if (!outgoing.TryGetValue(source, out var sourceEdges))
                    continue;
                foreach (var edge in sourceEdges)
                {
                    if (!lineageNodes.Contains(edge.Edge.Target))
                        continue;
                    if (!predecessors.TryGetValue(edge.Edge.Target, out var incoming))
                    {
                        incoming = [];
                        predecessors.Add(edge.Edge.Target, incoming);
                    }
                    incoming.Add(source);
                }
            }

            HashSet<ExecutionNodeId> canReachJoin = [];
            Queue<ExecutionNodeId> pending = new();
            foreach (var ingress in joinIngress)
            {
                if (lineageNodes.Contains(ingress.Source))
                    pending.Enqueue(ingress.Source);
            }
            while (pending.TryDequeue(out var current))
            {
                if (!canReachJoin.Add(current)
                    || !predecessors.TryGetValue(current, out var incoming))
                {
                    continue;
                }
                foreach (var predecessor in incoming)
                    pending.Enqueue(predecessor);
            }

            return lineageNodes.Any(node => !canReachJoin.Contains(node));
        }

        bool HasFreeForkCycle(
            IReadOnlySet<ExecutionNodeId> lineageNodes,
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing)
        {
            Dictionary<ExecutionNodeId, VisitState> states = [];
            foreach (var node in lineageNodes.OrderBy(static id => id.Value, StringComparer.Ordinal))
            {
                if (!states.ContainsKey(node)
                    && VisitFreeForkCycle(node, lineageNodes, outgoing, states))
                    return true;
            }
            return false;
        }

        bool VisitFreeForkCycle(
            ExecutionNodeId current,
            IReadOnlySet<ExecutionNodeId> lineageNodes,
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing,
            Dictionary<ExecutionNodeId, VisitState> states)
        {
            states[current] = VisitState.Active;
            if (nodes.TryGetValue(current, out var node)
                && !IsDurableBoundary(node.Node)
                && outgoing.TryGetValue(current, out var currentEdges))
            {
                foreach (var edge in currentEdges)
                {
                    var target = edge.Edge.Target;
                    if (!lineageNodes.Contains(target))
                        continue;
                    if (!states.TryGetValue(target, out var state))
                    {
                        if (VisitFreeForkCycle(target, lineageNodes, outgoing, states))
                            return true;
                    }
                    else if (state == VisitState.Active)
                    {
                        return true;
                    }
                }
            }
            states[current] = VisitState.Complete;
            return false;
        }

        void ValidateActivationCycles(
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing,
            HashSet<ExecutionNodeId> reachable)
        {
            Dictionary<ExecutionNodeId, VisitState> states = [];
            foreach (var node in reachable.OrderBy(static id => id.Value, StringComparer.Ordinal))
            {
                if (!states.ContainsKey(node))
                    VisitActivation(node, outgoing, reachable, states);
            }
        }

        void VisitActivation(
            ExecutionNodeId current,
            IReadOnlyDictionary<ExecutionNodeId, ImmutableArray<EdgeInfo>> outgoing,
            HashSet<ExecutionNodeId> reachable,
            Dictionary<ExecutionNodeId, VisitState> states)
        {
            states[current] = VisitState.Active;
            if (nodes.TryGetValue(current, out var node)
                && !IsDurableBoundary(node.Node)
                && outgoing.TryGetValue(current, out var currentEdges))
            {
                foreach (var edge in currentEdges)
                {
                    var target = edge.Edge.Target;
                    if (!reachable.Contains(target))
                        continue;
                    if (!states.TryGetValue(target, out var state))
                    {
                        VisitActivation(target, outgoing, reachable, states);
                    }
                    else if (state == VisitState.Active)
                    {
                        Error(
                            ProcessDefinitionDiagnosticCodes.FreeActivationCycle,
                            $"Edge '{edge.Edge.Id.Value}' closes a cycle without crossing a durable boundary.",
                            edge.Location,
                            subject: edge.Edge.Id.Value,
                            relatedLocations: [nodes[target].Location]);
                    }
                }
            }
            states[current] = VisitState.Complete;
        }

        void ValidateBindingFlowAndExpressions()
        {
            if (nodes.Count == 0 || expressions.Count == 0)
                return;

            var visible = ComputeDefiniteFlow(
                bindings.Keys,
                bindings.ContainsKey(ProcessBindingIds.Input)
                    ? [ProcessBindingIds.Input]
                    : [],
                static edge => edge.ProducedBindings);

            foreach (var expression in expressions)
            {
                var available = visible.TryGetValue(expression.Owner, out var nodeVisible)
                    ? new HashSet<ValueBindingId>(nodeVisible)
                    : [];
                if (expression.LocalBinding is { } local
                    && !string.IsNullOrWhiteSpace(local.Binding.Value))
                {
                    available.Add(local.Binding);
                }

                var scopeBindings = ImmutableArray.CreateBuilder<ExprScopeBinding>(available.Count);
                foreach (var binding in available.OrderBy(static id => id.Value, StringComparer.Ordinal))
                {
                    if (expression.LocalBinding is { Contract: not null } localBinding
                        && localBinding.Binding == binding)
                    {
                        scopeBindings.Add(new(binding, localBinding.Contract));
                    }
                    else if (bindings.TryGetValue(binding, out var contract)
                             && contract.Contract is not null)
                    {
                        scopeBindings.Add(new(binding, contract.Contract));
                    }
                }

                ExprExpectation expectation;
                if (expression.ExpectedBoolean)
                    expectation = ExprExpectation.Boolean;
                else if (expression.Expected is { } expected)
                    expectation = new(value: expected);
                else
                    expectation = ExprExpectation.Any;
                var analysis = ExprAnalyzer.Analyze(new(
                    new ExprSiteId($"process:{expression.Owner.Value}:{expression.Location}"),
                    expression.Expression,
                    new ExprScope(scopeBindings.ToImmutable()),
                    expectation,
                    ProcessExpressionLanguage.Capabilities,
                    diagnosticLocation: expression.Location));
                diagnostics.AddRange(analysis.Validation.Diagnostics);
            }
        }

        void ValidateRequestObligationFlow()
        {
            if (nodes.Count == 0 || replyRequests.Count == 0)
                return;

            var visible = ComputeDefiniteFlow(
                requestObligations.Keys,
                Array.Empty<RequestObligationBindingId>(),
                static edge => edge.ProducedRequestObligations);

            foreach (var reply in replyRequests)
            {
                if (!requestObligations.TryGetValue(reply.Request, out var obligation))
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.ReplyRequestObligationUnresolved,
                        $"Reply Request-obligation binding '{reply.Request.Value}' has no producer.",
                        reply.Location,
                        subject: reply.Request.Value);
                    continue;
                }

                if (!visible.TryGetValue(reply.Owner, out var available) || !available.Contains(reply.Request))
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.ReplyRequestObligationUnresolved,
                        $"Reply Request-obligation binding '{reply.Request.Value}' is not definitely visible on every incoming path.",
                        reply.Location,
                        subject: reply.Request.Value,
                        relatedLocations: [obligation.Location]);
                    continue;
                }

                if (reply.ExpectedRequest is not null && obligation.Contract != reply.ExpectedRequest)
                {
                    Error(
                        ProcessDefinitionDiagnosticCodes.ReplyRequestContractMismatch,
                        "The Reply contract does not discharge the exact Request contract retained by its target obligation.",
                        reply.Location,
                        subject: reply.Request.Value,
                        relatedLocations: [obligation.Location],
                        expected: reply.ExpectedRequest.Definition.DefinitionId.Value,
                        observed: obligation.Contract.Definition.DefinitionId.Value);
                }
            }
        }

        Dictionary<ExecutionNodeId, HashSet<TBinding>> ComputeDefiniteFlow<TBinding>(
            IEnumerable<TBinding> bindingUniverse,
            IReadOnlyCollection<TBinding> entryBindings,
            Func<EdgeInfo, IEnumerable<TBinding>> producedBindings)
            where TBinding : notnull
        {
            var incoming = edges
                .Where(edge => nodes.ContainsKey(edge.Edge.Target))
                .GroupBy(static edge => edge.Edge.Target)
                .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
            var universe = bindingUniverse.ToHashSet();
            var visible = nodes.Keys.ToDictionary(
                static node => node,
                node => node == definition.Entry
                    ? new HashSet<TBinding>(entryBindings)
                    : new HashSet<TBinding>(universe));

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var node in nodes.Keys)
                {
                    if (node == definition.Entry)
                        continue;
                    HashSet<TBinding> next;
                    if (forkJoinsByJoin.TryGetValue(node, out var forkJoin))
                    {
                        next = ComputeJoinVisible(forkJoin, visible, producedBindings);
                    }
                    else if (!incoming.TryGetValue(node, out var nodeIncoming) || nodeIncoming.IsDefaultOrEmpty)
                    {
                        next = [];
                    }
                    else
                    {
                        next = null!;
                        foreach (var edge in nodeIncoming)
                        {
                            var from = visible.TryGetValue(edge.Source, out var sourceVisible)
                                ? new HashSet<TBinding>(sourceVisible)
                                : [];
                            from.UnionWith(producedBindings(edge));
                            if (next is null)
                                next = from;
                            else
                                next.IntersectWith(from);
                        }
                        next ??= [];
                    }
                    if (visible[node].SetEquals(next))
                        continue;
                    visible[node] = next;
                    changed = true;
                }
            }
            return visible;
        }

        static HashSet<TBinding> ComputeJoinVisible<TBinding>(
            ForkJoinInfo forkJoin,
            IReadOnlyDictionary<ExecutionNodeId, HashSet<TBinding>> visible,
            Func<EdgeInfo, IEnumerable<TBinding>> producedBindings)
            where TBinding : notnull
        {
            var result = visible.TryGetValue(forkJoin.Fork.Id, out var forkVisible)
                ? new HashSet<TBinding>(forkVisible)
                : [];

            // A partial Join selects control progress, not a stable composite binding environment. Until Process IR
            // models explicit branch-result aggregation, Any and RequiredCount expose only the pre-Fork scope.
            if (!forkJoin.IsSound
                || forkJoin.Join.Policy is null
                || forkJoin.Join.Policy.Mode != ProcessJoinMode.All)
            {
                return result;
            }

            foreach (var branch in forkJoin.Branches)
            {
                HashSet<TBinding>? guaranteed = null;
                foreach (var ingress in branch.JoinIngress)
                {
                    var from = visible.TryGetValue(ingress.Source, out var sourceVisible)
                        ? new HashSet<TBinding>(sourceVisible)
                        : [];
                    from.UnionWith(producedBindings(ingress));
                    if (guaranteed is null)
                        guaranteed = from;
                    else
                        guaranteed.IntersectWith(from);
                }

                if (guaranteed is null)
                    return result;
                result.UnionWith(guaranteed);
            }
            return result;
        }

        static bool TryGetBooleanConstant(Expr? expression, out bool value)
        {
            switch (expression)
            {
                case ConstantExpr constant when constant.Value.TryGetBoolean(out value):
                    return true;
                case LiteralExpr literal when literal.Value.TryGetBoolean(out value):
                    return true;
                case UnaryExpr { Operator: UnaryOperator.Not } unary
                    when TryGetBooleanConstant(unary.Operand, out var operand):
                    value = !operand;
                    return true;
                case BinaryExpr { Operator: BinaryOperator.And } binary
                    when TryGetBooleanConstant(binary.Left, out var left):
                    if (!left)
                    {
                        value = false;
                        return true;
                    }
                    return TryGetBooleanConstant(binary.Right, out value);
                case BinaryExpr { Operator: BinaryOperator.Or } binary
                    when TryGetBooleanConstant(binary.Left, out var left):
                    if (left)
                    {
                        value = true;
                        return true;
                    }
                    return TryGetBooleanConstant(binary.Right, out value);
                case ConditionalExpr conditional
                    when TryGetBooleanConstant(conditional.Test, out var test):
                    return TryGetBooleanConstant(test ? conditional.IfTrue : conditional.IfFalse, out value);
                default:
                    value = false;
                    return false;
            }
        }

        static bool TryGetConstant(Expr? expression, out ObservationValue value)
        {
            switch (expression)
            {
                case ConstantExpr constant:
                    value = constant.Value;
                    return true;
                case LiteralExpr literal:
                    value = literal.Value;
                    return true;
                case ConditionalExpr conditional
                    when TryGetBooleanConstant(conditional.Test, out var test):
                    return TryGetConstant(test ? conditional.IfTrue : conditional.IfFalse, out value);
                default:
                    value = default;
                    return false;
            }
        }

        static bool PatternMatches(PortableValue? pattern, ObservationValue value) =>
            pattern?.State switch
            {
                PortableValueState.Null => value.Kind == ObservationValueKind.Null,
                PortableValueState.Absent => value.Kind == ObservationValueKind.Undefined,
                PortableValueState.Concrete => pattern.Value is { } concrete && concrete.Equals(value),
                _ => false
            };

        static bool IsDurableBoundary(ProcessNode node) =>
            node is RequestProcessNode
                or AwaitMatchProcessNode
                or TimerProcessNode
                or DurableCutProcessNode;

        static bool IsTerminal(ProcessNode node) => node is ReturnProcessNode or FailProcessNode;

        void AppendValidation(DocumentValidationResult validation, string prefix)
        {
            foreach (var diagnostic in validation.Diagnostics)
            {
                var location = Prefix(prefix, diagnostic.Location);
                DocumentDiagnosticEvidence? evidence = diagnostic.Evidence;
                if (evidence is not null && !evidence.RelatedLocations.IsDefaultOrEmpty)
                {
                    evidence = new(
                        evidence.Stage,
                        evidence.Subject,
                        [.. evidence.RelatedLocations.Select(related => Prefix(prefix, related))],
                        evidence.SourceReferences,
                        evidence.ResolutionOptions,
                        evidence.Expected,
                        evidence.Observed);
                }
                diagnostics.Add(diagnostic with { Location = location, Evidence = evidence });
            }
        }

        void Missing(string location, string message) => Error(
            ProcessDefinitionDiagnosticCodes.RequiredMemberMissing,
            message,
            location);

        void Error(
            string code,
            string message,
            string location,
            string? subject = null,
            ImmutableArray<string> relatedLocations = default,
            string? expected = null,
            string? observed = null)
        {
            DocumentDiagnosticEvidence? evidence = subject is null
                                                   && relatedLocations.IsDefaultOrEmpty
                                                   && expected is null
                                                   && observed is null
                ? null
                : new(
                    stage: "processValidation",
                    subject: subject,
                    relatedLocations: relatedLocations,
                    expected: expected,
                    observed: observed);
            diagnostics.Add(new(code, DiagnosticSeverity.Error, message, location, Evidence: evidence));
        }

        static string Describe(ValueContract? contract) => contract?.GetEffectiveType()?.ToString()
                                                           ?? contract?.Shape?.ToString()
                                                           ?? "untyped";

        static string Child(string location, string child) =>
            location.Length == 0 ? "/" + child : location + "/" + child;

        static string Prefix(string prefix, string? location)
        {
            if (string.IsNullOrEmpty(location) || location == "$")
                return prefix;
            return location[0] == '/' ? prefix + location : prefix;
        }

        sealed record NodeInfo(ProcessNode Node, string Location);

        sealed record BindingInfo(
            ValueContract Contract,
            string Location,
            ExecutionNodeId? ProducerNode);

        sealed record RequestObligationInfo(
            RequestContractReference Contract,
            string Location,
            ExecutionNodeId ProducerNode);

        sealed record ReplyRequestInfo(
            ExecutionNodeId Owner,
            RequestObligationBindingId Request,
            string Location,
            RequestContractReference? ExpectedRequest);

        sealed record BranchLineageInfo(
            ProcessForkBranch Branch,
            EdgeInfo Start,
            ImmutableArray<ExecutionNodeId> Nodes,
            ImmutableArray<EdgeInfo> JoinIngress,
            bool Converges,
            ExecutionNodeId? ForeignJoin,
            bool HasClosedRegion,
            bool HasFreeCycle);

        sealed record ForkJoinInfo(
            ForkProcessNode Fork,
            JoinProcessNode Join,
            ImmutableArray<BranchLineageInfo> Branches,
            bool IsSound);

        sealed class EdgeInfo(
            ProcessEdge edge,
            string location,
            ExecutionNodeId source,
            ExecutionNodeId? forkBranch)
        {
            public ProcessEdge Edge { get; } = edge;

            public string Location { get; } = location;

            public ExecutionNodeId Source { get; } = source;

            public ExecutionNodeId? ForkBranch { get; } = forkBranch;

            public HashSet<ValueBindingId> ProducedBindings { get; } = [];

            public HashSet<RequestObligationBindingId> ProducedRequestObligations { get; } = [];
        }

        sealed record ExpressionInfo(
            ExecutionNodeId Owner,
            Expr Expression,
            string Location,
            ValueContract? Expected,
            bool ExpectedBoolean,
            ProcessOutputBinding? LocalBinding);

        enum VisitState
        {
            Active,
            Complete
        }
    }
}

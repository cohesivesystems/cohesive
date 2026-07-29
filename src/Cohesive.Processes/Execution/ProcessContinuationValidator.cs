using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Processes.Execution;

/// <summary>Stable diagnostics emitted while admitting a restored canonical Process continuation.</summary>
public static class ProcessContinuationDiagnosticCodes
{
    /// <summary>The restored continuation pins a different exact Process definition.</summary>
    public const string DefinitionMismatch = "processes.execution.continuation.definitionMismatch";

    /// <summary>A durable state collection repeats one of its stable identities.</summary>
    public const string IdentityDuplicate = "processes.execution.continuation.identityDuplicate";

    /// <summary>A durable state collection is not in its required ordinal identity order.</summary>
    public const string CanonicalOrderInvalid = "processes.execution.continuation.canonicalOrderInvalid";

    /// <summary>A durable state member is absent or has no usable stable identity.</summary>
    public const string StateMemberInvalid = "processes.execution.continuation.stateMemberInvalid";

    /// <summary>A continuation node identity is absent from the exact compiled Process plan.</summary>
    public const string NodeUnresolved = "processes.execution.continuation.nodeUnresolved";

    /// <summary>A token lifecycle value, typed binding, obligation, step, or failure is malformed.</summary>
    public const string TokenStateInvalid = "processes.execution.continuation.tokenStateInvalid";

    /// <summary>An active wait and its owning waiting token contradict one another.</summary>
    public const string WaitTokenMismatch = "processes.execution.continuation.waitTokenMismatch";

    /// <summary>A wait kind, timer set, obligation, or winner contradicts its exact compiled node.</summary>
    public const string WaitShapeMismatch = "processes.execution.continuation.waitShapeMismatch";

    /// <summary>Buffered input and its durable semantic receipt contradict one another.</summary>
    public const string InputStateMismatch = "processes.execution.continuation.inputStateMismatch";

    /// <summary>A Request wait and its outstanding logical Request evidence contradict one another.</summary>
    public const string RequestStateMismatch = "processes.execution.continuation.requestStateMismatch";

    /// <summary>A Fork registration, branch token, or reciprocal plan node contradicts another.</summary>
    public const string ForkStateMismatch = "processes.execution.continuation.forkStateMismatch";

    /// <summary>A terminal continuation retains state that can no longer be consumed.</summary>
    public const string TerminalStateInvalid = "processes.execution.continuation.terminalStateInvalid";
}

/// <summary>Validates a restored Process continuation before any host operation may execute.</summary>
/// <remarks>
/// This validator is a pure, fail-closed interpretation of persisted coordination state. It reports semantic
/// corruption through deterministic structured diagnostics and does not repair, reorder, or otherwise mutate the
/// supplied continuation.
/// </remarks>
public static class ProcessContinuationValidator
{
    /// <summary>Validates one restored continuation against its exact compiled Process plan.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan selected for recovery.</param>
    /// <param name="state">Restored immutable continuation to admit before execution.</param>
    /// <returns>Deterministically ordered diagnostics; a result with no errors admits the continuation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="state"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        CompiledProcessPlan plan,
        ProcessContinuationState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        return new ValidationContext(plan, state).Validate();
    }

    sealed class ValidationContext(
        CompiledProcessPlan plan,
        ProcessContinuationState state)
    {
        const string ValidationStage = "processContinuationRestore";

        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly Dictionary<ExecutionNodeId, CanonicalProcessNode> planNodes = plan.Definition.Nodes
            .ToDictionary(static node => node.Id);
        readonly Dictionary<ValueBindingId, ValueContract> bindingContracts = BuildBindingContracts(plan.Definition);
        readonly Dictionary<RequestObligationBindingId, RequestContractReference> obligationContracts =
            BuildObligationContracts(plan.Definition);
        readonly Dictionary<TokenId, (ProcessTokenState Token, int Index)> tokens = [];
        readonly Dictionary<string, (ProcessForkState Fork, int Index)> forks = new(StringComparer.Ordinal);
        readonly Dictionary<ProcessWaitRegistrationId, (ProcessWaitState Wait, int Index)> waits = [];
        readonly Dictionary<EmissionId, (ProcessBufferedInput Input, int Index)> bufferedInputs = [];
        readonly Dictionary<EmissionId, (ProcessInputReceipt Receipt, int Index)> inputReceipts = [];
        readonly Dictionary<EmissionId, (ProcessOutstandingRequest Request, int Index)> requests = [];

        public DocumentValidationResult Validate()
        {
            ValidateDefinition();
            ValidateCanonicalCollections();
            IndexState();
            ValidateTokenState();
            ValidateNodeReferences();
            ValidateWaits();
            ValidateInputs();
            ValidateRequests();
            ValidateForks();
            ValidateTerminalState();
            diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        void ValidateTokenState()
        {
            if (state.CompletedActivationCount < 0)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.StateMemberInvalid,
                    "A restored continuation cannot have a negative completed-activation count.",
                    "/completedActivationCount",
                    observed: state.CompletedActivationCount.ToString(CultureInfo.InvariantCulture));
            }

            for (var index = 0; index < state.Tokens.Length; index++)
            {
                var token = state.Tokens[index];
                if (token is null)
                {
                    continue;
                }

                var location = ItemLocation("/tokens", index);
                if (!Enum.IsDefined(token.Disposition)
                    || token.Disposition is ExecutionTokenDisposition.Unspecified
                        or ExecutionTokenDisposition.Active)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Token '{token.Id.Value}' has no durable lifecycle disposition.",
                        Child(location, "disposition"),
                        subject: token.Id.Value,
                        observed: token.Disposition.ToString());
                }

                if (token.Step < 0)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Token '{token.Id.Value}' has a negative execution step.",
                        Child(location, "step"),
                        subject: token.Id.Value,
                        observed: token.Step.ToString(CultureInfo.InvariantCulture));
                }

                var failureValid = token.Disposition == ExecutionTokenDisposition.Failed
                    ? token.Failure is
                    {
                        Severity: DiagnosticSeverity.Error,
                        Code.Length: > 0,
                        Message.Length: > 0
                    }
                    : token.Failure is null;
                if (!failureValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Token '{token.Id.Value}' failure evidence contradicts its lifecycle disposition.",
                        Child(location, "failure"),
                        subject: token.Id.Value);
                }

                ValidateBindings(token.Bindings, Child(location, "bindings"), token.Id.Value);
                ValidateObligations(
                    token.RequestObligations,
                    Child(location, "requestObligations"),
                    token.Id.Value);
            }

            for (var index = 0; index < state.Forks.Length; index++)
            {
                var fork = state.Forks[index];
                if (fork is null)
                {
                    continue;
                }

                var location = ItemLocation("/forks", index);
                ValidateBindings(fork.ParentBindings, Child(location, "parentBindings"), fork.RegistrationId);
                ValidateObligations(
                    fork.ParentRequestObligations,
                    Child(location, "parentRequestObligations"),
                    fork.RegistrationId);
            }
        }

        void ValidateBindings(
            ImmutableArray<ProcessBindingValue> values,
            string location,
            string subject)
        {
            HashSet<ValueBindingId> identities = [];
            string? previous = null;
            for (var index = 0; index < values.Length; index++)
            {
                var binding = values[index];
                var bindingLocation = ItemLocation(location, index);
                if (binding is null || string.IsNullOrWhiteSpace(binding.Binding.Value))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        "A token binding requires a stable identity and typed value.",
                        bindingLocation,
                        subject);
                    continue;
                }

                var identity = binding.Binding.Value;
                var canonical = identities.Add(binding.Binding)
                    && (previous is null || StringComparer.Ordinal.Compare(previous, identity) < 0);
                if (!canonical)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Token binding '{identity}' is duplicated or not in canonical order.",
                        Child(bindingLocation, "binding"),
                        identity);
                }
                previous = identity;

                var value = binding.Value;
                var valueValid = value is not null
                    && value.State is not (PortableValueState.Unknown or PortableValueState.Failed)
                    && bindingContracts.TryGetValue(binding.Binding, out var expected)
                    && value.Contract == expected
                    && PortableExecutionValidator.Validate(
                        value,
                        plan.ValidationContext.ShapeGraph).IsValid;
                if (!valueValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Token binding '{identity}' is undeclared or violates its exact portable contract.",
                        Child(bindingLocation, "value"),
                        identity);
                }
            }
        }

        void ValidateObligations(
            ImmutableArray<ProcessRequestObligation> values,
            string location,
            string subject)
        {
            HashSet<RequestObligationBindingId> bindings = [];
            HashSet<EmissionId> emissions = [];
            string? previous = null;
            for (var index = 0; index < values.Length; index++)
            {
                var obligation = values[index];
                var obligationLocation = ItemLocation(location, index);
                if (obligation is null
                    || string.IsNullOrWhiteSpace(obligation.Binding.Value)
                    || obligation.Request?.Context is not { } context)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        "A Request obligation requires a stable binding and exact Request envelope.",
                        obligationLocation,
                        subject);
                    continue;
                }

                var identity = obligation.Binding.Value;
                var canonical = bindings.Add(obligation.Binding)
                    && emissions.Add(context.EmissionId)
                    && (previous is null || StringComparer.Ordinal.Compare(previous, identity) < 0);
                if (!canonical)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Request obligation '{identity}' is duplicated or not in canonical order.",
                        Child(obligationLocation, "binding"),
                        identity);
                }
                previous = identity;

                var contracts = plan.ValidationContext.InteractionContracts;
                var requestValid = obligationContracts.TryGetValue(obligation.Binding, out var expected)
                    && obligation.Request.Contract == expected
                    && contracts is not null
                    && InteractionEnvelopeValidator.Validate(
                        obligation.Request,
                        contracts,
                        plan.ValidationContext.ShapeGraph).IsValid;
                if (!requestValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.TokenStateInvalid,
                        $"Request obligation '{identity}' is undeclared or violates its exact Request contract.",
                        Child(obligationLocation, "request"),
                        identity);
                }
            }
        }

        void ValidateDefinition()
        {
            if (state.Definition is not null && state.Definition == plan.DefinitionReference)
            {
                return;
            }

            Error(
                ProcessContinuationDiagnosticCodes.DefinitionMismatch,
                "Restored continuation definition identity, revision, or fingerprint differs from the compiled plan.",
                "/definition",
                subject: state.Continuation?.ProcessInstanceId.Value,
                expected: Format(plan.DefinitionReference),
                observed: Format(state.Definition));
        }

        void ValidateCanonicalCollections()
        {
            ValidateCanonicalIdentities(
                state.Tokens,
                static token => token?.Id.Value,
                "/tokens",
                "id");
            ValidateCanonicalIdentities(
                state.Forks,
                static fork => fork?.RegistrationId,
                "/forks",
                "registrationId");
            ValidateCanonicalIdentities(
                state.Waits,
                static wait => wait?.RegistrationId.Value,
                "/waits",
                "registrationId");
            ValidateCanonicalIdentities(
                state.BufferedInputs,
                static buffered => buffered?.Input?.Envelope?.Context?.EmissionId.Value,
                "/bufferedInputs",
                "input/envelope/context/emissionId");
            ValidateCanonicalIdentities(
                state.InputReceipts,
                static receipt => receipt?.Input?.Envelope?.Context?.EmissionId.Value,
                "/inputReceipts",
                "input/envelope/context/emissionId");
            ValidateCanonicalIdentities(
                state.OutstandingRequests,
                static request => request?.Emission.Value,
                "/outstandingRequests",
                "emission");
        }

        void ValidateCanonicalIdentities<T>(
            ImmutableArray<T> values,
            Func<T, string?> selectIdentity,
            string collectionLocation,
            string identityMember)
            where T : class
        {
            HashSet<string> observed = new(StringComparer.Ordinal);
            string? previous = null;
            for (var index = 0; index < values.Length; index++)
            {
                var item = values[index];
                var location = ItemLocation(collectionLocation, index, identityMember);
                if (item is null)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.StateMemberInvalid,
                        "A restored durable state collection cannot contain a null member.",
                        ItemLocation(collectionLocation, index));
                    continue;
                }

                var identity = selectIdentity(item);
                if (string.IsNullOrWhiteSpace(identity))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.StateMemberInvalid,
                        "A restored durable state member requires a stable identity.",
                        location);
                    continue;
                }

                if (!observed.Add(identity))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.IdentityDuplicate,
                        $"Stable identity '{identity}' occurs more than once in restored durable state.",
                        location,
                        subject: identity);
                }

                if (previous is not null
                    && StringComparer.Ordinal.Compare(previous, identity) > 0)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.CanonicalOrderInvalid,
                        "Restored durable state identities must be retained in ordinal order.",
                        location,
                        subject: identity,
                        expected: $">= {previous}",
                        observed: identity);
                }

                previous = identity;
            }
        }

        void IndexState()
        {
            for (var index = 0; index < state.Tokens.Length; index++)
            {
                var token = state.Tokens[index];
                if (token is not null && !string.IsNullOrWhiteSpace(token.Id.Value))
                {
                    tokens.TryAdd(token.Id, (token, index));
                }
            }

            for (var index = 0; index < state.Forks.Length; index++)
            {
                var fork = state.Forks[index];
                if (fork is not null && !string.IsNullOrWhiteSpace(fork.RegistrationId))
                {
                    forks.TryAdd(fork.RegistrationId, (fork, index));
                }
            }

            for (var index = 0; index < state.Waits.Length; index++)
            {
                var wait = state.Waits[index];
                if (wait is not null && !string.IsNullOrWhiteSpace(wait.RegistrationId.Value))
                {
                    waits.TryAdd(wait.RegistrationId, (wait, index));
                }
            }

            for (var index = 0; index < state.BufferedInputs.Length; index++)
            {
                var buffered = state.BufferedInputs[index];
                if (buffered?.Input?.Envelope?.Context is { } context
                    && !string.IsNullOrWhiteSpace(context.EmissionId.Value))
                {
                    bufferedInputs.TryAdd(context.EmissionId, (buffered, index));
                }
            }

            for (var index = 0; index < state.InputReceipts.Length; index++)
            {
                var receipt = state.InputReceipts[index];
                if (receipt?.Input?.Envelope?.Context is { } context
                    && !string.IsNullOrWhiteSpace(context.EmissionId.Value))
                {
                    inputReceipts.TryAdd(context.EmissionId, (receipt, index));
                }
            }

            for (var index = 0; index < state.OutstandingRequests.Length; index++)
            {
                var request = state.OutstandingRequests[index];
                if (request is not null && !string.IsNullOrWhiteSpace(request.Emission.Value))
                {
                    requests.TryAdd(request.Emission, (request, index));
                }
            }
        }

        void ValidateNodeReferences()
        {
            for (var index = 0; index < state.Tokens.Length; index++)
            {
                var token = state.Tokens[index];
                if (token is not null)
                {
                    ResolveNode(token.Node, ItemLocation("/tokens", index, "node"));
                }
            }

            for (var index = 0; index < state.Forks.Length; index++)
            {
                var fork = state.Forks[index];
                if (fork is null)
                {
                    continue;
                }

                ResolveNode(fork.Fork, ItemLocation("/forks", index, "fork"));
                ResolveNode(fork.Join, ItemLocation("/forks", index, "join"));
            }

            for (var index = 0; index < state.Waits.Length; index++)
            {
                var wait = state.Waits[index];
                if (wait is not null)
                {
                    ResolveNode(wait.Node, ItemLocation("/waits", index, "node"));
                }
            }

            for (var index = 0; index < state.OutstandingRequests.Length; index++)
            {
                var request = state.OutstandingRequests[index];
                if (request is not null)
                {
                    ResolveNode(request.Node, ItemLocation("/outstandingRequests", index, "node"));
                }
            }
        }

        void ValidateWaits()
        {
            foreach (var (wait, index) in waits.Values)
            {
                ValidateWaitShape(wait, index);
                if (!wait.Active)
                {
                    continue;
                }

                var location = ItemLocation("/waits", index);
                if (!tokens.TryGetValue(wait.Token, out var tokenEntry))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.WaitTokenMismatch,
                        $"Active wait '{wait.RegistrationId}' refers to missing token '{wait.Token.Value}'.",
                        Child(location, "token"),
                        subject: wait.RegistrationId.Value);
                    continue;
                }

                if (tokenEntry.Token.Disposition != ExecutionTokenDisposition.Waiting
                    || tokenEntry.Token.Node != wait.Node)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.WaitTokenMismatch,
                        $"Active wait '{wait.RegistrationId}' must be paired with its waiting token at the same node.",
                        location,
                        subject: wait.RegistrationId.Value,
                        expected: $"waiting:{wait.Node.Value}",
                        observed: $"{tokenEntry.Token.Disposition}:{tokenEntry.Token.Node.Value}");
                }
            }

            foreach (var (token, tokenIndex) in tokens.Values)
            {
                var activeWaitCount = waits.Values.Count(candidate =>
                    candidate.Wait.Active && candidate.Wait.Token == token.Id);
                var unresolvedForkCount = forks.Values.Count(candidate =>
                    !candidate.Fork.Resolved
                    && candidate.Fork.Owner == token.Id
                    && candidate.Fork.Join == token.Node);
                if (token.Disposition == ExecutionTokenDisposition.Waiting)
                {
                    if (activeWaitCount + unresolvedForkCount == 1)
                    {
                        continue;
                    }

                    Error(
                        ProcessContinuationDiagnosticCodes.WaitTokenMismatch,
                        $"Waiting token '{token.Id.Value}' must have exactly one active wait or unresolved Join registration.",
                        ItemLocation("/tokens", tokenIndex),
                        subject: token.Id.Value,
                        expected: "1",
                        observed: (activeWaitCount + unresolvedForkCount).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        void ValidateWaitShape(ProcessWaitState wait, int index)
        {
            var location = ItemLocation("/waits", index);
            if (!planNodes.TryGetValue(wait.Node, out var node))
            {
                return;
            }

            var kindMatches = (wait.Kind, node) switch
            {
                (ProcessWaitKind.AwaitMatch, AwaitMatchProcessNode) => true,
                (ProcessWaitKind.Timer, TimerProcessNode) => true,
                (ProcessWaitKind.DurableCut, DurableCutProcessNode) => true,
                (ProcessWaitKind.Request, RequestProcessNode) => true,
                _ => false
            };
            if (!kindMatches)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.WaitShapeMismatch,
                    $"Wait '{wait.RegistrationId}' kind '{wait.Kind}' contradicts compiled node '{node.GetType().Name}'.",
                    Child(location, "kind"),
                    subject: wait.RegistrationId.Value);
                return;
            }

            var registeredAtValid = wait.RegisteredAtUtc != default
                && wait.RegisteredAtUtc.Offset == TimeSpan.Zero;
            var timersValid = wait.Timers.All(static timer =>
                timer is not null
                && timer.DueAtUtc != default
                && timer.DueAtUtc.Offset == TimeSpan.Zero);
            timersValid = timersValid && node switch
            {
                TimerProcessNode => wait.Timers is
                [
                    {
                        Clause: var clause,
                        Priority: 0
                    }
                ] && clause == wait.Node,
                AwaitMatchProcessNode awaitMatch => TimersMatch(awaitMatch, wait.Timers),
                _ => wait.Timers.IsEmpty
            };
            if (!registeredAtValid || !timersValid)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.WaitShapeMismatch,
                    $"Wait '{wait.RegistrationId}' has invalid registration time or timer evidence for its compiled node.",
                    Child(location, "timers"),
                    subject: wait.RegistrationId.Value);
            }

            var obligationValid = wait.Kind == ProcessWaitKind.Request
                ? wait.ObligationEmission is not null
                : wait.ObligationEmission is null;
            var activeWinnerValid = !wait.Active
                || wait.WinnerClause is null && wait.WinnerInput is null;
            var retainedWinnerValid = (wait.Kind, node, wait.WinnerClause, wait.WinnerInput) switch
            {
                (_, _, null, null) => true,
                (ProcessWaitKind.Timer, TimerProcessNode, { } winner, null) => winner == wait.Node,
                (ProcessWaitKind.DurableCut, DurableCutProcessNode, _, _) => false,
                (ProcessWaitKind.Request, RequestProcessNode request, { } winner, not null) =>
                    request.Outcomes.Any(outcome => outcome.Id == winner),
                (ProcessWaitKind.AwaitMatch, AwaitMatchProcessNode awaitMatch, { } winner, var input) =>
                    awaitMatch.Clauses.Any(clause =>
                        clause.Id == winner
                        && (clause is ProcessAwaitInteractionClause) == (input is not null)),
                _ => false
            };
            if (!obligationValid || !activeWinnerValid || !retainedWinnerValid)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.WaitShapeMismatch,
                    $"Wait '{wait.RegistrationId}' has invalid Request obligation or winner evidence.",
                    location,
                    subject: wait.RegistrationId.Value);
            }
        }

        static bool TimersMatch(
            AwaitMatchProcessNode node,
            ImmutableArray<ProcessTimerState> timers)
        {
            var expected = node.Clauses.OfType<ProcessAwaitTimerClause>().ToArray();
            if (expected.Length != timers.Length)
            {
                return false;
            }

            for (var index = 0; index < expected.Length; index++)
            {
                if (timers[index].Clause != expected[index].Id
                    || timers[index].Priority != expected[index].Priority)
                {
                    return false;
                }
            }
            return true;
        }

        void ValidateInputs()
        {
            foreach (var (emission, entry) in bufferedInputs)
            {
                if (inputReceipts.TryGetValue(emission, out var receipt)
                    && receipt.Receipt.Disposition == ProcessInputAdmissionDisposition.Buffered
                    && receipt.Receipt.Input == entry.Input.Input)
                {
                    continue;
                }

                Error(
                    ProcessContinuationDiagnosticCodes.InputStateMismatch,
                    $"Buffered input '{emission.Value}' requires one exact Buffered semantic receipt.",
                    ItemLocation("/bufferedInputs", entry.Index),
                    subject: emission.Value);
            }

            foreach (var (emission, entry) in inputReceipts)
            {
                var hasExactBuffer = bufferedInputs.TryGetValue(emission, out var buffered)
                    && buffered.Input.Input == entry.Receipt.Input;
                if ((entry.Receipt.Disposition == ProcessInputAdmissionDisposition.Buffered) == hasExactBuffer)
                {
                    continue;
                }

                Error(
                    ProcessContinuationDiagnosticCodes.InputStateMismatch,
                    entry.Receipt.Disposition == ProcessInputAdmissionDisposition.Buffered
                        ? $"Buffered receipt '{emission.Value}' has no exact retained input."
                        : $"Dispositioned input '{emission.Value}' remains buffered.",
                    ItemLocation("/inputReceipts", entry.Index),
                    subject: emission.Value);
            }
        }

        void ValidateRequests()
        {
            foreach (var (wait, waitIndex) in waits.Values)
            {
                if (!wait.Active || wait.Kind != ProcessWaitKind.Request)
                {
                    continue;
                }

                var location = ItemLocation("/waits", waitIndex);
                if (wait.ObligationEmission is not { } emission
                    || !requests.TryGetValue(emission, out var requestEntry)
                    || requestEntry.Request.Token != wait.Token
                    || requestEntry.Request.Node != wait.Node)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.RequestStateMismatch,
                        $"Active Request wait '{wait.RegistrationId}' has no exact outstanding Request obligation.",
                        location,
                        subject: wait.RegistrationId.Value);
                    continue;
                }

                if (!planNodes.TryGetValue(wait.Node, out var node)
                    || node is not RequestProcessNode requestNode
                    || requestEntry.Request.Contract != requestNode.Contract)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.RequestStateMismatch,
                        $"Outstanding Request '{emission.Value}' does not match the exact compiled Request node.",
                        ItemLocation("/outstandingRequests", requestEntry.Index),
                        subject: emission.Value);
                }
            }

            foreach (var (request, requestIndex) in requests.Values)
            {
                var matchingWaits = waits.Values.Count(candidate =>
                    candidate.Wait.Active
                    && candidate.Wait.Kind == ProcessWaitKind.Request
                    && candidate.Wait.ObligationEmission == request.Emission
                    && candidate.Wait.Token == request.Token
                    && candidate.Wait.Node == request.Node);
                if (matchingWaits == 1)
                {
                    continue;
                }

                Error(
                    ProcessContinuationDiagnosticCodes.RequestStateMismatch,
                    $"Outstanding Request '{request.Emission.Value}' must have exactly one matching active Request wait.",
                    ItemLocation("/outstandingRequests", requestIndex),
                    subject: request.Emission.Value,
                    expected: "1",
                    observed: matchingWaits.ToString(CultureInfo.InvariantCulture));
            }
        }

        void ValidateForks()
        {
            foreach (var (fork, forkIndex) in forks.Values)
            {
                ValidateFork(fork, forkIndex);
            }

            foreach (var (token, tokenIndex) in tokens.Values)
            {
                if (token.ForkMembership is not { } membership)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(membership.RegistrationId)
                    || !forks.TryGetValue(membership.RegistrationId, out var forkEntry)
                    || !forkEntry.Fork.Branches.Any(branch =>
                        branch.Token == token.Id && branch.Branch == membership.Branch))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        $"Token '{token.Id.Value}' has no exact owning Fork branch membership.",
                        ItemLocation("/tokens", tokenIndex, "forkMembership"),
                        subject: token.Id.Value);
                }
            }
        }

        void ValidateFork(ProcessForkState fork, int forkIndex)
        {
            var location = ItemLocation("/forks", forkIndex);
            if (!planNodes.TryGetValue(fork.Fork, out var forkPlanNode)
                || forkPlanNode is not ForkProcessNode forkNode
                || !planNodes.TryGetValue(fork.Join, out var joinPlanNode)
                || joinPlanNode is not JoinProcessNode joinNode
                || forkNode.Join != fork.Join
                || joinNode.Fork != fork.Fork)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' does not match reciprocal compiled Fork and Join nodes.",
                    location,
                    subject: fork.RegistrationId);
                return;
            }

            var ownerFound = tokens.TryGetValue(fork.Owner, out var owner);
            var ownerIsCoordinator = ownerFound && owner.Token.ForkMembership is null;
            var ownerIsParked = ownerFound
                && owner.Token.Node == fork.Join
                && owner.Token.Disposition == ExecutionTokenDisposition.Waiting;
            var attemptAllowsAbortedFork = state.Terminal is
            {
                Kind: ExecutionTerminalOutcomeKind.Failed
                    or ExecutionTerminalOutcomeKind.Cancelled
                    or ExecutionTerminalOutcomeKind.Terminated
            };
            if (!ownerIsCoordinator
                || fork.Resolved && ownerIsParked
                || !fork.Resolved && !attemptAllowsAbortedFork && !ownerIsParked
                || !fork.Resolved && attemptAllowsAbortedFork && IsLive(owner.Token.Disposition))
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' has no coherent coordinator token.",
                    Child(location, "owner"),
                    subject: fork.RegistrationId);
            }

            var occurrenceValid = fork.Occurrence >= 0;
            if (!occurrenceValid)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' has a negative occurrence.",
                    Child(location, "occurrence"),
                    subject: fork.RegistrationId,
                    observed: fork.Occurrence.ToString(CultureInfo.InvariantCulture));
            }
            else if (ownerFound && owner.Token.Step <= fork.Occurrence)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' occurs outside its owner token history.",
                    Child(location, "occurrence"),
                    subject: fork.RegistrationId,
                    expected: $"< {owner.Token.Step}",
                    observed: fork.Occurrence.ToString(CultureInfo.InvariantCulture));
            }

            var continuation = state.Continuation;
            var identityInputsValid = occurrenceValid
                && continuation is not null
                && !string.IsNullOrWhiteSpace(fork.Owner.Value);
            if (identityInputsValid)
            {
                var expectedRegistration = ProcessReferenceIdentities.ForkRegistration(
                    continuation!,
                    fork.Owner,
                    fork.Fork,
                    fork.Occurrence);
                if (!string.Equals(fork.RegistrationId, expectedRegistration, StringComparison.Ordinal))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        $"Fork registration '{fork.RegistrationId}' does not match its exact durable occurrence identity.",
                        Child(location, "registrationId"),
                        subject: fork.RegistrationId,
                        expected: expectedRegistration,
                        observed: fork.RegistrationId);
                }
            }

            HashSet<ExecutionNodeId> branchIds = [];
            HashSet<TokenId> branchTokens = [];
            HashSet<long> completionSequences = [];
            Dictionary<ExecutionNodeId, ProcessForkBranchState> branchesById = [];
            var declaredBranches = forkNode.Branches.Select(static branch => branch.Id).ToHashSet();
            var terminalBranchCount = 0;
            foreach (var (branch, branchIndex) in fork.Branches.Select(static (branch, index) => (branch, index)))
            {
                var branchLocation = ItemLocation(Child(location, "branches"), branchIndex);
                if (branch is null)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        "A restored Fork cannot contain a null branch state.",
                        branchLocation,
                        subject: fork.RegistrationId);
                    continue;
                }

                var branchIdentityUnique = branchIds.Add(branch.Branch);
                if (branchIdentityUnique)
                {
                    branchesById.Add(branch.Branch, branch);
                }
                var canDeriveBranchToken = identityInputsValid
                    && !string.IsNullOrWhiteSpace(branch.Branch.Value);
                var expectedToken = canDeriveBranchToken
                    ? ProcessReferenceIdentities.ForkToken(
                        continuation!,
                        fork.Owner,
                        fork.Fork,
                        fork.Occurrence,
                        branch.Branch)
                    : default;
                var coherent = branchIndex < forkNode.Branches.Length
                    && forkNode.Branches[branchIndex].Id == branch.Branch
                    && declaredBranches.Contains(branch.Branch)
                    && branchIdentityUnique
                    && branchTokens.Add(branch.Token)
                    && branch.Token != fork.Owner
                    && (!canDeriveBranchToken || branch.Token == expectedToken)
                    && tokens.TryGetValue(branch.Token, out var child)
                    && child.Token.ForkMembership is { } membership
                    && string.Equals(membership.RegistrationId, fork.RegistrationId, StringComparison.Ordinal)
                    && membership.Branch == branch.Branch
                    && child.Token.Disposition == branch.Disposition;
                if (!coherent)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        $"Fork branch '{branch.Branch.Value}' contradicts its declared child token membership or disposition.",
                        branchLocation,
                        subject: branch.Branch.Value);
                }

                var terminalBranch = IsTerminal(branch.Disposition);
                terminalBranchCount += terminalBranch ? 1 : 0;
                var completionSequenceValid = joinNode.Policy.CompletionOrder switch
                {
                    ProcessJoinCompletionOrder.Unobservable => branch.CompletionSequence is null,
                    ProcessJoinCompletionOrder.Observable when terminalBranch =>
                        branch.CompletionSequence is > 0
                        && completionSequences.Add(branch.CompletionSequence.Value),
                    ProcessJoinCompletionOrder.Observable => branch.CompletionSequence is null,
                    _ => false
                };
                if (!completionSequenceValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        $"Fork branch '{branch.Branch.Value}' has completion evidence that contradicts its disposition or Join policy.",
                        Child(branchLocation, "completionSequence"),
                        subject: branch.Branch.Value);
                }
            }

            if (fork.Branches.Length != forkNode.Branches.Length)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' must retain every compiled branch exactly once.",
                    Child(location, "branches"),
                    subject: fork.RegistrationId,
                    expected: forkNode.Branches.Length.ToString(CultureInfo.InvariantCulture),
                    observed: fork.Branches.Length.ToString(CultureInfo.InvariantCulture));
            }

            if (joinNode.Policy.CompletionOrder == ProcessJoinCompletionOrder.Observable
                && (completionSequences.Count != terminalBranchCount
                    || completionSequences.Any(sequence => sequence > terminalBranchCount)))
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' must retain one contiguous logical completion sequence for every terminal branch.",
                    Child(location, "branches"),
                    subject: fork.RegistrationId,
                    expected: terminalBranchCount == 0 ? "empty" : $"1..{terminalBranchCount}");
            }

            ValidateForkSelection(
                fork,
                joinNode.Policy,
                branchesById,
                terminalBranchCount,
                attemptAllowsAbortedFork,
                location);
        }

        void ValidateForkSelection(
            ProcessForkState fork,
            ProcessJoinPolicy policy,
            IReadOnlyDictionary<ExecutionNodeId, ProcessForkBranchState> branchesById,
            int terminalBranchCount,
            bool attemptAllowsAbortedFork,
            string location)
        {
            var threshold = policy.Mode switch
            {
                ProcessJoinMode.All => fork.Branches.Length,
                ProcessJoinMode.Any => 1,
                ProcessJoinMode.RequiredCount => policy.RequiredCount,
                _ => 0
            };
            var completed = fork.Branches
                .Where(static branch => branch is not null
                    && branch.Disposition == ExecutionTokenDisposition.Completed)
                .ToArray();
            HashSet<ExecutionNodeId> selectedIds = [];
            List<ProcessForkBranchState> selected = new(fork.SelectedBranches.Length);
            ProcessForkBranchState? previous = null;
            for (var selectedIndex = 0; selectedIndex < fork.SelectedBranches.Length; selectedIndex++)
            {
                var selectedId = fork.SelectedBranches[selectedIndex];
                var selectedLocation = ItemLocation(Child(location, "selectedBranches"), selectedIndex);
                var selectedIdentityUnique = selectedIds.Add(selectedId);
                var selectedMemberFound = branchesById.TryGetValue(selectedId, out var selectedBranch);
                var memberValid = !string.IsNullOrWhiteSpace(selectedId.Value)
                    && selectedIdentityUnique
                    && selectedMemberFound
                    && selectedBranch is
                    {
                        Disposition: ExecutionTokenDisposition.Completed
                    };
                if (!memberValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        $"Selected Fork branch '{selectedId.Value}' must be a unique completed member of the registration.",
                        selectedLocation,
                        subject: fork.RegistrationId);
                    continue;
                }

                selected.Add(selectedBranch!);
                if (previous is not null && CompareEligible(previous, selectedBranch!, policy) > 0)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                        $"Selected Fork branches do not follow the Join policy's canonical order.",
                        selectedLocation,
                        subject: fork.RegistrationId);
                }
                previous = selectedBranch;
            }

            var selectedCountValid = fork.SelectedBranches.IsEmpty
                || threshold > 0 && fork.SelectedBranches.Length == threshold;
            if (!selectedCountValid)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' retains the wrong number of selected branches for its Join policy.",
                    Child(location, "selectedBranches"),
                    subject: fork.RegistrationId,
                    expected: threshold.ToString(CultureInfo.InvariantCulture),
                    observed: fork.SelectedBranches.Length.ToString(CultureInfo.InvariantCulture));
            }

            var hasCompleteSelection = threshold > 0
                && fork.SelectedBranches.Length == threshold
                && selected.Count == threshold
                && selectedIds.Count == threshold;
            if (hasCompleteSelection
                && MustReconstructSelection(policy, completed.Length, threshold)
                && !selected.Select(static branch => branch.Branch).SequenceEqual(
                    OrderEligible(completed, policy)
                        .Take(threshold)
                        .Select(static branch => branch.Branch)))
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Fork registration '{fork.RegistrationId}' selected branches that contradict canonical Join arbitration.",
                    Child(location, "selectedBranches"),
                    subject: fork.RegistrationId);
            }

            var thresholdReached = threshold > 0 && completed.Length >= threshold;
            if (fork.Resolved)
            {
                var remainingBranchesValid = policy.Cancellation switch
                {
                    ProcessJoinCancellationPolicy.AwaitRemaining => terminalBranchCount == fork.Branches.Length,
                    ProcessJoinCancellationPolicy.CancelRemaining => fork.Branches.All(branch =>
                        branch is not null
                        && (selectedIds.Contains(branch.Branch) || IsTerminal(branch.Disposition))),
                    ProcessJoinCancellationPolicy.ContinueRemaining => true,
                    _ => false
                };
                if (thresholdReached && hasCompleteSelection && remainingBranchesValid)
                {
                    return;
                }

                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Resolved Fork registration '{fork.RegistrationId}' contradicts its Join threshold, selection, or remaining-branch policy.",
                    Child(location, "resolved"),
                    subject: fork.RegistrationId);
                return;
            }

            if (attemptAllowsAbortedFork)
            {
                if (fork.SelectedBranches.IsEmpty || thresholdReached && hasCompleteSelection)
                {
                    return;
                }

                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Terminal unresolved Fork registration '{fork.RegistrationId}' retains an incomplete Join selection.",
                    Child(location, "selectedBranches"),
                    subject: fork.RegistrationId);
                return;
            }

            var unresolvedStateValid = thresholdReached
                ? hasCompleteSelection
                    && policy.Cancellation == ProcessJoinCancellationPolicy.AwaitRemaining
                    && terminalBranchCount < fork.Branches.Length
                : fork.SelectedBranches.IsEmpty
                    && terminalBranchCount < fork.Branches.Length;
            if (!unresolvedStateValid)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ForkStateMismatch,
                    $"Unresolved Fork registration '{fork.RegistrationId}' contradicts its Join threshold or remaining-branch policy.",
                    Child(location, "resolved"),
                    subject: fork.RegistrationId);
            }
        }

        static bool MustReconstructSelection(
            ProcessJoinPolicy policy,
            int completedCount,
            int threshold) => policy.Mode == ProcessJoinMode.All
                || policy.TieBreak == ProcessJoinTieBreak.CompletionThenBranchIdentity
                || completedCount == threshold;

        static IEnumerable<ProcessForkBranchState> OrderEligible(
            IEnumerable<ProcessForkBranchState> completed,
            ProcessJoinPolicy policy) => policy.TieBreak switch
            {
                ProcessJoinTieBreak.BranchIdentity => completed.OrderBy(
                    static branch => branch.Branch.Value,
                    StringComparer.Ordinal),
                ProcessJoinTieBreak.CompletionThenBranchIdentity => completed
                    .OrderBy(static branch => branch.CompletionSequence)
                    .ThenBy(static branch => branch.Branch.Value, StringComparer.Ordinal),
                _ => []
            };

        static int CompareEligible(
            ProcessForkBranchState left,
            ProcessForkBranchState right,
            ProcessJoinPolicy policy)
        {
            if (policy.TieBreak == ProcessJoinTieBreak.CompletionThenBranchIdentity)
            {
                var sequence = Nullable.Compare(left.CompletionSequence, right.CompletionSequence);
                if (sequence != 0)
                {
                    return sequence;
                }
            }

            return StringComparer.Ordinal.Compare(left.Branch.Value, right.Branch.Value);
        }

        static bool IsTerminal(ExecutionTokenDisposition disposition) => disposition is
            ExecutionTokenDisposition.Completed
            or ExecutionTokenDisposition.Failed
            or ExecutionTokenDisposition.Cancelled;

        static bool IsLive(ExecutionTokenDisposition disposition) => disposition is
            ExecutionTokenDisposition.Ready
            or ExecutionTokenDisposition.Active
            or ExecutionTokenDisposition.Waiting;

        void ValidateTerminalState()
        {
            if (state.Terminal is null)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.StateMemberInvalid,
                    "A restored Process continuation requires terminal-outcome state.",
                    "/terminal");
                return;
            }

            if (state.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
            {
                return;
            }

            var liveTokens = state.Tokens.Count(static token => token is not null
                && token.Disposition is ExecutionTokenDisposition.Ready
                    or ExecutionTokenDisposition.Active
                    or ExecutionTokenDisposition.Waiting);
            var activeWaits = state.Waits.Count(static wait => wait is { Active: true });
            var liveForks = state.Forks.Count(fork => fork is { Resolved: false }
                && (tokens.TryGetValue(fork.Owner, out var owner)
                    && owner.Token.Disposition is ExecutionTokenDisposition.Ready
                        or ExecutionTokenDisposition.Active
                        or ExecutionTokenDisposition.Waiting
                    || fork.Branches.Any(branch => branch is not null
                        && branch.Disposition is ExecutionTokenDisposition.Ready
                            or ExecutionTokenDisposition.Active
                            or ExecutionTokenDisposition.Waiting)));
            if (state.BufferedInputs.IsDefaultOrEmpty
                && liveTokens == 0
                && activeWaits == 0
                && liveForks == 0
                && state.OutstandingRequests.IsDefaultOrEmpty)
            {
                return;
            }

            Error(
                ProcessContinuationDiagnosticCodes.TerminalStateInvalid,
                "A terminal Process continuation cannot retain live tokens, waits, Requests, Fork work, or buffered input.",
                state.BufferedInputs.IsDefaultOrEmpty ? "/terminal" : "/bufferedInputs",
                subject: state.Continuation?.ProcessInstanceId.Value,
                expected: "no live work",
                observed: $"tokens={liveTokens}; waits={activeWaits}; forks={liveForks}; requests={state.OutstandingRequests.Length}; buffered={state.BufferedInputs.Length}");
        }

        static Dictionary<ValueBindingId, ValueContract> BuildBindingContracts(CanonicalProcessDefinition definition)
        {
            Dictionary<ValueBindingId, ValueContract> contracts =
                new() { [ProcessBindingIds.Input] = definition.Input };

            static void Add(
                IDictionary<ValueBindingId, ValueContract> target,
                ProcessOutputBinding? output)
            {
                if (output is not null)
                {
                    target[output.Binding] = output.Contract;
                }
            }

            foreach (var node in definition.Nodes)
            {
                switch (node)
                {
                    case InvokeTransitionProcessNode transition:
                        Add(contracts, transition.Continuation.Output);
                        break;
                    case EvaluateRelationProcessNode relation:
                        Add(contracts, relation.Continuation.Output);
                        break;
                    case RequestProcessNode request:
                        foreach (var outcome in request.Outcomes)
                        {
                            Add(contracts, outcome.Continuation.Output);
                        }
                        break;
                    case AwaitMatchProcessNode awaitMatch:
                        foreach (var clause in awaitMatch.Clauses)
                        {
                            Add(contracts, clause.Continuation.Output);
                            if (clause is ProcessAwaitInteractionClause interaction)
                            {
                                Add(contracts, interaction.Input);
                            }
                        }
                        break;
                }
            }
            return contracts;
        }

        static Dictionary<RequestObligationBindingId, RequestContractReference> BuildObligationContracts(
            CanonicalProcessDefinition definition)
        {
            Dictionary<RequestObligationBindingId, RequestContractReference> contracts = [];
            foreach (var awaitMatch in definition.Nodes.OfType<AwaitMatchProcessNode>())
            {
                foreach (var clause in awaitMatch.Clauses.OfType<ProcessAwaitInteractionClause>())
                {
                    if (clause.RequestObligation is { } obligation
                        && clause.Contract is RequestContractReference request)
                    {
                        contracts[obligation.Binding] = request;
                    }
                }
            }
            return contracts;
        }

        bool ResolveNode(ExecutionNodeId node, string location)
        {
            if (planNodes.ContainsKey(node))
            {
                return true;
            }

            Error(
                ProcessContinuationDiagnosticCodes.NodeUnresolved,
                $"Node '{node.Value}' is absent from the exact compiled Process plan.",
                location,
                subject: node.Value);
            return false;
        }

        void Error(
            string code,
            string message,
            string location,
            string? subject = null,
            string? expected = null,
            string? observed = null) => diagnostics.Add(new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: ValidationStage,
                subject: subject,
                sourceReferences: [plan.Document.Metadata.Provenance.Source.Reference],
                expected: expected,
                observed: observed)));

        static string ItemLocation(string collection, int index, string? member = null)
        {
            var location = $"{collection}/{index.ToString(CultureInfo.InvariantCulture)}";
            return member is null ? location : Child(location, member);
        }

        static string Child(string parent, string child) => $"{parent}/{child}";

        static string Format(ExecutionDefinitionReference? reference) => reference is null
            ? "<missing>"
            : $"{reference.DefinitionId.Value}@{reference.RevisionId.Value}#{reference.Fingerprint.Value}";
    }
}

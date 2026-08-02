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

    /// <summary>A child registration, identity, Request, or terminal result contradicts another.</summary>
    public const string ChildStateMismatch = "processes.execution.continuation.childStateMismatch";

    /// <summary>A bounded partition registration, work item, child, or owner contradicts another.</summary>
    public const string PartitionStateMismatch = "processes.execution.continuation.partitionStateMismatch";

    /// <summary>A recurrence identity, progress value, count, or durable wait contradicts another.</summary>
    public const string RecurrenceStateMismatch = "processes.execution.continuation.recurrenceStateMismatch";

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
        readonly Dictionary<string, (ProcessChildState Child, int Index)> children = new(StringComparer.Ordinal);
        readonly Dictionary<string, (ProcessPartitionState Partition, int Index)> partitions = new(StringComparer.Ordinal);
        readonly Dictionary<string, (ProcessRecurrenceState Recurrence, int Index)> recurrences = new(StringComparer.Ordinal);
        readonly Dictionary<ProcessWaitRegistrationId, (ProcessWaitState Wait, int Index)> waits = [];
        readonly Dictionary<EmissionId, (ProcessBufferedInput Input, int Index)> bufferedInputs = [];
        readonly Dictionary<EmissionId, (ProcessInputReceipt Receipt, int Index)> inputReceipts = [];
        readonly Dictionary<EmissionId, (ProcessOutstandingRequest Request, int Index)> requests = [];

        public DocumentValidationResult Validate()
        {
            ValidateDefinition();
            if (state.Continuation is null
                || string.IsNullOrWhiteSpace(state.Continuation.ProcessInstanceId.Value)
                || string.IsNullOrWhiteSpace(state.Continuation.ProcessAttemptId.Value))
            {
                Error(
                    ProcessContinuationDiagnosticCodes.StateMemberInvalid,
                    "A restored Process continuation requires exact instance and attempt identity evidence.",
                    "/continuation");
                diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
                return DocumentValidationResult.FromDiagnostics(diagnostics);
            }

            ValidateCanonicalCollections();
            IndexState();
            ValidateTokenState();
            ValidateNodeReferences();
            ValidateWaits();
            ValidateInputs();
            ValidateRequests();
            ValidateForks();
            ValidateChildren();
            ValidatePartitions();
            ValidateRecurrences();
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
                state.Children,
                static child => child?.RegistrationId,
                "/children",
                "registrationId");
            ValidateCanonicalIdentities(
                state.Partitions,
                static partition => partition?.RegistrationId,
                "/partitions",
                "registrationId");
            ValidateCanonicalIdentities(
                state.Recurrences,
                static recurrence => recurrence?.RegistrationId,
                "/recurrences",
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

            for (var index = 0; index < state.Children.Length; index++)
            {
                var child = state.Children[index];
                if (child is not null && !string.IsNullOrWhiteSpace(child.RegistrationId))
                {
                    children.TryAdd(child.RegistrationId, (child, index));
                }
            }

            for (var index = 0; index < state.Partitions.Length; index++)
            {
                var partition = state.Partitions[index];
                if (partition is not null && !string.IsNullOrWhiteSpace(partition.RegistrationId))
                {
                    partitions.TryAdd(partition.RegistrationId, (partition, index));
                }
            }

            for (var index = 0; index < state.Recurrences.Length; index++)
            {
                var recurrence = state.Recurrences[index];
                if (recurrence is not null && !string.IsNullOrWhiteSpace(recurrence.RegistrationId))
                {
                    recurrences.TryAdd(recurrence.RegistrationId, (recurrence, index));
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

            for (var index = 0; index < state.Children.Length; index++)
            {
                var child = state.Children[index];
                if (child is not null)
                {
                    ResolveNode(child.Node, ItemLocation("/children", index, "node"));
                }
            }

            for (var index = 0; index < state.Partitions.Length; index++)
            {
                var partition = state.Partitions[index];
                if (partition is not null)
                {
                    ResolveNode(partition.Node, ItemLocation("/partitions", index, "node"));
                }
            }

            for (var index = 0; index < state.Recurrences.Length; index++)
            {
                var recurrence = state.Recurrences[index];
                if (recurrence is not null)
                {
                    ResolveNode(recurrence.Node, ItemLocation("/recurrences", index, "node"));
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
            var tokenFound = tokens.TryGetValue(wait.Token, out var token);
            var identityValid = wait.Occurrence >= 0
                && tokenFound
                && !string.IsNullOrWhiteSpace(wait.Node.Value)
                && token.Token.Step > wait.Occurrence
                && (!wait.Active || token.Token.Step - 1 == wait.Occurrence)
                && wait.RegistrationId == ProcessReferenceIdentities.WaitRegistration(
                    state.Continuation,
                    wait.Token,
                    wait.Node,
                    wait.Occurrence);
            if (!identityValid)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.WaitShapeMismatch,
                    $"Wait '{wait.RegistrationId}' contradicts its exact token-step occurrence identity.",
                    Child(location, "registrationId"),
                    subject: wait.RegistrationId.Value);
            }

            if (!planNodes.TryGetValue(wait.Node, out var node))
            {
                return;
            }

            var kindMatches = (wait.Kind, node) switch
            {
                (ProcessWaitKind.AwaitMatch, AwaitMatchProcessNode) => true,
                (ProcessWaitKind.Timer, TimerProcessNode) => true,
                (ProcessWaitKind.DurableCut, DurableCutProcessNode) => true,
                (ProcessWaitKind.Request, RequestProcessNode or InvokeProcessProcessNode or ForEachPartitionProcessNode) => true,
                (ProcessWaitKind.PartitionBatch, ForEachPartitionProcessNode) => true,
                (ProcessWaitKind.RepeatAcrossActivation, RepeatAcrossActivationProcessNode) => true,
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
                (ProcessWaitKind.Request, InvokeProcessProcessNode child, { } winner, not null) =>
                    child.Outcomes.Any(outcome => outcome.Id == winner),
                (ProcessWaitKind.Request, ForEachPartitionProcessNode, null, not null) => true,
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
            foreach (var (emission, entry) in inputReceipts)
            {
                if (entry.Receipt.IsValidAdmissionEvidence())
                {
                    continue;
                }

                Error(
                    ProcessContinuationDiagnosticCodes.InputStateMismatch,
                    $"Input receipt '{emission.Value}' requires a closed semantic reason compatible with its policy disposition.",
                    Child(ItemLocation("/inputReceipts", entry.Index), "reason"),
                    subject: emission.Value,
                    expected: "defined compatible admission reason and disposition",
                    observed: $"{entry.Receipt.Reason}:{entry.Receipt.Disposition}");
            }

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
                    || !TryGetRequestNodeSemantics(node, out var requestContract)
                    || requestEntry.Request.Contract != requestContract)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.RequestStateMismatch,
                        $"Outstanding Request '{emission.Value}' does not match the exact compiled Request node.",
                        ItemLocation("/outstandingRequests", requestEntry.Index),
                        subject: emission.Value);
                }

                if (node is InvokeProcessProcessNode or ForEachPartitionProcessNode)
                {
                    var matchingChildren = children.Values.Count(candidate =>
                        candidate.Child.Disposition == ProcessChildDisposition.Active
                        && candidate.Child.Token == wait.Token
                        && candidate.Child.Node == wait.Node
                        && candidate.Child.RequestEmission == emission);
                    if (matchingChildren != 1)
                    {
                        Error(
                            ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                            $"Active child Request wait '{wait.RegistrationId}' must reverse-map to exactly one active child occurrence.",
                            location,
                            subject: wait.RegistrationId.Value,
                            expected: "1",
                            observed: matchingChildren.ToString(CultureInfo.InvariantCulture));
                    }
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

        void ValidateChildren()
        {
            foreach (var (child, childIndex) in children.Values)
            {
                var location = ItemLocation("/children", childIndex);
                if (!planNodes.TryGetValue(child.Node, out var node)
                    || !TryGetChildNodeSemantics(
                        node,
                        out var process,
                        out var contract,
                        out var purpose,
                        out var cancellation,
                        out var multiplicity))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                        $"Child registration '{child.RegistrationId}' does not refer to a child-bearing Process node.",
                        Child(location, "node"),
                        subject: child.RegistrationId);
                    continue;
                }

                var ownerFound = tokens.TryGetValue(child.Owner, out var owner);
                var identityInputsValid = ownerFound
                    && child.Occurrence >= 0
                    && child.Continuation is not null
                    && child.Process is not null
                    && !string.IsNullOrWhiteSpace(child.Token.Value)
                    && (multiplicity == ProcessChildRequestMultiplicity.Single
                        ? child.ProgressIdentity is null
                        : !string.IsNullOrWhiteSpace(child.ProgressIdentity));
                var expectedRegistration = identityInputsValid
                    ? ProcessReferenceIdentities.ChildRegistration(
                        state.Continuation,
                        child.Owner,
                        child.Node,
                        child.Occurrence,
                        child.ProgressIdentity)
                    : null;
                var expectedToken = identityInputsValid
                    && multiplicity == ProcessChildRequestMultiplicity.Partitioned
                    ? ProcessReferenceIdentities.PartitionToken(
                        state.Continuation,
                        child.Owner,
                        child.Node,
                        child.Occurrence,
                        child.ProgressIdentity!)
                    : child.Owner;
                var expectedContinuation = identityInputsValid
                    ? ProcessReferenceIdentities.ChildContinuation(
                        state.Continuation,
                        child.Owner,
                        child.Node,
                        child.Occurrence,
                        child.ProgressIdentity,
                        process)
                    : null;
                var expectedRequestWait = identityInputsValid
                    ? ProcessReferenceIdentities.WaitRegistration(
                        state.Continuation,
                        child.Token,
                        child.Node,
                        multiplicity == ProcessChildRequestMultiplicity.Single ? child.Occurrence : 0)
                    : default;
                var identityValid = identityInputsValid
                    && owner.Token.Step > child.Occurrence
                    && string.Equals(child.RegistrationId, expectedRegistration, StringComparison.Ordinal)
                    && child.Token == expectedToken
                    && child.Process == process
                    && child.Continuation == expectedContinuation
                    && child.Purpose == purpose
                    && child.Cancellation == cancellation;
                if (!identityValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                        $"Child registration '{child.RegistrationId}' contradicts its exact owner, node, definition, policy, or derived identity.",
                        location,
                        subject: child.RegistrationId);
                }

                if (!Enum.IsDefined(child.Disposition)
                    || child.Disposition == ProcessChildDisposition.Unspecified)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                        $"Child registration '{child.RegistrationId}' has no durable lifecycle disposition.",
                        Child(location, "disposition"),
                        subject: child.RegistrationId);
                    continue;
                }

                if (multiplicity == ProcessChildRequestMultiplicity.Single
                    && child.Disposition is (ProcessChildDisposition.Pending
                        or ProcessChildDisposition.CancelledBeforeStart))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                        $"Direct child registration '{child.RegistrationId}' cannot retain a pre-start lifecycle disposition because InvokeProcess starts atomically.",
                        Child(location, "disposition"),
                        subject: child.RegistrationId);
                }

                if (multiplicity == ProcessChildRequestMultiplicity.Single
                    && child.Disposition is (ProcessChildDisposition.CancellationRequested
                        or ProcessChildDisposition.Detached)
                    && (!ownerFound || !IsTerminal(owner.Token.Disposition)))
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                        $"Direct child registration '{child.RegistrationId}' cannot retain a cancellation disposition while its shared owner token remains live.",
                        Child(location, "token"),
                        subject: child.RegistrationId);
                }

                var requestCount = child.RequestEmission is { } emission
                    ? requests.Values.Count(candidate =>
                        candidate.Request.Emission == emission
                        && candidate.Request.Token == child.Token
                        && candidate.Request.Node == child.Node
                        && candidate.Request.Contract == contract)
                    : 0;
                var waitCount = child.RequestEmission is { } requestEmission
                    ? waits.Values.Count(candidate =>
                        candidate.Wait.Active
                        && candidate.Wait.Kind == ProcessWaitKind.Request
                        && candidate.Wait.Token == child.Token
                        && candidate.Wait.Node == child.Node
                        && candidate.Wait.RegistrationId == expectedRequestWait
                        && candidate.Wait.ObligationEmission == requestEmission)
                    : 0;
                var lifecycleShapeValid = child.Disposition switch
                {
                    ProcessChildDisposition.Pending or ProcessChildDisposition.CancelledBeforeStart =>
                        child.RequestEmission is null
                        && child.TerminalOutcome is null
                        && child.Result is null
                        && requestCount == 0
                        && waitCount == 0,
                    ProcessChildDisposition.Active =>
                        child.RequestEmission is not null
                        && child.TerminalOutcome is null
                        && child.Result is null
                        && requestCount == 1
                        && waitCount == 1
                        && tokens.TryGetValue(child.Token, out var activeToken)
                        && activeToken.Token.Disposition == ExecutionTokenDisposition.Waiting,
                    ProcessChildDisposition.Completed or ProcessChildDisposition.Failed =>
                        child.RequestEmission is not null
                        && child.TerminalOutcome is not null
                        && child.Result is not null
                        && requestCount == 0
                        && waitCount == 0,
                    ProcessChildDisposition.CancellationRequested or ProcessChildDisposition.Detached =>
                        child.RequestEmission is not null
                        && child.TerminalOutcome is null
                        && child.Result is null
                        && requestCount == 0
                        && waitCount == 0,
                    _ => false
                };
                if (!lifecycleShapeValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                        $"Child registration '{child.RegistrationId}' has Request or result evidence that contradicts its lifecycle disposition.",
                        location,
                        subject: child.RegistrationId);
                }

                if (child.Disposition is ProcessChildDisposition.Pending
                    or ProcessChildDisposition.CancelledBeforeStart)
                {
                    if (waits.Values.Any(candidate =>
                            candidate.Wait.Kind == ProcessWaitKind.Request
                            && candidate.Wait.Token == child.Token
                            && candidate.Wait.Node == child.Node))
                    {
                        Error(
                            ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                            $"Pre-start child registration '{child.RegistrationId}' cannot retain a Request wait.",
                            Child(location, "requestEmission"),
                            subject: child.RegistrationId);
                    }
                }
                else if (child.Disposition is ProcessChildDisposition.CancellationRequested
                    or ProcessChildDisposition.Detached)
                {
                    ValidateCancelledChildEvidence(child, childIndex, expectedRequestWait);
                }

                if (multiplicity == ProcessChildRequestMultiplicity.Partitioned)
                {
                    var memberFound = tokens.TryGetValue(child.Token, out var member);
                    var memberLifecycleValid = child.Disposition switch
                    {
                        ProcessChildDisposition.Pending or ProcessChildDisposition.CancelledBeforeStart =>
                            !memberFound,
                        ProcessChildDisposition.Active =>
                            memberFound
                            && member.Token.Disposition == ExecutionTokenDisposition.Waiting
                            && member.Token.Node == child.Node,
                        ProcessChildDisposition.Completed or ProcessChildDisposition.Failed =>
                            memberFound
                            && member.Token.Disposition == ExecutionTokenDisposition.Completed
                            && member.Token.Node == child.Node,
                        ProcessChildDisposition.CancellationRequested or ProcessChildDisposition.Detached =>
                            memberFound
                            && member.Token.Disposition == ExecutionTokenDisposition.Cancelled
                            && member.Token.Node == child.Node,
                        _ => false
                    };
                    if (!memberLifecycleValid)
                    {
                        Error(
                            ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                            $"Partition child '{child.RegistrationId}' contradicts its deterministic member-token lifecycle.",
                            Child(location, "token"),
                            subject: child.RegistrationId);
                    }
                }

                if (child.TerminalOutcome is { } outcomeId && child.Result is { } result)
                {
                    var requestDefinition = plan.ValidationContext.InteractionContracts is { } catalog
                        && catalog.TryResolve(contract, out var resolved)
                        ? resolved as RequestContractDefinition
                        : null;
                    var outcome = requestDefinition?.Response.Find(outcomeId);
                    var resultValid = outcome is not null
                        && result.Contract == outcome.Schema.Contract
                        && PortableExecutionValidator.Validate(result, plan.ValidationContext.ShapeGraph).IsValid
                        && (child.Disposition == ProcessChildDisposition.Completed)
                        == (outcome is RequestResultDefinition);
                    if (!resultValid)
                    {
                        Error(
                            ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                            $"Child registration '{child.RegistrationId}' terminal result violates its exact Request outcome contract or classification.",
                            Child(location, "result"),
                            subject: child.RegistrationId);
                    }

                    ValidateTerminalChildEvidence(
                        child,
                        childIndex,
                        node,
                        contract,
                        expectedRequestWait,
                        outcomeId,
                        result);
                }
            }

            foreach (var group in children.Values
                         .Where(static candidate => candidate.Child.RequestEmission is not null)
                         .GroupBy(static candidate => candidate.Child.RequestEmission!.Value))
            {
                if (group.Count() <= 1)
                {
                    continue;
                }

                var duplicate = group.OrderBy(static candidate => candidate.Index).Last();
                Error(
                    ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                    "A child Request emission can belong to exactly one child occurrence.",
                    ItemLocation("/children", duplicate.Index, "requestEmission"),
                    subject: duplicate.Child.RegistrationId,
                    expected: "1",
                    observed: group.Count().ToString(CultureInfo.InvariantCulture));
            }

            foreach (var group in children.Values
                         .Where(static candidate => candidate.Child.ProgressIdentity is null)
                         .GroupBy(static candidate => (
                             candidate.Child.Owner,
                             candidate.Child.Node,
                             candidate.Child.Occurrence)))
            {
                if (group.Count() <= 1)
                {
                    continue;
                }

                var duplicate = group.OrderBy(static candidate => candidate.Index).Last();
                Error(
                    ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                    "An InvokeProcess owner, node, and occurrence can retain exactly one direct child.",
                    ItemLocation("/children", duplicate.Index),
                    subject: duplicate.Child.RegistrationId,
                    expected: "1",
                    observed: group.Count().ToString(CultureInfo.InvariantCulture));
            }
        }

        void ValidateTerminalChildEvidence(
            ProcessChildState child,
            int childIndex,
            CanonicalProcessNode node,
            RequestContractReference contract,
            ProcessWaitRegistrationId expectedWait,
            RequestTerminalOutcomeId outcome,
            PortableValue result)
        {
            var location = ItemLocation("/children", childIndex);
            var childRequestWaits = waits.Values.Where(candidate =>
                candidate.Wait.Kind == ProcessWaitKind.Request
                && candidate.Wait.Token == child.Token
                && candidate.Wait.Node == child.Node
                && candidate.Wait.ObligationEmission == child.RequestEmission).ToArray();
            if (childRequestWaits is not [var waitEntry]
                || waitEntry.Wait.Active
                || waitEntry.Wait.RegistrationId != expectedWait)
            {
                Error(
                    ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                    $"Terminal child registration '{child.RegistrationId}' requires one exact inactive Request wait tombstone.",
                    Child(location, "requestEmission"),
                    subject: child.RegistrationId,
                    expected: "1",
                    observed: childRequestWaits.Length.ToString(CultureInfo.InvariantCulture));
                return;
            }

            var wait = waitEntry.Wait;
            var expectedWinner = ProcessRequestSemantics.TryProject(node, out var semantics)
                ? semantics.Outcomes.SingleOrDefault(candidate => candidate.Outcome == outcome)?.Id
                : null;
            var winnerShapeValid = wait.WinnerInput is not null
                && (node is ForEachPartitionProcessNode
                    ? wait.WinnerClause is null
                    : expectedWinner is not null && wait.WinnerClause == expectedWinner);
            var consumedReplies = inputReceipts.Values.Where(candidate =>
                candidate.Receipt.Disposition == ProcessInputAdmissionDisposition.Consumed
                && candidate.Receipt.Target.Continuation == state.Continuation
                && candidate.Receipt.Target.Token == child.Token
                && candidate.Receipt.Input.Envelope is ReplyEnvelope reply
                && reply.InReplyTo == child.RequestEmission).ToArray();
            var receiptValid = wait.WinnerInput is { } winner
                && consumedReplies is [var receiptEntry]
                && receiptEntry.Receipt.Emission == winner
                && receiptEntry.Receipt.WaitRegistrationId == wait.RegistrationId
                && receiptEntry.Receipt.Target.WaitRegistrationId == wait.RegistrationId
                && receiptEntry.Receipt.Input.Envelope is ReplyEnvelope reply
                && reply.Context.EmissionId == winner
                && reply.InReplyTo == child.RequestEmission
                && reply.Outcome.Id == outcome
                && reply.Outcome.Value == result
                && reply.Context.Origin is ProcessInteractionOrigin origin
                && origin.Definition == child.Process
                && origin.Continuation == child.Continuation
                && plan.ValidationContext.InteractionContracts is { } catalog
                && catalog.TryResolve(reply.Contract, out var resolvedReply)
                && resolvedReply is ReplyContractDefinition replyDefinition
                && replyDefinition.Request == contract
                && replyDefinition.Outcome == outcome;
            if (winnerShapeValid && receiptValid)
            {
                return;
            }

            Error(
                ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                $"Terminal child registration '{child.RegistrationId}' lacks its exact consumed Reply, winner, target, contract, outcome, value, or child origin evidence.",
                Child(location, "result"),
                subject: child.RegistrationId);
        }

        void ValidateCancelledChildEvidence(
            ProcessChildState child,
            int childIndex,
            ProcessWaitRegistrationId expectedWait)
        {
            var location = ItemLocation("/children", childIndex);
            var childRequestWaits = waits.Values.Where(candidate =>
                candidate.Wait.Kind == ProcessWaitKind.Request
                && candidate.Wait.Token == child.Token
                && candidate.Wait.Node == child.Node
                && candidate.Wait.ObligationEmission == child.RequestEmission).ToArray();
            var consumedReplies = inputReceipts.Values.Count(candidate =>
                candidate.Receipt.Disposition == ProcessInputAdmissionDisposition.Consumed
                && candidate.Receipt.Target.Continuation == state.Continuation
                && candidate.Receipt.Target.Token == child.Token
                && candidate.Receipt.Input.Envelope is ReplyEnvelope reply
                && reply.InReplyTo == child.RequestEmission);
            if (childRequestWaits is [var waitEntry]
                && !waitEntry.Wait.Active
                && waitEntry.Wait.RegistrationId == expectedWait
                && waitEntry.Wait.WinnerClause is null
                && waitEntry.Wait.WinnerInput is null
                && consumedReplies == 0)
            {
                return;
            }

            Error(
                ProcessContinuationDiagnosticCodes.ChildStateMismatch,
                $"Cancelled or detached child registration '{child.RegistrationId}' requires one exact unresolved Request tombstone and no consumed Reply.",
                Child(location, "requestEmission"),
                subject: child.RegistrationId,
                expected: "one inactive winnerless wait; zero consumed Replies",
                observed: $"waits={childRequestWaits.Length}; consumedReplies={consumedReplies}");
        }

        void ValidatePartitions()
        {
            foreach (var (partition, partitionIndex) in partitions.Values)
            {
                var location = ItemLocation("/partitions", partitionIndex);
                if (!planNodes.TryGetValue(partition.Node, out var planNode)
                    || planNode is not ForEachPartitionProcessNode node)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"Partition registration '{partition.RegistrationId}' does not refer to bounded partition work.",
                        Child(location, "node"),
                        subject: partition.RegistrationId);
                    continue;
                }

                var ownerFound = tokens.TryGetValue(partition.Owner, out var owner);
                var identityValid = ownerFound
                    && partition.Occurrence >= 0
                    && owner.Token.Step > partition.Occurrence
                    && string.Equals(
                        partition.RegistrationId,
                        ProcessReferenceIdentities.PartitionRegistration(
                            state.Continuation,
                            partition.Owner,
                            partition.Node,
                            partition.Occurrence),
                        StringComparison.Ordinal);
                if (!identityValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"Partition registration '{partition.RegistrationId}' contradicts its exact owner occurrence identity.",
                        location,
                        subject: partition.RegistrationId);
                }

                var matchingWaits = ownerFound
                    && partition.Occurrence >= 0
                    ? waits.Values.Where(candidate =>
                        candidate.Wait.Kind == ProcessWaitKind.PartitionBatch
                        && candidate.Wait.Token == partition.Owner
                        && candidate.Wait.Node == partition.Node
                        && candidate.Wait.Occurrence == partition.Occurrence
                        && candidate.Wait.RegistrationId == ProcessReferenceIdentities.WaitRegistration(
                            state.Continuation,
                            partition.Owner,
                            partition.Node,
                            partition.Occurrence)).ToArray()
                    : [];
                var waitShapeValid = matchingWaits is [var exactWait]
                    && exactWait.Wait.Active == !partition.Resolved;
                var ownerShapeValid = waitShapeValid
                    && (partition.Resolved
                        || ownerFound
                           && owner.Token.Disposition == ExecutionTokenDisposition.Waiting
                           && owner.Token.Node == partition.Node);
                if (!ownerShapeValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"Partition registration '{partition.RegistrationId}' contradicts its coordinator wait or resolved state.",
                        Child(location, "resolved"),
                        subject: partition.RegistrationId);
                }

                if (partition.Work.Length > node.Limits.MaximumItems)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"Partition registration '{partition.RegistrationId}' exceeds its compiled maximum item count.",
                        Child(location, "work"),
                        subject: partition.RegistrationId,
                        expected: $"<= {node.Limits.MaximumItems}",
                        observed: partition.Work.Length.ToString(CultureInfo.InvariantCulture));
                }

                string? previous = null;
                HashSet<string> progressIds = new(StringComparer.Ordinal);
                HashSet<string> childIds = new(StringComparer.Ordinal);
                for (var workIndex = 0; workIndex < partition.Work.Length; workIndex++)
                {
                    var work = partition.Work[workIndex];
                    var workLocation = ItemLocation(Child(location, "work"), workIndex);
                    if (work is null)
                    {
                        Error(
                            ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                            "Partition work cannot contain a null item.",
                            workLocation,
                            subject: partition.RegistrationId);
                        continue;
                    }

                    var workValid = !string.IsNullOrWhiteSpace(work.ProgressIdentity)
                        && progressIds.Add(work.ProgressIdentity)
                        && (previous is null
                            || StringComparer.Ordinal.Compare(previous, work.ProgressIdentity) < 0)
                        && !string.IsNullOrWhiteSpace(work.ChildRegistrationId)
                        && childIds.Add(work.ChildRegistrationId)
                        && work.Partition is not null
                        && work.Partition.Contract == node.Partition.Contract
                        && work.Partition.State is not (
                            PortableValueState.Missing
                            or PortableValueState.Unknown
                            or PortableValueState.Failed)
                        && PortableExecutionValidator.Validate(
                            work.Partition,
                            plan.ValidationContext.ShapeGraph).IsValid
                        && children.TryGetValue(work.ChildRegistrationId, out var child)
                        && child.Child.Owner == partition.Owner
                        && child.Child.Node == partition.Node
                        && child.Child.Occurrence == partition.Occurrence
                        && string.Equals(
                            child.Child.ProgressIdentity,
                            work.ProgressIdentity,
                            StringComparison.Ordinal);
                    if (!workValid)
                    {
                        Error(
                            ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                            $"Partition work '{work.ProgressIdentity}' is noncanonical or contradicts its typed value and exact child occurrence.",
                            workLocation,
                            subject: partition.RegistrationId);
                    }
                    previous = work.ProgressIdentity;
                }

                var unexpectedChildren = children.Values.Count(candidate =>
                    candidate.Child.Owner == partition.Owner
                    && candidate.Child.Node == partition.Node
                    && candidate.Child.Occurrence == partition.Occurrence
                    && !childIds.Contains(candidate.Child.RegistrationId));
                if (unexpectedChildren != 0)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"Partition registration '{partition.RegistrationId}' does not name every child in its exact occurrence.",
                        Child(location, "work"),
                        subject: partition.RegistrationId,
                        observed: unexpectedChildren.ToString(CultureInfo.InvariantCulture));
                }

                var occurrenceChildren = partition.Work
                    .Where(static work => work is not null)
                    .Select(work => children.TryGetValue(work.ChildRegistrationId, out var child)
                        ? child.Child
                        : null)
                    .Where(static child => child is not null)
                    .ToArray();
                var activeChildren = occurrenceChildren.Count(static child => child!.Disposition
                    == ProcessChildDisposition.Active);
                var resolutionShapeValid = activeChildren <= node.Limits.MaximumParallelism
                    && (partition.Resolved
                        ? occurrenceChildren.All(static child => child!.Disposition is not (
                            ProcessChildDisposition.Pending or ProcessChildDisposition.Active))
                        : occurrenceChildren.All(static child => child!.Disposition is not (
                            ProcessChildDisposition.Failed
                            or ProcessChildDisposition.CancellationRequested
                            or ProcessChildDisposition.Detached
                            or ProcessChildDisposition.CancelledBeforeStart)));
                if (!resolutionShapeValid)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"Partition registration '{partition.RegistrationId}' contradicts its parallelism bound or resolved child lifecycle.",
                        Child(location, "resolved"),
                        subject: partition.RegistrationId,
                        expected: $"active <= {node.Limits.MaximumParallelism}; coherent resolution");
                }
            }


            foreach (var group in partitions.Values
                         .Where(static candidate => !candidate.Partition.Resolved)
                         .GroupBy(static candidate => (
                             candidate.Partition.Owner,
                             candidate.Partition.Node)))
            {
                if (group.Count() <= 1)
                {
                    continue;
                }

                var duplicate = group.OrderBy(static candidate => candidate.Index).Last();
                Error(
                    ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                    "A coordinator token and bounded-work node can retain at most one unresolved partition occurrence.",
                    ItemLocation("/partitions", duplicate.Index),
                    subject: duplicate.Partition.RegistrationId,
                    expected: "1",
                    observed: group.Count().ToString(CultureInfo.InvariantCulture));
            }

            foreach (var (wait, waitIndex) in waits.Values)
            {
                if (wait.Kind != ProcessWaitKind.PartitionBatch)
                {
                    continue;
                }

                var matching = partitions.Values.Count(candidate =>
                    candidate.Partition.Owner == wait.Token
                    && candidate.Partition.Node == wait.Node
                    && !string.IsNullOrWhiteSpace(candidate.Partition.Owner.Value)
                    && !string.IsNullOrWhiteSpace(candidate.Partition.Node.Value)
                    && candidate.Partition.Occurrence >= 0
                    && candidate.Partition.Occurrence == wait.Occurrence
                    && wait.Active == !candidate.Partition.Resolved
                    && wait.RegistrationId == ProcessReferenceIdentities.WaitRegistration(
                        state.Continuation,
                        candidate.Partition.Owner,
                        candidate.Partition.Node,
                        candidate.Partition.Occurrence));
                if (matching != 1)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.PartitionStateMismatch,
                        $"PartitionBatch wait '{wait.RegistrationId}' requires one exact partition occurrence with the reciprocal lifecycle.",
                        ItemLocation("/waits", waitIndex),
                        subject: wait.RegistrationId.Value,
                        expected: "1",
                        observed: matching.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        void ValidateRecurrences()
        {
            foreach (var (recurrence, recurrenceIndex) in recurrences.Values)
            {
                var location = ItemLocation("/recurrences", recurrenceIndex);
                if (!planNodes.TryGetValue(recurrence.Node, out var planNode)
                    || planNode is not RepeatAcrossActivationProcessNode node)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch,
                        $"Recurrence registration '{recurrence.RegistrationId}' does not refer to explicit durable recurrence.",
                        Child(location, "node"),
                        subject: recurrence.RegistrationId);
                    continue;
                }

                var tokenFound = tokens.TryGetValue(recurrence.Token, out var token);
                var identityValid = tokenFound
                    && recurrence.Occurrence >= 0
                    && token.Token.Step > recurrence.Occurrence
                    && string.Equals(
                        recurrence.RegistrationId,
                        ProcessReferenceIdentities.RecurrenceRegistration(
                            state.Continuation,
                            recurrence.Token,
                            recurrence.Node,
                            recurrence.Occurrence),
                        StringComparison.Ordinal);
                var countsValid = recurrence.RepeatCount is >= 1
                    && recurrence.RepeatCount <= node.Policy.MaximumOccurrences
                    && recurrence.UnchangedProgressCount >= 0
                    && (recurrence.UnchangedProgressCount < recurrence.RepeatCount
                        || !recurrence.Active
                           && recurrence.UnchangedProgressCount == recurrence.RepeatCount
                           && (long)recurrence.UnchangedProgressCount
                               == (long)node.Policy.MaximumUnchangedProgressOccurrences + 1L)
                    && (long)recurrence.UnchangedProgressCount
                        <= (long)node.Policy.MaximumUnchangedProgressOccurrences + 1L
                    && (recurrence.Active
                        ? recurrence.UnchangedProgressCount <= node.Policy.MaximumUnchangedProgressOccurrences
                        : true);
                var progressValid = recurrence.LastProgress is { } progress
                    && progress.Contract == node.ProgressContract
                    && progress.State is not (
                        PortableValueState.Missing
                        or PortableValueState.Unknown
                        or PortableValueState.Failed)
                    && PortableExecutionValidator.Validate(progress, plan.ValidationContext.ShapeGraph).IsValid;
                var activeValid = !recurrence.Active
                    || tokenFound
                       && IsLive(token.Token.Disposition)
                       && token.Token.Step > 0;
                var initialWaitCount = tokenFound
                    && recurrence.Occurrence >= 0
                    ? waits.Values.Count(candidate =>
                        candidate.Wait.Kind == ProcessWaitKind.RepeatAcrossActivation
                        && candidate.Wait.Token == recurrence.Token
                        && candidate.Wait.Node == recurrence.Node
                        && candidate.Wait.RegistrationId == ProcessReferenceIdentities.WaitRegistration(
                            state.Continuation,
                            recurrence.Token,
                            recurrence.Node,
                            recurrence.Occurrence))
                    : 0;
                if (!identityValid
                    || !countsValid
                    || !progressValid
                    || !activeValid
                    || initialWaitCount != 1)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch,
                        $"Recurrence registration '{recurrence.RegistrationId}' contradicts its identity, progress, limits, token lifecycle, or initial wait tombstone.",
                        location,
                        subject: recurrence.RegistrationId);
                }
            }

            foreach (var (wait, waitIndex) in waits.Values)
            {
                if (wait.Kind != ProcessWaitKind.RepeatAcrossActivation)
                {
                    continue;
                }

                var matching = recurrences.Values.Count(candidate =>
                    candidate.Recurrence.Token == wait.Token
                    && candidate.Recurrence.Node == wait.Node
                    && wait.Occurrence >= candidate.Recurrence.Occurrence
                    && (!wait.Active
                        || candidate.Recurrence.Active
                           && tokens.TryGetValue(wait.Token, out var token)
                           && !string.IsNullOrWhiteSpace(wait.Node.Value)
                           && token.Token.Step > 0
                           && wait.RegistrationId == ProcessReferenceIdentities.WaitRegistration(
                               state.Continuation,
                               wait.Token,
                               wait.Node,
                               token.Token.Step - 1)));
                if (wait.Active ? matching != 1 : matching == 0)
                {
                    Error(
                        ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch,
                        wait.Active
                            ? $"Repeat wait '{wait.RegistrationId}' requires one exact active recurrence occurrence."
                            : $"Repeat wait tombstone '{wait.RegistrationId}' requires one retained recurrence occurrence.",
                        ItemLocation("/waits", waitIndex),
                        subject: wait.RegistrationId.Value,
                        expected: "1",
                        observed: matching.ToString(CultureInfo.InvariantCulture));
                }
            }

            foreach (var group in recurrences.Values.GroupBy(static candidate => (
                         candidate.Recurrence.Token,
                         candidate.Recurrence.Node)))
            {
                var expectedWaitCount = group.Sum(static candidate => (long)candidate.Recurrence.RepeatCount);
                var recurrenceWaits = waits.Values.Count(candidate =>
                    candidate.Wait.Kind == ProcessWaitKind.RepeatAcrossActivation
                    && candidate.Wait.Token == group.Key.Token
                    && candidate.Wait.Node == group.Key.Node);
                var expectedActiveCount = group.Count(candidate =>
                    candidate.Recurrence.Active
                    && tokens.TryGetValue(candidate.Recurrence.Token, out var token)
                    && token.Token.Disposition == ExecutionTokenDisposition.Waiting
                    && token.Token.Node == candidate.Recurrence.Node);
                var activeWaitCount = waits.Values.Count(candidate =>
                    candidate.Wait.Active
                    && candidate.Wait.Kind == ProcessWaitKind.RepeatAcrossActivation
                    && candidate.Wait.Token == group.Key.Token
                    && candidate.Wait.Node == group.Key.Node);
                if (recurrenceWaits == expectedWaitCount && activeWaitCount == expectedActiveCount)
                {
                    continue;
                }

                var first = group.OrderBy(static candidate => candidate.Index).First();
                Error(
                    ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch,
                    "Retained recurrence state requires exactly one wait tombstone per committed repeat and one active recurrence wait while its token is parked at the recurrence node.",
                    ItemLocation("/recurrences", first.Index),
                    subject: first.Recurrence.RegistrationId,
                    expected: $"waits={expectedWaitCount}; active={expectedActiveCount}",
                    observed: $"waits={recurrenceWaits}; active={activeWaitCount}");
            }


            foreach (var group in recurrences.Values
                         .GroupBy(static candidate => (
                             candidate.Recurrence.Token,
                             candidate.Recurrence.Node)))
            {
                if (group.Count() <= 1)
                {
                    continue;
                }

                var duplicate = group.OrderBy(static candidate => candidate.Index).Last();
                Error(
                    ProcessContinuationDiagnosticCodes.RecurrenceStateMismatch,
                    "A token and recurrence node can retain at most one recurrence registration.",
                    ItemLocation("/recurrences", duplicate.Index),
                    subject: duplicate.Recurrence.RegistrationId,
                    expected: "1",
                    observed: group.Count().ToString(CultureInfo.InvariantCulture));
            }
        }

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
            var liveChildren = state.Children.Count(static child => child is not null
                && child.Disposition is ProcessChildDisposition.Pending or ProcessChildDisposition.Active);
            var livePartitions = state.Partitions.Count(static partition => partition is { Resolved: false });
            var liveRecurrences = state.Recurrences.Count(static recurrence => recurrence is { Active: true });
            if (state.BufferedInputs.IsDefaultOrEmpty
                && liveTokens == 0
                && activeWaits == 0
                && liveForks == 0
                && liveChildren == 0
                && livePartitions == 0
                && liveRecurrences == 0
                && state.OutstandingRequests.IsDefaultOrEmpty)
            {
                return;
            }

            Error(
                ProcessContinuationDiagnosticCodes.TerminalStateInvalid,
                "A terminal Process continuation cannot retain live tokens, waits, Requests, Fork, child, partition, recurrence work, or buffered input.",
                state.BufferedInputs.IsDefaultOrEmpty ? "/terminal" : "/bufferedInputs",
                subject: state.Continuation?.ProcessInstanceId.Value,
                expected: "no live work",
                observed: $"tokens={liveTokens}; waits={activeWaits}; forks={liveForks}; children={liveChildren}; partitions={livePartitions}; recurrences={liveRecurrences}; requests={state.OutstandingRequests.Length}; buffered={state.BufferedInputs.Length}");
        }

        static bool TryGetRequestNodeSemantics(
            CanonicalProcessNode node,
            out RequestContractReference contract) =>
            ProcessRequestSemantics.TryGetContract(node, out contract);

        static bool TryGetChildNodeSemantics(
            CanonicalProcessNode node,
            out ExecutionDefinitionReference process,
            out RequestContractReference contract,
            out ProcessChildPurpose purpose,
            out ProcessChildCancellationPolicy cancellation,
            out ProcessChildRequestMultiplicity multiplicity)
        {
            if (ProcessRequestSemantics.TryProjectChild(node, out var child))
            {
                process = child.Process;
                contract = child.Contract;
                purpose = child.Purpose;
                cancellation = child.Cancellation;
                multiplicity = child.Multiplicity;
                return true;
            }

            process = null!;
            contract = null!;
            purpose = ProcessChildPurpose.Unspecified;
            cancellation = ProcessChildCancellationPolicy.Unspecified;
            multiplicity = default;
            return false;
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
                    case InvokeProcessProcessNode child:
                        foreach (var outcome in child.Outcomes)
                        {
                            Add(contracts, outcome.Continuation.Output);
                        }
                        break;
                    case ForEachPartitionProcessNode partition:
                        Add(contracts, partition.Partition);
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

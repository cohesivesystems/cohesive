using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Stable diagnostics emitted by Transition reference interpretation.</summary>
public static class TransitionExecutionDiagnosticCodes
{
    /// <summary>The activation input or observation does not satisfy its compiled semantic contract.</summary>
    public const string ActivationInvalid = "transitions.execution.activation.invalid";

    /// <summary>A required finite observation was not supplied.</summary>
    public const string ObservationUnavailable = "transitions.execution.observation.unavailable";

    /// <summary>A required finite observation is explicitly indeterminate.</summary>
    public const string ObservationUnknown = "transitions.execution.observation.unknown";

    /// <summary>A pure expression could not be evaluated from the supplied evidence.</summary>
    public const string ExpressionEvaluationFailed = "transitions.execution.expression.failed";

    /// <summary>A dynamically produced value violates its statically compiled contract.</summary>
    public const string ResultContractViolated = "transitions.execution.result.contractViolated";

    /// <summary>An accepted candidate state violates an authored invariant.</summary>
    public const string InvariantViolated = "transitions.execution.invariant.violated";

    /// <summary>An authored NoChange path changed candidate aggregate state.</summary>
    public const string NoChangeModifiedState = "transitions.execution.noChange.modifiedState";

    /// <summary>A linked Machine edge failed to establish its target configuration.</summary>
    public const string MachineTargetViolated = "transitions.execution.machine.targetViolated";

    /// <summary>Compiled Machine-derived evidence required by a MoveMachine node is unavailable.</summary>
    public const string MachineLinkUnavailable = "transitions.execution.machine.linkUnavailable";

    /// <summary>Compiled control flow reached no terminal outcome.</summary>
    public const string OutcomeUnavailable = "transitions.execution.outcome.unavailable";

    /// <summary>Fresh commit-time evidence omitted an actual observation read.</summary>
    public const string CommitObservationUnavailable = "transitions.execution.commitObservation.unavailable";

    /// <summary>Fresh commit-time evidence explicitly reported an actual observation read as indeterminate.</summary>
    public const string CommitObservationUnknown = "transitions.execution.commitObservation.unknown";
}

/// <summary>
/// Deterministic, non-committing reference interpretation of a compiled canonical Transition plan.
/// </summary>
/// <remarks>
/// Full-state and sparse evaluation are input adapters over one execution core. The interpreter performs no I/O,
/// invokes no services or delegates, and mutates no caller-owned state. Its sparse patch, emission intents,
/// guarantee demands, conflicts, and evidence are returned as data for an external commit interpretation.
/// </remarks>
public static class TransitionReferenceInterpreter
{
    /// <summary>Decides one direct Transition activation from explicit finite evidence.</summary>
    /// <param name="plan">Successfully compiled exact Transition plan.</param>
    /// <param name="activation">Typed input plus full or sparse aggregate observation.</param>
    /// <returns>A complete deterministic, non-committing Transition decision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="activation"/> is <see langword="null"/>.
    /// </exception>
    public static TransitionDecision Decide(
        CompiledTransitionPlan plan,
        TransitionActivation activation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(activation);
        return new Engine(plan, activation).Run();
    }

    /// <summary>Decides an activation from one complete coherent aggregate state.</summary>
    /// <param name="plan">Successfully compiled exact Transition plan.</param>
    /// <param name="activationId">Caller-supplied stable activation identity.</param>
    /// <param name="input">Typed invocation input.</param>
    /// <param name="state">Concrete complete aggregate state.</param>
    /// <param name="commitState">Optional fresh complete state used only for commit-coherence validation.</param>
    /// <returns>A complete deterministic, non-committing Transition decision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="input"/>, or <paramref name="state"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or full-state frame is invalid.</exception>
    public static TransitionDecision DecideFullState(
        CompiledTransitionPlan plan,
        ActivationId activationId,
        PortableValue input,
        PortableValue state,
        PortableValue? commitState = null) => Decide(
        plan,
        new(
            activationId,
            input,
            TransitionObservationFrame.Full(state),
            commitState is null ? null : TransitionObservationFrame.Full(commitState)));

    /// <summary>Decides an activation from explicitly supplied sparse aggregate observations.</summary>
    /// <param name="plan">Successfully compiled exact Transition plan.</param>
    /// <param name="activationId">Caller-supplied stable activation identity.</param>
    /// <param name="input">Typed invocation input.</param>
    /// <param name="observations">Exact finite observation entries.</param>
    /// <param name="commitObservations">Optional fresh entries used only for commit-coherence validation.</param>
    /// <returns>A complete deterministic, non-committing Transition decision.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="input"/>, or <paramref name="observations"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or sparse frame is invalid.</exception>
    public static TransitionDecision DecideSparse(
        CompiledTransitionPlan plan,
        ActivationId activationId,
        PortableValue input,
        IEnumerable<TransitionObservationEntry> observations,
        IEnumerable<TransitionObservationEntry>? commitObservations = null) => Decide(
        plan,
        new(
            activationId,
            input,
            TransitionObservationFrame.Sparse(observations),
            commitObservations is null ? null : TransitionObservationFrame.Sparse(commitObservations)));

    sealed class Engine
    {
        readonly CompiledTransitionPlan plan;
        readonly TransitionActivation activation;
        readonly PortableExpressionReferenceEvaluator evaluator = new(
            TransitionExpressionLanguage.Capabilities,
            interpreterName: "Transition reference interpreter");
        readonly Dictionary<ValueBindingId, PortableExpressionValue> bindings = [];
        readonly Dictionary<FieldPath, PortableExpressionValue> candidate = [];
        readonly Dictionary<TransitionObservationAccess, PortableValue> observedReads = [];
        readonly List<TransitionObservationAccess> observedReadOrder = [];
        readonly List<TransitionTraceEvent> trace = [];
        readonly List<TransitionExecutedPatch> executedPatches = [];
        readonly List<TransitionEmissionIntent> emissions = [];
        readonly List<TransitionMachineMovement> movements = [];
        readonly Dictionary<FieldPath, ValueContract> observationContracts = [];
        readonly Dictionary<string, ValueContract> inputContracts = new(StringComparer.Ordinal);

        public Engine(CompiledTransitionPlan plan, TransitionActivation activation)
        {
            this.plan = plan;
            this.activation = activation;
        }

        public TransitionDecision Run()
        {
            try
            {
                var activationFailure = ValidateActivation();
                if (activationFailure is not null)
                    return Failure(TransitionDecisionKind.InfrastructureFailure, activationFailure);

                bindings[TransitionBindingIds.Input] = PortableExpressionValue.FromPortable(activation.Input);

                foreach (var precondition in plan.Definition.Preconditions)
                {
                    var admitted = EvaluateBoolean(
                        precondition.Predicate,
                        precondition.Id,
                        candidateState: false,
                        operation: "Transition admission predicate");
                    AddTrace(
                        TransitionTraceEventKind.AdmissionEvaluated,
                        precondition.Id,
                        detail: admitted ? "true" : "false");
                    if (!admitted)
                    {
                        var rejection = EvaluateTyped(
                            precondition.Rejection,
                            plan.Definition.Outcome,
                            precondition.Id,
                            candidateState: false);
                        AddTrace(
                            TransitionTraceEventKind.OutcomeReturned,
                            precondition.Id,
                            detail: TransitionDecisionKind.AdmissionRejected.ToString());
                        return Complete(
                            TransitionDecisionKind.AdmissionRejected,
                            rejection,
                            retainedPatches: [],
                            retainedEmissions: [],
                            retainedMovements: []);
                    }
                }

                var terminal = ExecuteSequence(plan.Definition.Body, bindings);
                if (terminal is null)
                {
                    throw Invalid(
                        TransitionExecutionDiagnosticCodes.OutcomeUnavailable,
                        "Compiled Transition control flow completed without a terminal outcome.",
                        plan.Definition.Body.Id);
                }

                if (terminal.Kind == TransitionDecisionKind.AdmissionRejected)
                {
                    return Complete(
                        terminal.Kind,
                        terminal.Outcome,
                        retainedPatches: [],
                        retainedEmissions: [],
                        retainedMovements: []);
                }

                if (terminal.Kind == TransitionDecisionKind.DomainRejected)
                {
                    return Complete(
                        terminal.Kind,
                        terminal.Outcome,
                        retainedPatches: [],
                        retainedEmissions: [.. emissions],
                        retainedMovements: []);
                }

                RecomputeAffectedDerivedFields();
                ValidateInvariants();

                var changed = executedPatches.Where(static patch => patch.Changed).ToImmutableArray();
                var kind = terminal.Kind;
                if (kind == TransitionDecisionKind.NoChange && !changed.IsDefaultOrEmpty)
                {
                    throw Invalid(
                        TransitionExecutionDiagnosticCodes.NoChangeModifiedState,
                        "An authored NoChange outcome changed candidate aggregate state.",
                        terminal.Node);
                }
                if (kind == TransitionDecisionKind.Applied
                    && changed.IsDefaultOrEmpty
                    && movements.Count == 0)
                    kind = TransitionDecisionKind.NoChange;

                return Complete(
                    kind,
                    terminal.Outcome,
                    retainedPatches: changed,
                    retainedEmissions: [.. emissions],
                    retainedMovements: [.. movements]);
            }
            catch (TransitionRuntimeDecisionException exception)
            {
                return Failure(exception.Kind, exception.Diagnostic);
            }
            catch (PortableExpressionEvaluationException exception)
            {
                if (exception.SourceDiagnostic is not null)
                    return Failure(TransitionDecisionKind.InfrastructureFailure, exception.SourceDiagnostic);
                var code = exception.Error == PortableExpressionEvaluationError.RuntimeInputUnavailable
                    ? exception.ValueState == PortableValueState.Unknown
                        ? TransitionExecutionDiagnosticCodes.ObservationUnknown
                        : TransitionExecutionDiagnosticCodes.ObservationUnavailable
                    : TransitionExecutionDiagnosticCodes.ExpressionEvaluationFailed;
                return Failure(
                    TransitionDecisionKind.InfrastructureFailure,
                    Diagnostic(code, exception.Message, node: null, stage: "referenceInterpretation"));
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               or OverflowException
                                               or DivideByZeroException)
            {
                return Failure(
                    TransitionDecisionKind.InfrastructureFailure,
                    Diagnostic(
                        TransitionExecutionDiagnosticCodes.ExpressionEvaluationFailed,
                        exception.Message,
                        node: null,
                        stage: "referenceInterpretation"));
            }
        }

        Terminal? ExecuteSequence(
            SequenceTransitionNode sequence,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            foreach (var node in sequence.Steps)
            {
                var terminal = ExecuteNode(node, scope);
                if (terminal is not null)
                    return terminal;
            }
            return null;
        }

        Terminal? ExecuteNode(
            TransitionNode node,
            Dictionary<ValueBindingId, PortableExpressionValue> scope) => node switch
            {
                SequenceTransitionNode sequence => ExecuteSequence(sequence, new(scope)),
                LetTransitionNode let => ExecuteLet(let, scope),
                ChoiceTransitionNode choice => ExecuteChoice(choice, scope),
                MatchTransitionNode match => ExecuteMatch(match, scope),
                UpdateTransitionNode update => ExecuteUpdate(update, scope),
                EmitTransitionNode emit => ExecuteEmit(emit, scope),
                MoveMachineTransitionNode movement => ExecuteMachineMovement(movement, scope),
                OutcomeTransitionNode outcome => ExecuteOutcome(outcome, scope),
                _ => throw Invalid(
                    TransitionExecutionDiagnosticCodes.ExpressionEvaluationFailed,
                    $"Transition node '{node.GetType().Name}' is unsupported.",
                    node.Id)
            };

        Terminal? ExecuteLet(
            LetTransitionNode let,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var value = Evaluate(let.Value, let.Id, candidateState: false, scope);
            _ = ValidateTyped(value, let.Contract, let.Id);
            scope.Add(let.Binding, value);
            AddTrace(TransitionTraceEventKind.BindingCreated, let.Id, detail: let.Binding.Value);
            return null;
        }

        Terminal? ExecuteChoice(
            ChoiceTransitionNode choice,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            foreach (var choiceCase in choice.Cases)
            {
                if (!EvaluateBoolean(
                        choiceCase.Predicate,
                        choiceCase.Id,
                        candidateState: false,
                        operation: "Choice predicate",
                        scope))
                {
                    continue;
                }

                AddTrace(
                    TransitionTraceEventKind.CaseSelected,
                    choice.Id,
                    selectedCase: choiceCase.Id);
                return ExecuteSequence(choiceCase.Body, new(scope));
            }

            if (choice.Fallback is not null)
            {
                AddTrace(
                    TransitionTraceEventKind.CaseSelected,
                    choice.Id,
                    selectedCase: choice.Fallback.Id);
                return ExecuteSequence(choice.Fallback.Body, new(scope));
            }

            throw Invalid(
                TransitionExecutionDiagnosticCodes.OutcomeUnavailable,
                $"Choice '{choice.Id.Value}' selected no case and has no fallback.",
                choice.Id);
        }

        Terminal? ExecuteMatch(
            MatchTransitionNode match,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var value = Evaluate(match.Value, match.Id, candidateState: false, scope);
            _ = ValidateTyped(value, match.Contract, match.Id);
            foreach (var matchCase in match.Cases)
            {
                if (!RuntimeEquals(value, PortableExpressionValue.FromPortable(matchCase.Pattern)))
                    continue;

                AddTrace(
                    TransitionTraceEventKind.CaseSelected,
                    match.Id,
                    selectedCase: matchCase.Id);
                return ExecuteSequence(matchCase.Body, new(scope));
            }

            if (match.Fallback is not null)
            {
                AddTrace(
                    TransitionTraceEventKind.CaseSelected,
                    match.Id,
                    selectedCase: match.Fallback.Id);
                return ExecuteSequence(match.Fallback.Body, new(scope));
            }

            throw Invalid(
                TransitionExecutionDiagnosticCodes.OutcomeUnavailable,
                $"Match '{match.Id.Value}' selected no case and has no fallback.",
                match.Id);
        }

        Terminal? ExecuteUpdate(
            UpdateTransitionNode update,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var contract = ResolveObservationContract(update.Path);
            var before = ReadCandidate(update.Path, update.Id);
            RequireKnownPriorValue(before, update.Path, update.Id);
            var executed = update.Operation switch
            {
                SetTransitionPatch set => ExecuteSet(update, set, contract, before, scope),
                RemoveTransitionPatch => ExecuteRemove(update, contract, before),
                IncrementTransitionPatch increment => ExecuteIncrement(update, increment, contract, before, scope),
                AddToSetTransitionPatch add => ExecuteAddToSet(update, add, contract, before, scope),
                AppendTransitionPatch append => ExecuteAppend(update, append, contract, before, scope),
                UpsertOwnedChildTransitionPatch upsert => ExecuteOwnedChildUpsert(
                    update,
                    upsert,
                    contract,
                    before,
                    scope),
                RemoveOwnedChildTransitionPatch remove => ExecuteOwnedChildRemoval(
                    update,
                    remove,
                    contract,
                    before,
                    scope),
                _ => throw Invalid(
                    TransitionExecutionDiagnosticCodes.ExpressionEvaluationFailed,
                    $"Patch operation '{update.Operation.GetType().Name}' is unsupported.",
                    update.Id)
            };
            WriteCandidate(executed.Path, PortableExpressionValue.FromPortable(executed.After));
            executedPatches.Add(executed);
            TracePatch(executed, TransitionTraceEventKind.PatchExecuted);
            return null;
        }

        TransitionExecutedPatch ExecuteSet(
            UpdateTransitionNode update,
            SetTransitionPatch operation,
            ValueContract contract,
            PortableExpressionValue before,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var after = EvaluateTyped(operation.Value, contract, update.Id, candidateState: false, scope);
            return new(
                update.Id,
                update.Path,
                new EvaluatedSetTransitionPatch(after),
                before.ToPortable(contract),
                after);
        }

        static TransitionExecutedPatch ExecuteRemove(
            UpdateTransitionNode update,
            ValueContract contract,
            PortableExpressionValue before)
        {
            var after = PortableValue.Absent(contract);
            return new(
                update.Id,
                update.Path,
                new EvaluatedRemoveTransitionPatch(),
                before.ToPortable(contract),
                after);
        }

        TransitionExecutedPatch ExecuteIncrement(
            UpdateTransitionNode update,
            IncrementTransitionPatch operation,
            ValueContract contract,
            PortableExpressionValue before,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var amount = EvaluateTyped(operation.Amount, contract, update.Id, candidateState: false, scope);
            ObservationValue result;
            try
            {
                result = ObservationValueSemantics.Add(
                    before.RequireConcrete("Increment patch target"),
                    PortableExpressionValue.FromPortable(amount).RequireConcrete("Increment patch amount"));
            }
            catch (Exception exception) when (exception is InvalidOperationException or OverflowException)
            {
                throw Infrastructure(
                    TransitionExecutionDiagnosticCodes.ExpressionEvaluationFailed,
                    $"Increment patch '{update.Id.Value}' failed: {exception.Message}",
                    update.Id);
            }
            var after = ValidateTyped(PortableExpressionValue.Concrete(result), contract, update.Id);
            return new(
                update.Id,
                update.Path,
                new EvaluatedIncrementTransitionPatch(amount),
                before.ToPortable(contract),
                after);
        }

        TransitionExecutedPatch ExecuteAddToSet(
            UpdateTransitionNode update,
            AddToSetTransitionPatch operation,
            ValueContract contract,
            PortableExpressionValue before,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var elementContract = ElementContract(contract);
            var value = EvaluateTyped(operation.Value, elementContract, update.Id, candidateState: false, scope);
            var source = RequireArray(before, "AddToSet patch target");
            var candidateValue = PortableExpressionValue.FromPortable(value).RequireObservation("AddToSet patch value");
            var changed = !source.Any(item => ObservationValueSemantics.Equals(item, candidateValue));
            var result = changed ? Append(source, candidateValue) : source.ToArray();
            var after = ValidateTyped(
                PortableExpressionValue.Concrete(ObservationValue.FromArray(result)),
                contract,
                update.Id);
            return new(
                update.Id,
                update.Path,
                new EvaluatedAddToSetTransitionPatch(value),
                before.ToPortable(contract),
                after);
        }

        TransitionExecutedPatch ExecuteAppend(
            UpdateTransitionNode update,
            AppendTransitionPatch operation,
            ValueContract contract,
            PortableExpressionValue before,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var elementContract = ElementContract(contract);
            var value = EvaluateTyped(operation.Value, elementContract, update.Id, candidateState: false, scope);
            var result = Append(
                RequireArray(before, "Append patch target"),
                PortableExpressionValue.FromPortable(value).RequireObservation("Append patch value"));
            var after = ValidateTyped(
                PortableExpressionValue.Concrete(ObservationValue.FromArray(result)),
                contract,
                update.Id);
            return new(
                update.Id,
                update.Path,
                new EvaluatedAppendTransitionPatch(value),
                before.ToPortable(contract),
                after);
        }

        TransitionExecutedPatch ExecuteOwnedChildUpsert(
            UpdateTransitionNode update,
            UpsertOwnedChildTransitionPatch operation,
            ValueContract contract,
            PortableExpressionValue before,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var elementContract = ElementContract(contract);
            var identityContract = ResolveRelativeContract(elementContract, operation.IdentityPath);
            var identity = EvaluateTyped(
                operation.Identity,
                identityContract,
                update.Id,
                candidateState: false,
                scope);
            var value = EvaluateTyped(
                operation.Value,
                elementContract,
                update.Id,
                candidateState: false,
                scope);
            var source = RequireArray(before, "owned-child upsert target");
            var identityValue = PortableExpressionValue.FromPortable(identity);
            var replacementValue = PortableExpressionValue.FromPortable(value);
            var replacementIdentity = replacementValue.Project(operation.IdentityPath);
            if (!RuntimeEquals(replacementIdentity, identityValue))
            {
                throw Invalid(
                    TransitionExecutionDiagnosticCodes.ResultContractViolated,
                    $"Owned-child replacement identity at '{operation.IdentityPath}' does not equal "
                    + $"the selected identity for '{update.Path}'.",
                    update.Id);
            }
            var replacement = replacementValue.RequireObservation("owned-child upsert value");
            var result = source.ToArray();
            var found = false;
            for (var index = 0; index < result.Length; index++)
            {
                var childIdentity = PortableExpressionValue.FromObservation(result[index]).Project(operation.IdentityPath);
                if (!RuntimeEquals(childIdentity, identityValue))
                    continue;
                if (found)
                {
                    throw Invalid(
                        TransitionExecutionDiagnosticCodes.ResultContractViolated,
                        $"Owned-child identity '{identity}' occurs more than once at '{update.Path}'.",
                        update.Id);
                }
                result[index] = replacement;
                found = true;
            }
            if (!found)
                result = Append(result, replacement);
            var after = ValidateTyped(
                PortableExpressionValue.Concrete(ObservationValue.FromArray(result)),
                contract,
                update.Id);
            return new(
                update.Id,
                update.Path,
                new EvaluatedUpsertOwnedChildTransitionPatch(operation.IdentityPath, identity, value),
                before.ToPortable(contract),
                after);
        }

        TransitionExecutedPatch ExecuteOwnedChildRemoval(
            UpdateTransitionNode update,
            RemoveOwnedChildTransitionPatch operation,
            ValueContract contract,
            PortableExpressionValue before,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var elementContract = ElementContract(contract);
            var identityContract = ResolveRelativeContract(elementContract, operation.IdentityPath);
            var identity = EvaluateTyped(
                operation.Identity,
                identityContract,
                update.Id,
                candidateState: false,
                scope);
            var source = RequireArray(before, "owned-child removal target");
            var identityValue = PortableExpressionValue.FromPortable(identity);
            List<ObservationValue> result = new(source.Count);
            var found = false;
            foreach (var child in source)
            {
                if (RuntimeEquals(
                        PortableExpressionValue.FromObservation(child).Project(operation.IdentityPath),
                        identityValue))
                {
                    if (found)
                    {
                        throw Invalid(
                            TransitionExecutionDiagnosticCodes.ResultContractViolated,
                            $"Owned-child identity '{identity}' occurs more than once at '{update.Path}'.",
                            update.Id);
                    }
                    found = true;
                    continue;
                }
                result.Add(child);
            }
            var after = ValidateTyped(
                PortableExpressionValue.Concrete(ObservationValue.FromArray([.. result])),
                contract,
                update.Id);
            return new(
                update.Id,
                update.Path,
                new EvaluatedRemoveOwnedChildTransitionPatch(operation.IdentityPath, identity),
                before.ToPortable(contract),
                after);
        }

        Terminal? ExecuteEmit(
            EmitTransitionNode emit,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var contract = FindExpressionContract(
                emit.Id,
                TransitionExpressionSiteKind.EmissionPayload) ?? new ValueContract();
            var payload = EvaluateTyped(emit.Payload, contract, emit.Id, candidateState: false, scope);
            emissions.Add(new(emit.Id, emit.Contract, payload));
            AddTrace(
                TransitionTraceEventKind.EmissionProduced,
                emit.Id,
                contract: emit.Contract);
            return null;
        }

        Terminal? ExecuteMachineMovement(
            MoveMachineTransitionNode movement,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var link = plan.MachineEdges.FirstOrDefault(candidateLink =>
                candidateLink.Machine == movement.Machine && candidateLink.Edge == movement.Edge)
                ?? throw Invalid(
                    TransitionExecutionDiagnosticCodes.MachineLinkUnavailable,
                    $"Compiled Machine edge '{movement.Edge.Value}' is unavailable.",
                    movement.Id);

            if (!EvaluateBoolean(
                    link.SourceConfiguration,
                    movement.Id,
                    candidateState: false,
                    operation: "Machine source configuration",
                    scope))
            {
                var rejection = EvaluateTyped(
                    movement.Rejection,
                    plan.Definition.Outcome,
                    movement.Id,
                    candidateState: false,
                    scope);
                AddTrace(
                    TransitionTraceEventKind.OutcomeReturned,
                    movement.Id,
                    detail: TransitionDecisionKind.AdmissionRejected.ToString());
                return new(TransitionDecisionKind.AdmissionRejected, rejection, movement.Id);
            }

            ImmutableArray<TransitionExecutedPatch>.Builder assignments =
                ImmutableArray.CreateBuilder<TransitionExecutedPatch>(link.Assignments.Length);
            foreach (var assignment in link.Assignments)
            {
                var before = ReadCandidate(assignment.Path, movement.Id);
                RequireKnownPriorValue(before, assignment.Path, movement.Id);
                var executed = new TransitionExecutedPatch(
                    movement.Id,
                    assignment.Path,
                    new EvaluatedSetTransitionPatch(assignment.Value),
                    before.ToPortable(assignment.Value.Contract),
                    assignment.Value);
                WriteCandidate(assignment.Path, PortableExpressionValue.FromPortable(assignment.Value));
                executedPatches.Add(executed);
                assignments.Add(executed);
                TracePatch(executed, TransitionTraceEventKind.PatchExecuted);
            }

            if (!EvaluateBoolean(
                    link.TargetConfiguration,
                    movement.Id,
                    candidateState: true,
                    operation: "Machine target configuration",
                    scope))
            {
                throw Invalid(
                    TransitionExecutionDiagnosticCodes.MachineTargetViolated,
                    $"Machine edge '{movement.Edge.Value}' did not establish its declared target configuration.",
                    movement.Id);
            }

            movements.Add(new(
                movement.Id,
                movement.Machine,
                movement.Edge,
                assignments.MoveToImmutable()));
            AddTrace(
                TransitionTraceEventKind.MachineMoved,
                movement.Id,
                contract: movement.Machine,
                edge: movement.Edge);
            return null;
        }

        Terminal ExecuteOutcome(
            OutcomeTransitionNode outcome,
            Dictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var value = EvaluateTyped(
                outcome.Value,
                plan.Definition.Outcome,
                outcome.Id,
                candidateState: false,
                scope);
            var kind = outcome.Disposition switch
            {
                TransitionOutcomeDisposition.Applied => TransitionDecisionKind.Applied,
                TransitionOutcomeDisposition.NoChange => TransitionDecisionKind.NoChange,
                TransitionOutcomeDisposition.DomainRejected => TransitionDecisionKind.DomainRejected,
                _ => throw Invalid(
                    TransitionExecutionDiagnosticCodes.OutcomeUnavailable,
                    $"Outcome '{outcome.Id.Value}' has no supported disposition.",
                    outcome.Id)
            };
            AddTrace(TransitionTraceEventKind.OutcomeReturned, outcome.Id, detail: kind.ToString());
            return new(kind, value, outcome.Id);
        }

        void RecomputeAffectedDerivedFields()
        {
            HashSet<FieldPath> changed = [.. executedPatches.Where(static patch => patch.Changed).Select(static patch => patch.Path)];
            foreach (var derived in plan.DerivedFields)
            {
                if (!derived.DirectDependencies.Any(dependency => changed.Any(path => path.Overlaps(dependency))))
                    continue;

                var before = ReadCandidate(derived.Path, derived.Node);
                RequireKnownPriorValue(before, derived.Path, derived.Node);
                var after = EvaluateTyped(
                    derived.Expression,
                    derived.Contract,
                    derived.Node,
                    candidateState: true,
                    bindings);
                var executed = new TransitionExecutedPatch(
                    derived.Node,
                    derived.Path,
                    new EvaluatedSetTransitionPatch(after),
                    before.ToPortable(derived.Contract),
                    after);
                WriteCandidate(derived.Path, PortableExpressionValue.FromPortable(after));
                executedPatches.Add(executed);
                if (executed.Changed)
                    changed.Add(executed.Path);
                TracePatch(executed, TransitionTraceEventKind.DerivedFieldRecomputed);
            }
        }

        void ValidateInvariants()
        {
            foreach (var invariant in plan.Definition.Invariants)
            {
                var holds = EvaluateBoolean(
                    invariant.Predicate,
                    invariant.Id,
                    candidateState: true,
                    operation: "Transition invariant",
                    bindings);
                AddTrace(
                    TransitionTraceEventKind.InvariantEvaluated,
                    invariant.Id,
                    detail: holds ? "true" : "false");
                if (!holds)
                {
                    throw Invalid(
                        TransitionExecutionDiagnosticCodes.InvariantViolated,
                        $"Transition invariant '{invariant.Id.Value}' does not hold for the candidate state.",
                        invariant.Id);
                }
            }
        }

        PortableExpressionValue Evaluate(
            Expr expression,
            ExecutionNodeId node,
            bool candidateState,
            Dictionary<ValueBindingId, PortableExpressionValue>? scope = null)
        {
            var visible = scope ?? bindings;
            return evaluator.Evaluate(expression, new()
            {
                ResolveBinding = binding => ResolveBinding(binding, node, candidateState, visible),
                ResolveField = (binding, path) => ResolveField(binding, path, node, candidateState, visible),
                ResolveParameter = parameter => ResolveParameter(parameter)
            });
        }

        bool EvaluateBoolean(
            Expr expression,
            ExecutionNodeId node,
            bool candidateState,
            string operation,
            Dictionary<ValueBindingId, PortableExpressionValue>? scope = null) =>
            PortableExpressionReferenceEvaluator.RequireBoolean(
                Evaluate(expression, node, candidateState, scope),
                operation);

        PortableValue EvaluateTyped(
            Expr expression,
            ValueContract contract,
            ExecutionNodeId node,
            bool candidateState,
            Dictionary<ValueBindingId, PortableExpressionValue>? scope = null) =>
            ValidateTyped(Evaluate(expression, node, candidateState, scope), contract, node);

        PortableValue ValidateTyped(
            PortableExpressionValue value,
            ValueContract contract,
            ExecutionNodeId node)
        {
            if (value.State == PortableValueState.Failed && value.Failure is not null)
            {
                throw new TransitionRuntimeDecisionException(
                    TransitionDecisionKind.InfrastructureFailure,
                    value.Failure);
            }
            if (value.State is PortableValueState.Missing or PortableValueState.Unknown)
            {
                throw Infrastructure(
                    value.State == PortableValueState.Unknown
                        ? TransitionExecutionDiagnosticCodes.ObservationUnknown
                        : TransitionExecutionDiagnosticCodes.ObservationUnavailable,
                    $"Node '{node.Value}' produced non-terminal value state '{value.State}'.",
                    node);
            }

            var portable = value.ToPortable(contract);
            var validation = PortableExecutionValidator.Validate(portable, plan.ShapeGraph);
            if (!validation.IsValid)
            {
                throw Invalid(
                    TransitionExecutionDiagnosticCodes.ResultContractViolated,
                    $"Node '{node.Value}' produced a value outside its compiled contract: "
                    + string.Join("; ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                    node);
            }
            return portable;
        }

        PortableExpressionValue ResolveBinding(
            ValueBindingId binding,
            ExecutionNodeId node,
            bool candidateState,
            IReadOnlyDictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            if (binding == TransitionBindingIds.Observation)
                return candidateState ? ReadCandidateWhole(node) : ReadOriginal(access: TransitionObservationAccess.Whole, node);
            if (scope.TryGetValue(binding, out var value))
                return value;
            return PortableExpressionValue.Absent;
        }

        PortableExpressionValue ResolveField(
            ValueBindingId? binding,
            FieldPath path,
            ExecutionNodeId node,
            bool candidateState,
            IReadOnlyDictionary<ValueBindingId, PortableExpressionValue> scope)
        {
            var selected = binding ?? TransitionBindingIds.Observation;
            if (selected == TransitionBindingIds.Observation)
                return candidateState ? ReadCandidate(path, node) : ReadOriginal(TransitionObservationAccess.At(path), node);
            if (selected == TransitionBindingIds.Input)
                return PortableExpressionValue.FromPortable(activation.Input).Project(path);
            if (scope.TryGetValue(selected, out var value))
                return value.Project(path);
            return PortableExpressionValue.Absent;
        }

        PortableExpressionValue ResolveParameter(string parameter)
        {
            var path = FieldPath.FromField(parameter);
            var contract = ResolveInputContract(parameter);
            var value = PortableExpressionValue.FromPortable(activation.Input).Project(path);
            _ = ValidateTyped(value, contract, new($"input/{parameter}"));
            return value;
        }

        PortableExpressionValue ReadCandidate(FieldPath path, ExecutionNodeId node)
        {
            if (candidate.TryGetValue(path, out var exact))
                return exact;

            var ancestors = candidate
                .Where(entry => IsPrefix(entry.Key, path))
                .OrderByDescending(static entry => entry.Key.Segments.Length)
                .ToArray();
            if (ancestors.Length > 0)
            {
                var ancestor = ancestors[0];
                return ancestor.Value.Project(Suffix(path, ancestor.Key.Segments.Length));
            }

            var original = ReadOriginal(TransitionObservationAccess.At(path), node);
            var descendants = candidate
                .Where(entry => IsPrefix(path, entry.Key))
                .OrderBy(static entry => entry.Key, TransitionStructuralOrdering.FieldPaths)
                .ToArray();
            if (descendants.Length == 0)
                return original;

            var aggregate = original.RequireObservation("candidate-state reconstruction");
            foreach (var descendant in descendants)
            {
                aggregate = aggregate.WithField(
                    Suffix(descendant.Key, path.Segments.Length),
                    descendant.Value.RequireObservation("candidate-state reconstruction"));
            }
            return PortableExpressionValue.FromObservation(aggregate);
        }

        PortableExpressionValue ReadCandidateWhole(ExecutionNodeId node)
        {
            var original = ReadOriginal(TransitionObservationAccess.Whole, node);
            var aggregate = original.RequireObservation("candidate-state reconstruction");
            foreach (var entry in candidate.OrderBy(
                         static entry => entry.Key,
                         TransitionStructuralOrdering.FieldPaths))
                aggregate = aggregate.WithField(entry.Key, entry.Value.RequireObservation("candidate-state reconstruction"));
            return PortableExpressionValue.FromObservation(aggregate);
        }

        void WriteCandidate(FieldPath path, PortableExpressionValue value) => candidate[path] = value;

        PortableExpressionValue ReadOriginal(
            TransitionObservationAccess access,
            ExecutionNodeId node)
        {
            if (!TryResolveFrame(activation.Observation, access, out var portable))
            {
                throw Infrastructure(
                    TransitionExecutionDiagnosticCodes.ObservationUnavailable,
                    $"Required aggregate observation '{access}' was not supplied.",
                    node);
            }

            AddTrace(TransitionTraceEventKind.ObservationRead, node, access: access);
            if (!observedReads.ContainsKey(access))
            {
                observedReads.Add(access, portable);
                observedReadOrder.Add(access);
            }
            return PortableExpressionValue.FromPortable(portable);
        }

        bool TryResolveFrame(
            TransitionObservationFrame frame,
            TransitionObservationAccess access,
            out PortableValue value)
        {
            var contract = access.Path is { } path
                ? ResolveObservationContract(path)
                : plan.Definition.Observation;
            if (frame.TryGetExact(access, out var exact))
            {
                value = Recontract(exact, contract);
                return true;
            }

            if (access.Path is not { } requested)
            {
                value = null!;
                return false;
            }

            if (frame.TryGetExact(TransitionObservationAccess.Whole, out var whole))
            {
                value = PortableExpressionValue.FromPortable(whole).Project(requested).ToPortable(contract);
                return true;
            }

            var covering = frame.Entries
                .Where(entry => entry.Access.Path is { } supplied && IsPrefix(supplied, requested))
                .OrderByDescending(static entry => entry.Access.Path!.Value.Segments.Length)
                .FirstOrDefault();
            if (covering is null)
            {
                value = null!;
                return false;
            }

            value = PortableExpressionValue.FromPortable(covering.Value)
                .Project(Suffix(requested, covering.Access.Path!.Value.Segments.Length))
                .ToPortable(contract);
            return true;
        }

        TransitionDecision Complete(
            TransitionDecisionKind kind,
            PortableValue? outcome,
            ImmutableArray<TransitionExecutedPatch> retainedPatches,
            ImmutableArray<TransitionEmissionIntent> retainedEmissions,
            ImmutableArray<TransitionMachineMovement> retainedMovements)
        {
            var commitRequired = !retainedPatches.IsDefaultOrEmpty
                || !retainedEmissions.IsDefaultOrEmpty
                || !retainedMovements.IsDefaultOrEmpty;
            var demands = new TransitionGuaranteeDemands(
                commitRequired,
                atomicPatchAndEmissions: commitRequired
                    && !retainedPatches.IsDefaultOrEmpty
                    && !retainedEmissions.IsDefaultOrEmpty,
                commitRequired ? [.. observedReadOrder] : []);

            if (commitRequired && activation.CommitObservation is not null)
            {
                List<TransitionObservationConflict> conflicts = [];
                foreach (var access in observedReadOrder)
                {
                    if (!TryResolveFrame(activation.CommitObservation, access, out var fresh))
                    {
                        return Failure(
                            TransitionDecisionKind.InfrastructureFailure,
                            Diagnostic(
                                TransitionExecutionDiagnosticCodes.CommitObservationUnavailable,
                                $"Fresh commit evidence omitted actual read '{access}'.",
                                node: null,
                                stage: "commitValidation"));
                    }
                    if (fresh.State == PortableValueState.Unknown)
                    {
                        return Failure(
                            TransitionDecisionKind.InfrastructureFailure,
                            Diagnostic(
                                TransitionExecutionDiagnosticCodes.CommitObservationUnknown,
                                $"Fresh commit evidence reported actual read '{access}' as Unknown.",
                                node: null,
                                stage: "commitValidation"));
                    }
                    if (fresh.State == PortableValueState.Failed && fresh.Failure is not null)
                    {
                        return Failure(
                            TransitionDecisionKind.InfrastructureFailure,
                            fresh.Failure);
                    }
                    var expected = observedReads[access];
                    if (expected != fresh)
                        conflicts.Add(new(access, expected, fresh));
                }

                if (conflicts.Count > 0)
                {
                    return new(
                        TransitionDecisionKind.Conflict,
                        outcome: null,
                        patch: [],
                        emissions: [],
                        machineMovements: [],
                        new(
                            commitRequired: false,
                            atomicPatchAndEmissions: false,
                            concurrencyObservations: [.. observedReadOrder]),
                        conflicts: [.. conflicts],
                        diagnostics: [],
                        Evidence());
                }
            }

            return new(
                kind,
                outcome,
                retainedPatches,
                retainedEmissions,
                retainedMovements,
                demands,
                conflicts: [],
                diagnostics: [],
                Evidence());
        }

        TransitionDecision Failure(
            TransitionDecisionKind kind,
            DocumentValidationDiagnostic diagnostic) => new(
            kind,
            outcome: null,
            patch: [],
            emissions: [],
            machineMovements: [],
            new(
                commitRequired: false,
                atomicPatchAndEmissions: false,
                concurrencyObservations: []),
            conflicts: [],
            diagnostics: [diagnostic],
            Evidence());

        TransitionExecutionEvidence Evidence() => new(
            plan.DefinitionReference,
            activation.Id,
            [.. trace]);

        DocumentValidationDiagnostic? ValidateActivation()
        {
            if (activation.Input.Contract != plan.Definition.Input)
            {
                return Diagnostic(
                    TransitionExecutionDiagnosticCodes.ActivationInvalid,
                    "Activation input contract does not equal the compiled Transition input contract.",
                    node: null,
                    stage: "activationValidation");
            }
            var inputValidation = PortableExecutionValidator.Validate(activation.Input, plan.ShapeGraph);
            if (!inputValidation.IsValid)
                return inputValidation.Diagnostics[0];

            var observationFailure = ValidateFrame(activation.Observation, "observation");
            if (observationFailure is not null)
                return observationFailure;
            return activation.CommitObservation is null
                ? null
                : ValidateFrame(activation.CommitObservation, "commitObservation");
        }

        DocumentValidationDiagnostic? ValidateFrame(
            TransitionObservationFrame frame,
            string location)
        {
            foreach (var entry in frame.Entries)
            {
                ValueContract expected;
                try
                {
                    expected = entry.Access.Path is { } path
                        ? ResolveObservationContract(path)
                        : plan.Definition.Observation;
                }
                catch (TransitionRuntimeDecisionException exception)
                {
                    return Diagnostic(
                        TransitionExecutionDiagnosticCodes.ActivationInvalid,
                        $"Observation '{entry.Access}' is not a valid compiled aggregate access: "
                        + exception.Message,
                        node: null,
                        stage: "activationValidation",
                        location);
                }
                if (entry.Value.Contract != expected)
                {
                    return Diagnostic(
                        TransitionExecutionDiagnosticCodes.ActivationInvalid,
                        $"Observation '{entry.Access}' does not carry its exact compiled contract.",
                        node: null,
                        stage: "activationValidation",
                        location);
                }
                var validation = PortableExecutionValidator.Validate(entry.Value, plan.ShapeGraph);
                if (!validation.IsValid)
                    return validation.Diagnostics[0];
            }
            return null;
        }

        ValueContract ResolveObservationContract(FieldPath path)
        {
            if (!observationContracts.TryGetValue(path, out var contract))
            {
                contract = ResolveRelativeContract(plan.Definition.Observation, path);
                observationContracts.Add(path, contract);
            }
            return contract;
        }

        ValueContract ResolveInputContract(string parameter)
        {
            if (!inputContracts.TryGetValue(parameter, out var contract))
            {
                contract = ResolveRelativeContract(plan.Definition.Input, FieldPath.FromField(parameter));
                inputContracts.Add(parameter, contract);
            }
            return contract;
        }

        ValueContract ResolveRelativeContract(ValueContract root, FieldPath path)
        {
            var current = root;
            for (var index = 0; index < path.Segments.Length; index++)
            {
                var segment = path.Segments[index];
                if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
                {
                    throw Invalid(
                        TransitionExecutionDiagnosticCodes.ResultContractViolated,
                        $"Field path '{path}' contains unsupported collection-element navigation.",
                        new("contract/resolution"));
                }
                if (current.Cardinality == FieldCardinality.Many)
                {
                    throw Invalid(
                        TransitionExecutionDiagnosticCodes.ResultContractViolated,
                        $"Field path '{path}' navigates through a collection without an element scope.",
                        new("contract/resolution"));
                }

                current = ResolveFieldContract(current, segment.Segment)
                    ?? throw Invalid(
                        TransitionExecutionDiagnosticCodes.ResultContractViolated,
                        $"Field path '{path}' is absent from the compiled aggregate contract.",
                        new("contract/resolution"));
            }
            return current;
        }

        ValueContract? ResolveFieldContract(ValueContract parent, string name)
        {
            if (parent.Shape is { } shapeIdentity
                && plan.ShapeGraph?.TryGetShape(shapeIdentity, out var shape) == true
                && shape.TryGetField(name, out var shapeField))
            {
                return ValueContract.FromField(shapeField);
            }

            var type = parent.Type;
            if (type is NamedTypeRef named && plan.ShapeGraph?.TryGetType(named.TypeId, out var definition) == true)
            {
                if (definition is TypeDefinition.Structural structural
                    && structural.TryGetField(name, out var field))
                {
                    return new(
                        field.Type,
                        cardinality: field.Cardinality,
                        presence: field.Presence,
                        nullability: field.Nullability);
                }
                return null;
            }
            if (type is ObjectTypeRef objectType)
            {
                var field = objectType.Fields.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.Ordinal));
                if (field is not null)
                {
                    return new(
                        field.Type,
                        cardinality: field.Cardinality,
                        presence: field.Presence,
                        nullability: field.Nullability);
                }
            }
            return null;
        }

        ValueContract? FindExpressionContract(
            ExecutionNodeId node,
            TransitionExpressionSiteKind kind) => plan.Analysis.ExpressionSites
            .FirstOrDefault(site => site.Node == node && site.Kind == kind)
            ?.Analysis.KnownResult;

        static ValueContract ElementContract(ValueContract collection) => new(
            collection.Type,
            collection.Shape,
            cardinality: FieldCardinality.Single,
            presence: FieldPresence.Required,
            nullability: collection.Nullability);

        static PortableValue Recontract(PortableValue value, ValueContract contract) =>
            PortableExpressionValue.FromPortable(value).ToPortable(contract);

        static void RequireKnownPriorValue(
            PortableExpressionValue value,
            FieldPath path,
            ExecutionNodeId node)
        {
            if (value.State == PortableValueState.Failed && value.Failure is not null)
            {
                throw new TransitionRuntimeDecisionException(
                    TransitionDecisionKind.InfrastructureFailure,
                    value.Failure);
            }
            if (value.State is PortableValueState.Missing or PortableValueState.Unknown)
            {
                throw Infrastructure(
                    value.State == PortableValueState.Unknown
                        ? TransitionExecutionDiagnosticCodes.ObservationUnknown
                        : TransitionExecutionDiagnosticCodes.ObservationUnavailable,
                    $"Patch target '{path}' has non-comparable prior value state '{value.State}'.",
                    node);
            }
        }

        static IReadOnlyList<ObservationValue> RequireArray(
            PortableExpressionValue value,
            string operation)
        {
            var observation = value.RequireConcrete(operation);
            if (observation.Kind != ObservationValueKind.Array || observation.Array.IsDefault)
            {
                throw PortableExpressionReferenceEvaluator.Failure(
                    PortableExpressionEvaluationError.InvalidOperand,
                    $"Operation '{operation}' requires an array, but received '{observation.Kind}'.");
            }
            return observation.Array;
        }

        static ObservationValue[] Append(
            IReadOnlyList<ObservationValue> source,
            ObservationValue value)
        {
            var result = new ObservationValue[source.Count + 1];
            for (var index = 0; index < source.Count; index++)
                result[index] = source[index];
            result[^1] = value;
            return result;
        }

        static bool RuntimeEquals(PortableExpressionValue left, PortableExpressionValue right)
        {
            if (left.State != right.State)
                return false;
            return left.State != PortableValueState.Concrete
                || ObservationValueSemantics.Equals(left.Observation, right.Observation);
        }

        static bool IsPrefix(FieldPath prefix, FieldPath path)
        {
            if (prefix.Segments.Length > path.Segments.Length)
                return false;
            for (var index = 0; index < prefix.Segments.Length; index++)
            {
                if (prefix.Segments[index] != path.Segments[index])
                    return false;
            }
            return true;
        }

        static FieldPath Suffix(FieldPath path, int skipped) =>
            new([.. path.Segments.Skip(skipped)]);

        void TracePatch(TransitionExecutedPatch patch, TransitionTraceEventKind kind) => AddTrace(
            kind,
            patch.Node,
            path: patch.Path,
            before: patch.Before,
            after: patch.After,
            changed: patch.Changed);

        void AddTrace(
            TransitionTraceEventKind kind,
            ExecutionNodeId node,
            TransitionObservationAccess? access = null,
            FieldPath? path = null,
            ExecutionNodeId? selectedCase = null,
            PortableValue? before = null,
            PortableValue? after = null,
            bool? changed = null,
            ExecutionDefinitionReference? contract = null,
            ExecutionNodeId? edge = null,
            string? detail = null) => trace.Add(new(
            trace.Count,
            kind,
            node,
            access,
            path,
            selectedCase,
            before,
            after,
            changed,
            contract,
            edge,
            detail));

        static TransitionRuntimeDecisionException Invalid(
            string code,
            string message,
            ExecutionNodeId node) => new(
            TransitionDecisionKind.InvalidDefinition,
            Diagnostic(code, message, node, "referenceInterpretation"));

        static TransitionRuntimeDecisionException Infrastructure(
            string code,
            string message,
            ExecutionNodeId node) => new(
            TransitionDecisionKind.InfrastructureFailure,
            Diagnostic(code, message, node, "referenceInterpretation"));

        static DocumentValidationDiagnostic Diagnostic(
            string code,
            string message,
            ExecutionNodeId? node,
            string stage,
            string? location = null) => new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: stage,
                subject: node?.Value));

        sealed record Terminal(
            TransitionDecisionKind Kind,
            PortableValue Outcome,
            ExecutionNodeId Node);
    }

    sealed class TransitionRuntimeDecisionException(
        TransitionDecisionKind kind,
        DocumentValidationDiagnostic diagnostic)
        : InvalidOperationException(diagnostic.Message)
    {
        public TransitionDecisionKind Kind { get; } = kind;

        public DocumentValidationDiagnostic Diagnostic { get; } = diagnostic;
    }
}

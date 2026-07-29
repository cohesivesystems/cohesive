using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Processes.Execution;

/// <summary>Stable diagnostics emitted by canonical Process reference interpretation.</summary>
public static class ProcessExecutionDiagnosticCodes
{
    /// <summary>Plan, continuation, or activation evidence is incompatible.</summary>
    public const string ActivationInvalid = "processes.execution.activation.invalid";

    /// <summary>A pure Process expression could not be evaluated.</summary>
    public const string ExpressionFailed = "processes.execution.expression.failed";

    /// <summary>A dynamically produced value violates its compiled contract.</summary>
    public const string ResultContractViolated = "processes.execution.result.contractViolated";

    /// <summary>An explicit host operation returned structured failure evidence.</summary>
    public const string OperationFailed = "processes.execution.operation.failed";

    /// <summary>A host operation produced an interaction that violates the canonical contract catalog.</summary>
    public const string OperationEmissionInvalid = "processes.execution.operation.emissionInvalid";

    /// <summary>Canonical control flow reached an inconsistent runtime state.</summary>
    public const string ContinuationInvalid = "processes.execution.continuation.invalid";

    /// <summary>An interaction target cannot be resolved from canonical Process IR alone.</summary>
    public const string TargetResolutionFailed = "processes.execution.target.failed";

    /// <summary>An input targeted incompatible or unavailable continuation state.</summary>
    public const string InputNotAdmitted = "processes.execution.input.notAdmitted";

    /// <summary>A logical input identity was reused for different canonical evidence.</summary>
    public const string InputIdentityConflict = "processes.execution.input.identityConflict";

    /// <summary>An unscoped input matches more than one retained wait occurrence.</summary>
    public const string InputTargetAmbiguous = "processes.execution.input.targetAmbiguous";

    /// <summary>A recovery activation must be realized as a new Process attempt.</summary>
    public const string RecoveryRequiresRestart = "processes.execution.recovery.requiresRestart";

    /// <summary>An authored reuse policy had no prior winning disposition to reuse.</summary>
    public const string PriorDispositionUnavailable = "processes.execution.input.priorDispositionUnavailable";
}

/// <summary>Pure deterministic reference interpretation of canonical finite-activation Process IR.</summary>
/// <remarks>
/// The interpreter owns no threads, timers, repositories, leases, or ambient services. It reduces an immutable
/// semantic continuation and explicit activation evidence to replacement state and interaction intents. Physical
/// atomic checkpoint, inbox, and outbox persistence belongs to a separate runtime interpretation.
/// </remarks>
public static class ProcessReferenceInterpreter
{
    /// <summary>Creates initial execution state from durable Process-start acceptance evidence.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan.</param>
    /// <param name="receipt">Exact accepted Process-start request and admission time.</param>
    /// <returns>A continuation containing one ready root token at the canonical entry node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="receipt"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The receipt pins a different definition, or its input violates the compiled Process input contract.
    /// </exception>
    public static ProcessContinuationState Create(
        CompiledProcessPlan plan,
        ProcessStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Request.Definition != plan.DefinitionReference)
        {
            throw new ArgumentException("Process-start receipt pins a different compiled definition.", nameof(receipt));
        }

        return Create(
            plan,
            receipt.Request.InitialContinuation,
            receipt.Request.Input ?? PortableValue.Missing(plan.Definition.Input));
    }

    /// <summary>Creates the initial immutable continuation for one exact Process attempt.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan.</param>
    /// <param name="continuation">Logical Process instance and initial attempt.</param>
    /// <param name="input">Typed Process invocation input.</param>
    /// <returns>A continuation containing one ready root token at the canonical entry node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="continuation"/>, or <paramref name="input"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> has a different contract or violates the compiled Process input contract.
    /// </exception>
    public static ProcessContinuationState Create(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        PortableValue input)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Contract != plan.Definition.Input)
        {
            throw new ArgumentException("Process input does not carry the exact compiled input contract.", nameof(input));
        }

        var validation = PortableExecutionValidator.Validate(input, plan.ValidationContext.ShapeGraph);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "Process input violates its compiled contract: "
                + string.Join("; ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                nameof(input));
        }

        var root = new ProcessTokenState(
            ProcessReferenceIdentities.RootToken(continuation),
            plan.Definition.Entry,
            ExecutionTokenDisposition.Ready,
            step: 0,
            [new(ProcessBindingIds.Input, input)],
            requestObligations: [],
            forkMembership: null,
            failure: null);
        return new(
            plan.DefinitionReference,
            continuation,
            completedActivationCount: 0,
            [root],
            forks: [],
            waits: [],
            bufferedInputs: [],
            inputReceipts: [],
            outstandingRequests: [],
            new(ExecutionTerminalOutcomeKind.None));
    }

    /// <summary>Creates the clean replacement attempt required by a restart-on-recovery Process definition.</summary>
    /// <param name="plan">Successfully compiled exact Process plan.</param>
    /// <param name="abandoned">Interrupted continuation whose attempt must not resume.</param>
    /// <param name="replacementAttempt">New stable attempt identity allocated by the controlling runtime.</param>
    /// <returns>
    /// Initial state for the same Process instance and definition under <paramref name="replacementAttempt"/>.
    /// Tokens, waits, forks, receipts, Requests, and terminal state from <paramref name="abandoned"/> are not copied.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="abandoned"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Definition evidence differs, <paramref name="replacementAttempt"/> is default or unchanged, or the original
    /// Process input cannot be recovered exactly from the abandoned token set.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The Process definition does not declare <see cref="ProcessRecoveryPolicy.RestartAttempt"/>.
    /// </exception>
    public static ProcessContinuationState RestartAttempt(
        CompiledProcessPlan plan,
        ProcessContinuationState abandoned,
        ProcessAttemptId replacementAttempt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(abandoned);
        if (plan.Definition.RecoveryPolicy != ProcessRecoveryPolicy.RestartAttempt)
        {
            throw new InvalidOperationException(
                "Only a Process definition with RestartAttempt recovery policy can create a replacement attempt.");
        }
        if (abandoned.Definition != plan.DefinitionReference)
        {
            throw new ArgumentException("Abandoned continuation pins a different Process definition.", nameof(abandoned));
        }

        if (string.IsNullOrWhiteSpace(replacementAttempt.Value)
            || replacementAttempt == abandoned.Continuation.ProcessAttemptId)
        {
            throw new ArgumentException(
                "A replacement Process attempt identity must be stable and differ from the abandoned attempt.",
                nameof(replacementAttempt));
        }

        var inputs = abandoned.Tokens
            .SelectMany(static token => token.Bindings)
            .Where(static binding => binding.Binding == ProcessBindingIds.Input)
            .Select(static binding => binding.Value)
            .Distinct()
            .Take(2)
            .ToArray();
        if (inputs.Length != 1)
        {
            throw new ArgumentException(
                "The abandoned continuation must retain one exact canonical Process input value.",
                nameof(abandoned));
        }

        return Create(
            plan,
            new(
                abandoned.Continuation.ProcessInstanceId,
                replacementAttempt),
            inputs[0]);
    }

    /// <summary>Validates activation evidence that is independent of mutable continuation contents.</summary>
    /// <param name="plan">Successfully compiled exact Process plan.</param>
    /// <param name="continuation">Exact logical Process instance and attempt the activation addresses.</param>
    /// <param name="activation">Activation request to validate.</param>
    /// <returns>Structured provenance, cancellation, and recovery-policy diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static DocumentValidationResult ValidateActivationRequest(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        ProcessActivation activation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(activation);

        DocumentValidationDiagnostic? diagnostic = null;
        if (activation.Cancellation is { } cancellation
            && cancellation.AttemptId != continuation.ProcessAttemptId)
        {
            diagnostic = ActivationDiagnostic(
                plan,
                ProcessExecutionDiagnosticCodes.ActivationInvalid,
                "Cancellation intent targets a different Process attempt.");
        }
        else if (activation.Cancellation is not null
                 && activation.Cause is not (ProcessActivationCause.Control or ProcessActivationCause.Recovery))
        {
            diagnostic = ActivationDiagnostic(
                plan,
                ProcessExecutionDiagnosticCodes.ActivationInvalid,
                "A cancellation intent requires a Control or Recovery activation cause.");
        }
        else if (activation.Context.Provenance != plan.Document.Metadata.Provenance)
        {
            diagnostic = ActivationDiagnostic(
                plan,
                ProcessExecutionDiagnosticCodes.ActivationInvalid,
                "Activation emission provenance differs from the compiled Process document provenance.");
        }
        else if (activation.Cause == ProcessActivationCause.Recovery
                 && plan.Definition.RecoveryPolicy == ProcessRecoveryPolicy.RestartAttempt)
        {
            diagnostic = ActivationDiagnostic(
                plan,
                ProcessExecutionDiagnosticCodes.RecoveryRequiresRestart,
                "This Process definition requires recovery under a new attempt identity; the current continuation cannot resume.");
        }

        return diagnostic is null
            ? DocumentValidationResult.Valid
            : new([diagnostic]);
    }

    /// <summary>Reduces one immutable continuation through a finite deterministic activation.</summary>
    /// <param name="plan">Successfully compiled exact Process plan.</param>
    /// <param name="state">Complete semantic continuation to activate.</param>
    /// <param name="activation">Explicit activation cause, time, inputs, context, and optional cancellation.</param>
    /// <param name="host">Synchronous evidence port for Transition, Relation, and target resolution.</param>
    /// <returns>Replacement continuation, intents, input dispositions, diagnostics, and attributable trace.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static ProcessActivationDecision Activate(
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        ProcessActivation activation,
        IProcessReferenceHost host)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(host);
        return new Engine(plan, state, activation, host).Run();
    }

    static DocumentValidationDiagnostic ActivationDiagnostic(
        CompiledProcessPlan plan,
        string code,
        string message) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        "/activation",
        Evidence: new(
            stage: "processReferenceInterpretation",
            sourceReferences: [plan.Document.Metadata.Provenance.Source.Reference]));

    sealed class Engine
    {
        static readonly ValueContract UntypedValueContract = new(new JsonTypeRef(JsonTypeKind.Any));

        readonly CompiledProcessPlan plan;
        readonly ProcessContinuationState original;
        readonly ProcessActivation activation;
        readonly IProcessReferenceHost host;
        readonly PortableExpressionReferenceEvaluator evaluator = new(
            ProcessExpressionLanguage.Capabilities,
            "Process reference interpreter");
        readonly List<ProcessTokenState> tokens;
        readonly List<ProcessForkState> forks;
        readonly List<ProcessWaitState> waits;
        readonly List<ProcessBufferedInput> bufferedInputs;
        readonly List<ProcessInputReceipt> receipts;
        readonly List<ProcessOutstandingRequest> requests;
        readonly List<InteractionEnvelope> emissions = [];
        readonly List<ProcessInputReceipt> activationAdmissions = [];
        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly List<ProcessTraceEvent> trace = [];
        readonly Dictionary<ExecutionNodeId, int> nodeIndexes;
        ExecutionTerminalOutcome terminal;
        ExecutionNodeId? safePointNode;
        bool stopAtDurableCut;

        public Engine(
            CompiledProcessPlan plan,
            ProcessContinuationState state,
            ProcessActivation activation,
            IProcessReferenceHost host)
        {
            this.plan = plan;
            original = state;
            this.activation = activation;
            this.host = host;
            tokens = [.. state.Tokens];
            forks = [.. state.Forks];
            waits = [.. state.Waits];
            bufferedInputs = [.. state.BufferedInputs];
            receipts = [.. state.InputReceipts];
            requests = [.. state.OutstandingRequests];
            terminal = state.Terminal;
            nodeIndexes = plan.Definition.Nodes
                .Select(static (node, index) => (node.Id, index))
                .ToDictionary(static pair => pair.Id, static pair => pair.index);
        }

        public ProcessActivationDecision Run()
        {
            var invalid = ValidateActivation();
            if (invalid is not null)
            {
                return Rejected(invalid);
            }

            if (terminal.Kind != ExecutionTerminalOutcomeKind.None && activation.Cancellation is not null)
            {
                return Rejected(Diagnostic(
                    ProcessExecutionDiagnosticCodes.ActivationInvalid,
                    "A terminal Process attempt cannot accept a cancellation intent.",
                    node: null));
            }

            AdmitInputs();
            if (activation.Cancellation is not null)
            {
                return ApplyCancellation();
            }

            if (terminal.Kind != ExecutionTerminalOutcomeKind.None)
            {
                return CompleteDecision(DispositionFromTerminal());
            }

            ResumeExistingWaits();
            while (!stopAtDurableCut && terminal.Kind == ExecutionTerminalOutcomeKind.None)
            {
                var ready = tokens
                    .Where(static token => token.Disposition == ExecutionTokenDisposition.Ready)
                    .OrderBy(static token => token.Id.Value, StringComparer.Ordinal)
                    .Select(static token => token.Id)
                    .ToArray();
                if (ready.Length == 0)
                {
                    if (!ResolveJoins())
                    {
                        break;
                    }

                    continue;
                }

                foreach (var tokenId in ready)
                {
                    if (stopAtDurableCut || terminal.Kind != ExecutionTerminalOutcomeKind.None)
                    {
                        break;
                    }

                    var token = GetToken(tokenId);
                    if (token.Disposition == ExecutionTokenDisposition.Ready)
                    {
                        ExecuteToken(token);
                    }
                }

                if (!stopAtDurableCut && terminal.Kind == ExecutionTerminalOutcomeKind.None)
                {
                    _ = ResolveJoins();
                }
            }

            return CompleteDecision(
                terminal.Kind == ExecutionTerminalOutcomeKind.None
                    ? stopAtDurableCut
                        ? ProcessActivationDisposition.DurableCut
                        : ProcessActivationDisposition.Quiescent
                    : DispositionFromTerminal());
        }

        DocumentValidationDiagnostic? ValidateActivation()
        {
            if (original.Definition != plan.DefinitionReference)
            {
                return Diagnostic(
                    ProcessExecutionDiagnosticCodes.ActivationInvalid,
                    "Continuation definition identity, revision, or fingerprint differs from the compiled plan.",
                    node: null);
            }
            var validation = ValidateActivationRequest(plan, original.Continuation, activation);
            return validation.IsValid ? null : validation.Diagnostics[0];
        }

        ProcessActivationDecision ApplyCancellation()
        {
            CancelLiveTokens(except: null);
            CloseAllTokenWork();
            terminal = new(ExecutionTerminalOutcomeKind.Cancelled, activation.ObservedAtUtc);
            var anchor = tokens.OrderBy(static token => token.Id.Value, StringComparer.Ordinal).First();
            AddTrace(ProcessTraceEventKind.CancellationApplied, anchor, anchor.Node, detail: "safe-point");
            safePointNode = anchor.Node;
            return CompleteDecision(ProcessActivationDisposition.Cancelled);
        }

        void AdmitInputs()
        {
            foreach (var group in activation.Inputs
                         .GroupBy(static candidate => candidate.Envelope.Context.EmissionId)
                         .OrderBy(static group => group.Key.Value, StringComparer.Ordinal))
            {
                var candidates = group
                    .OrderBy(static candidate => candidate.Target.Continuation.ProcessInstanceId.Value, StringComparer.Ordinal)
                    .ThenBy(static candidate => candidate.Target.Continuation.ProcessAttemptId.Value, StringComparer.Ordinal)
                    .ThenBy(static candidate => candidate.Target.Token.Value, StringComparer.Ordinal)
                    .ThenBy(
                        static candidate => candidate.Target.WaitRegistrationId?.Value ?? string.Empty,
                        StringComparer.Ordinal)
                    .ThenBy(
                        static candidate => Convert.ToBase64String(
                            InteractionEnvelopeJsonSerializer.GetCanonicalBytes(candidate.Envelope)),
                        StringComparer.Ordinal)
                    .ToArray();
                var input = candidates[0];
                var emission = input.Envelope.Context.EmissionId;
                var envelopeValidations = candidates
                    .Select(ValidateInputEnvelope)
                    .ToArray();
                if (candidates.Skip(1).Any(candidate => !SameInput(input, candidate)))
                {
                    var conflict = Receipt(input, ProcessInputAdmissionDisposition.IdentityConflict);
                    UpsertActivationAdmission(conflict);
                    diagnostics.AddRange(envelopeValidations.SelectMany(static validation => validation.Diagnostics));
                    diagnostics.Add(Diagnostic(
                        ProcessExecutionDiagnosticCodes.InputIdentityConflict,
                        $"Activation presented conflicting canonical evidence for interaction emission '{emission.Value}'.",
                        node: null));
                    foreach (var candidate in candidates)
                    {
                        AddInputTrace(candidate, conflict with { Input = candidate }, "identity-conflict-batch");
                    }

                    continue;
                }

                var envelopeValidation = envelopeValidations[0];
                if (!envelopeValidation.IsValid)
                {
                    var rejected = Receipt(input, ProcessInputAdmissionDisposition.Rejected);
                    UpsertActivationAdmission(rejected);
                    receipts.Add(rejected);
                    diagnostics.AddRange(envelopeValidation.Diagnostics);
                    AddInputTrace(input, rejected, "invalid-envelope");
                    continue;
                }

                var prior = receipts.FirstOrDefault(candidate => candidate.Emission == emission);
                if (prior is not null)
                {
                    if (!SameInput(prior.Input, input))
                    {
                        var conflict = Receipt(input, ProcessInputAdmissionDisposition.IdentityConflict);
                        UpsertActivationAdmission(conflict);
                        diagnostics.Add(Diagnostic(
                            ProcessExecutionDiagnosticCodes.InputIdentityConflict,
                            $"Interaction emission '{emission.Value}' was reused for different canonical input evidence.",
                            node: null));
                        AddInputTrace(input, conflict, "identity-conflict");
                        continue;
                    }
                    var duplicate = prior with
                    {
                        Disposition = DuplicateDisposition(prior),
                        ObservedAtUtc = activation.ObservedAtUtc
                    };
                    UpsertActivationAdmission(duplicate);
                    AddInputTrace(input, duplicate, "duplicate");
                    continue;
                }

                if (input.Target.Continuation != original.Continuation)
                {
                    var policyWait = waits
                        .Where(candidate => candidate.Token == input.Target.Token
                                            && TargetMatchesWait(input.Target, candidate)
                                            && WaitAccepts(candidate, input.Envelope))
                        .OrderByDescending(static candidate => candidate.Active)
                        .ThenByDescending(static candidate => candidate.RegisteredAtUtc)
                        .ThenByDescending(static candidate => candidate.RegistrationId.Value, StringComparer.Ordinal)
                        .FirstOrDefault();
                    var stale = Receipt(
                        input,
                        policyWait is null
                            ? ProcessInputAdmissionDisposition.Stale
                            : StaleDisposition(policyWait, input),
                        policyWait?.RegistrationId);
                    UpsertActivationAdmission(stale);
                    receipts.Add(stale);
                    diagnostics.Add(Diagnostic(
                        ProcessExecutionDiagnosticCodes.InputNotAdmitted,
                        "Interaction targets a different Process instance or attempt.",
                        node: null));
                    AddInputTrace(input, stale, "stale-attempt");
                    continue;
                }

                var target = tokens.FirstOrDefault(candidate => candidate.Id == input.Target.Token);
                if (target is null)
                {
                    var missing = Receipt(input, ProcessInputAdmissionDisposition.MissingTarget);
                    UpsertActivationAdmission(missing);
                    receipts.Add(missing);
                    AddInputTrace(input, missing, "missing-target");
                    continue;
                }

                ProcessWaitState? activeWait;
                if (input.Target.WaitRegistrationId is { } exactRegistration)
                {
                    var addressedWait = waits.SingleOrDefault(candidate =>
                        candidate.Token == target.Id
                        && candidate.RegistrationId == exactRegistration);
                    if (addressedWait is null)
                    {
                        var missing = Receipt(input, ProcessInputAdmissionDisposition.MissingTarget);
                        UpsertActivationAdmission(missing);
                        receipts.Add(missing);
                        AddInputTrace(input, missing, "missing-wait-occurrence");
                        continue;
                    }

                    if (!addressedWait.Active)
                    {
                        var disposition = WaitAccepts(addressedWait, input.Envelope)
                            ? LateDisposition(addressedWait, input)
                            : MissingDisposition(addressedWait)
                              ?? ProcessInputAdmissionDisposition.MissingTarget;
                        var closed = Receipt(input, disposition, addressedWait.RegistrationId);
                        UpsertActivationAdmission(closed);
                        receipts.Add(closed);
                        AddInputTrace(input, closed, "closed-wait-occurrence");
                        continue;
                    }

                    activeWait = addressedWait;
                }
                else
                {
                    var hasActiveCompatibleWait = waits.Any(candidate =>
                        candidate.Token == target.Id
                        && candidate.Active
                        && WaitAccepts(candidate, input.Envelope));
                    var compatibleTombstones = waits
                        .Where(candidate => candidate.Token == target.Id
                                            && !candidate.Active
                                            && WaitAccepts(candidate, input.Envelope))
                        .OrderByDescending(static candidate => candidate.RegisteredAtUtc)
                        .ThenByDescending(static candidate => candidate.RegistrationId.Value, StringComparer.Ordinal)
                        .ToArray();
                    if (!hasActiveCompatibleWait && compatibleTombstones.Length > 1)
                    {
                        var ambiguous = Receipt(input, ProcessInputAdmissionDisposition.MissingTarget);
                        UpsertActivationAdmission(ambiguous);
                        receipts.Add(ambiguous);
                        diagnostics.Add(Diagnostic(
                            ProcessExecutionDiagnosticCodes.InputTargetAmbiguous,
                            "Unscoped interaction matches more than one retained wait occurrence.",
                            target.Node));
                        AddInputTrace(input, ambiguous, "ambiguous-wait-occurrence");
                        continue;
                    }

                    var tombstone = compatibleTombstones.FirstOrDefault();
                    if (tombstone is not null && !hasActiveCompatibleWait)
                    {
                        var late = Receipt(
                            input,
                            LateDisposition(tombstone, input),
                            tombstone.RegistrationId);
                        UpsertActivationAdmission(late);
                        receipts.Add(late);
                        AddInputTrace(input, late, "late");
                        continue;
                    }

                    activeWait = waits.SingleOrDefault(candidate => candidate.Token == target.Id && candidate.Active);
                }
                var missingDisposition = activeWait is null || WaitAccepts(activeWait, input.Envelope)
                    ? null
                    : MissingDisposition(activeWait);
                if (missingDisposition is { } resolvedMissingDisposition)
                {
                    var missing = Receipt(input, resolvedMissingDisposition, activeWait!.RegistrationId);
                    UpsertActivationAdmission(missing);
                    receipts.Add(missing);
                    AddInputTrace(input, missing, "incompatible-active-wait");
                    continue;
                }

                if (target.Disposition is ExecutionTokenDisposition.Completed
                    or ExecutionTokenDisposition.Failed
                    or ExecutionTokenDisposition.Cancelled)
                {
                    var missing = Receipt(input, ProcessInputAdmissionDisposition.MissingTarget);
                    UpsertActivationAdmission(missing);
                    receipts.Add(missing);
                    AddInputTrace(input, missing, "terminal-target");
                    continue;
                }

                var buffered = Receipt(input, ProcessInputAdmissionDisposition.Buffered);
                receipts.Add(buffered);
                UpsertActivationAdmission(buffered);
                bufferedInputs.Add(new(input, activation.ObservedAtUtc));
                AddInputTrace(input, buffered, "buffered");
            }
        }

        DocumentValidationResult ValidateInputEnvelope(ProcessActivationInput input)
        {
            var catalog = plan.ValidationContext.InteractionContracts;
            return catalog is null
                ? DocumentValidationResult.FromDiagnostics([
                    Diagnostic(
                        ProcessExecutionDiagnosticCodes.InputNotAdmitted,
                        "No exact interaction-contract catalog is available to admit the input.",
                        node: null)])
                : InteractionEnvelopeValidator.Validate(
                    input.Envelope,
                    catalog,
                    plan.ValidationContext.ShapeGraph);
        }

        void ResumeExistingWaits()
        {
            foreach (var waitId in waits
                         .Where(static wait => wait.Active)
                         .OrderBy(static wait => wait.RegistrationId.Value, StringComparer.Ordinal)
                         .Select(static wait => wait.RegistrationId)
                         .ToArray())
            {
                if (terminal.Kind != ExecutionTerminalOutcomeKind.None)
                {
                    return;
                }

                var wait = GetWait(waitId);
                var token = GetToken(wait.Token);
                if (token.Disposition != ExecutionTokenDisposition.Waiting)
                {
                    DeactivateWait(wait);
                    if (token.Disposition is ExecutionTokenDisposition.Ready or ExecutionTokenDisposition.Active)
                    {
                        FailToken(token, Diagnostic(
                            ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                            "An active persisted wait requires its token to be waiting.",
                            wait.Node));
                    }
                    continue;
                }
                ExecuteGuarded(token, wait.Node, () => ResumeWait(wait, token));
            }
        }

        void ResumeWait(ProcessWaitState wait, ProcessTokenState token)
        {
            switch (wait.Kind)
            {
                case ProcessWaitKind.DurableCut:
                    ResumeDurableCut(wait, token);
                    break;
                case ProcessWaitKind.Timer:
                    ResumeTimer(wait, token);
                    break;
                case ProcessWaitKind.Request:
                    ResumeRequest(wait, token);
                    break;
                case ProcessWaitKind.AwaitMatch:
                    _ = ResolveAwait(wait, token);
                    break;
                default:
                    FailToken(token, Diagnostic(
                        ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                        $"Unsupported persisted wait kind '{wait.Kind}'.",
                        token.Node));
                    break;
            }
        }

        void ResumeDurableCut(ProcessWaitState wait, ProcessTokenState token)
        {
            var node = (DurableCutProcessNode)plan.GetNode(wait.Node);
            DeactivateWait(wait);
            Resume(token, node.Resume, output: null);
        }

        void ResumeTimer(ProcessWaitState wait, ProcessTokenState token)
        {
            var timer = wait.Timers.Single();
            if (activation.ObservedAtUtc < timer.DueAtUtc)
            {
                return;
            }

            var node = (TimerProcessNode)plan.GetNode(wait.Node);
            DeactivateWait(wait, winnerClause: timer.Clause);
            AddTrace(ProcessTraceEventKind.WaitResolved, token, node.Id, timer.Clause, detail: "timer");
            Resume(token, node.Next, output: null);
        }

        void ResumeRequest(ProcessWaitState wait, ProcessTokenState token)
        {
            var outstanding = requests.SingleOrDefault(candidate => candidate.Token == token.Id && candidate.Node == wait.Node);
            if (outstanding is null)
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    "A persisted Request wait has no matching logical Request obligation.",
                    wait.Node));
                return;
            }

            var candidates = bufferedInputs
                .Where(item => item.Input.Target.Token == token.Id
                               && TargetMatchesWait(item.Input.Target, wait)
                               && item.Input.Envelope is ReplyEnvelope reply
                               && reply.InReplyTo == outstanding.Emission)
                .OrderBy(item => item.Input.Envelope.Context.EmissionId.Value, StringComparer.Ordinal)
                .ToArray();
            var node = (RequestProcessNode)plan.GetNode(wait.Node);
            foreach (var candidate in candidates)
            {
                var reply = (ReplyEnvelope)candidate.Input.Envelope;
                var branch = node.Outcomes.FirstOrDefault(outcome => outcome.Outcome == reply.Outcome.Id);
                if (branch is null || !ReplyMatchesRequest(reply, node.Contract))
                {
                    DispositionInput(
                        candidate,
                        ProcessInputAdmissionDisposition.Rejected,
                        wait.RegistrationId,
                        "request-result-rejected");
                    continue;
                }

                DispositionInput(candidate, ProcessInputAdmissionDisposition.Consumed, wait.RegistrationId, "request-result");
                requests.Remove(outstanding);
                DeactivateWait(wait, winnerClause: branch.Id, winnerInput: reply.Context.EmissionId);
                AddTrace(
                    ProcessTraceEventKind.WaitResolved,
                    token,
                    node.Id,
                    branch.Id,
                    reply.Context.EmissionId,
                    "request-reply");
                Resume(token, branch.Continuation, reply.Outcome.Value);
                DispositionOtherRequestResults(
                    token.Id,
                    outstanding.Emission,
                    GetWait(wait.RegistrationId));
                return;
            }
        }

        bool ReplyMatchesRequest(ReplyEnvelope reply, RequestContractReference request)
        {
            var catalog = plan.ValidationContext.InteractionContracts;
            return catalog is not null
                   && catalog.TryResolve(reply.Contract, out var resolved)
                   && resolved is ReplyContractDefinition definition
                   && definition.Request == request
                   && definition.Outcome == reply.Outcome.Id;
        }

        void DispositionOtherRequestResults(
            TokenId token,
            EmissionId request,
            ProcessWaitState closedWait)
        {
            foreach (var candidate in bufferedInputs
                         .Where(item => item.Input.Target.Token == token
                                        && TargetMatchesWait(item.Input.Target, closedWait)
                                        && item.Input.Envelope is ReplyEnvelope reply
                                        && reply.InReplyTo == request)
                         .ToArray())
            {
                DispositionInput(
                    candidate,
                    LateDisposition(closedWait, candidate.Input),
                    closedWait.RegistrationId,
                    "late-request-result");
            }
        }

        void ExecuteToken(ProcessTokenState token)
        {
            var node = plan.GetNode(token.Node);
            ReplaceToken(token with { Disposition = ExecutionTokenDisposition.Active });
            AddTrace(ProcessTraceEventKind.NodeEntered, token, node.Id);
            ExecuteGuarded(token, node.Id, () => ExecuteNode(token, node));
        }

        void ExecuteNode(ProcessTokenState token, CanonicalProcessNode node)
        {
            switch (node)
            {
                case InvokeTransitionProcessNode invocation:
                    ExecuteTransition(token, invocation);
                    break;
                case EvaluateRelationProcessNode evaluation:
                    ExecuteRelation(token, evaluation);
                    break;
                case RequestProcessNode request:
                    ExecuteRequest(token, request);
                    break;
                case EmitEventProcessNode emit:
                    ExecuteEvent(token, emit);
                    break;
                case SendSignalProcessNode signal:
                    ExecuteSignal(token, signal);
                    break;
                case ChoiceProcessNode choice:
                    ExecuteChoice(token, choice);
                    break;
                case MatchProcessNode match:
                    ExecuteMatch(token, match);
                    break;
                case ForkProcessNode fork:
                    ExecuteFork(token, fork);
                    break;
                case JoinProcessNode join:
                    ExecuteJoinArrival(token, join);
                    break;
                case AwaitMatchProcessNode awaitMatch:
                    ExecuteAwait(token, awaitMatch);
                    break;
                case TimerProcessNode timer:
                    ExecuteTimer(token, timer);
                    break;
                case ReplyProcessNode reply:
                    ExecuteReply(token, reply);
                    break;
                case DurableCutProcessNode cut:
                    ExecuteDurableCut(token, cut);
                    break;
                case ReturnProcessNode result:
                    ExecuteTerminal(token, result.Id, result.Result, ExecutionTerminalOutcomeKind.Completed);
                    break;
                case FailProcessNode failure:
                    ExecuteTerminal(token, failure.Id, failure.Result, ExecutionTerminalOutcomeKind.Failed);
                    break;
                default:
                    FailToken(token, Diagnostic(
                        ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                        $"Unsupported canonical Process node '{node.GetType().Name}'.",
                        node.Id));
                    break;
            }
        }

        void ExecuteGuarded(ProcessTokenState token, ExecutionNodeId node, Action action)
        {
            try
            {
                action();
            }
            catch (PortableExpressionEvaluationException exception)
            {
                FailToken(GetToken(token.Id), exception.SourceDiagnostic ?? Diagnostic(
                    ProcessExecutionDiagnosticCodes.ExpressionFailed,
                    exception.Message,
                    node));
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               or OverflowException
                                               or DivideByZeroException)
            {
                FailToken(GetToken(token.Id), Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    exception.Message,
                    node));
            }
        }

        void ExecuteTransition(ProcessTokenState token, InvokeTransitionProcessNode node)
        {
            if (!plan.ValidationContext.TryResolve(node.Transition, out var link))
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    "Compiled Transition link is unavailable.",
                    node.Id));
                return;
            }
            var subject = EvaluateUntyped(node.Subject, token);
            var input = EvaluateTyped(node.Input, link.Input, token);
            var result = host.InvokeTransition(new(
                node.Transition,
                subject,
                input,
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step,
                activation.ObservedAtUtc,
                activation.Context));
            CompleteOperation(token, node.Id, node.Continuation, link.Result, result);
        }

        void ExecuteRelation(ProcessTokenState token, EvaluateRelationProcessNode node)
        {
            if (!plan.ValidationContext.TryResolve(node.Relation, out var link))
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    "Compiled Relation or Query link is unavailable.",
                    node.Id));
                return;
            }
            var input = EvaluateTyped(node.Input, link.Input, token);
            var result = host.EvaluateRelation(new(
                node.Relation,
                input,
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step,
                activation.ObservedAtUtc,
                activation.Context));
            CompleteOperation(token, node.Id, node.Continuation, link.Result, result);
        }

        void CompleteOperation(
            ProcessTokenState token,
            ExecutionNodeId node,
            ProcessContinuation continuation,
            ValueContract resultContract,
            ProcessOperationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (!result.IsSuccessful)
            {
                AddTrace(
                    ProcessTraceEventKind.OperationCompleted,
                    token,
                    node,
                    detail: "failed",
                    operationOccurrence: token.Step);
                FailToken(token, result.Failure ?? Diagnostic(
                    ProcessExecutionDiagnosticCodes.OperationFailed,
                    "Host operation failed without structured evidence.",
                    node));
                return;
            }
            var value = result.Value!;
            if (value.Contract != resultContract)
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ResultContractViolated,
                    "Host operation returned a value with a different compiled result contract.",
                    node));
                return;
            }
            var valueValidation = PortableExecutionValidator.Validate(value, plan.ValidationContext.ShapeGraph);
            if (!valueValidation.IsValid)
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ResultContractViolated,
                    "Host operation result violates its compiled contract: "
                    + string.Join("; ", valueValidation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                    node));
                return;
            }

            foreach (var emission in result.Emissions)
            {
                var catalog = plan.ValidationContext.InteractionContracts;
                if (catalog is null)
                {
                    FailToken(token, Diagnostic(
                        ProcessExecutionDiagnosticCodes.OperationEmissionInvalid,
                        "Host operation produced an interaction without an exact compiled contract catalog.",
                        node));
                    return;
                }
                var validation = InteractionEnvelopeValidator.Validate(
                    emission,
                    catalog,
                    plan.ValidationContext.ShapeGraph);
                if (!validation.IsValid)
                {
                    FailToken(token, Diagnostic(
                        ProcessExecutionDiagnosticCodes.OperationEmissionInvalid,
                        "Host operation produced an invalid canonical interaction: "
                        + string.Join("; ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                        node));
                    return;
                }
            }
            emissions.AddRange(result.Emissions);
            AddTrace(
                ProcessTraceEventKind.OperationCompleted,
                token,
                node,
                detail: "completed",
                operationOccurrence: token.Step);
            Advance(token, continuation, value);
        }

        void ExecuteRequest(ProcessTokenState token, RequestProcessNode node)
        {
            var contract = ResolveContract<RequestContractDefinition>(node.Contract, node.Id);
            var payload = EvaluateTyped(node.Payload, contract.Payload.Contract, token);
            var emissionId = ProcessReferenceIdentities.Emission(
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step);
            var wait = RegisterWait(
                token,
                node.Id,
                ProcessWaitKind.Request,
                timers: [],
                obligationEmission: emissionId);
            var envelope = new RequestEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                EnvelopeContext(token, node.Id, emissionId, activation.Context.CausationId),
                node.Contract,
                payload,
                new ProcessTokenInteractionTarget(original.Continuation, token.Id, wait.RegistrationId));
            emissions.Add(envelope);
            requests.Add(new(token.Id, node.Id, emissionId, node.Contract, activation.ObservedAtUtc));
            AddTrace(
                ProcessTraceEventKind.InteractionEmitted,
                token,
                node.Id,
                emission: emissionId,
                detail: "request",
                emissionFingerprint: InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope));
            AddTrace(ProcessTraceEventKind.WaitRegistered, token, node.Id, detail: wait.RegistrationId.Value);
            Cut(node.Id);
        }

        void ExecuteEvent(ProcessTokenState token, EmitEventProcessNode node)
        {
            var contract = ResolveContract<DomainEventContractDefinition>(node.Contract, node.Id);
            var payload = EvaluateTyped(node.Payload, contract.Payload.Contract, token);
            var emissionId = ProcessReferenceIdentities.Emission(
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step);
            var envelope = new DomainEventEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                EnvelopeContext(token, node.Id, emissionId, activation.Context.CausationId),
                node.Contract,
                payload);
            emissions.Add(envelope);
            AddTrace(
                ProcessTraceEventKind.InteractionEmitted,
                token,
                node.Id,
                emission: emissionId,
                detail: "event",
                emissionFingerprint: InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope));
            Advance(token, node.Next);
        }

        void ExecuteSignal(ProcessTokenState token, SendSignalProcessNode node)
        {
            var contract = ResolveContract<SignalContractDefinition>(node.Contract, node.Id);
            var targetValue = EvaluateUntyped(node.Target, token);
            var resolved = host.ResolveSignalTarget(new(
                targetValue,
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step,
                activation.ObservedAtUtc,
                activation.Context));
            if (!resolved.IsSuccessful)
            {
                FailToken(token, resolved.Failure ?? Diagnostic(
                    ProcessExecutionDiagnosticCodes.TargetResolutionFailed,
                    "Signal target resolution failed without structured evidence.",
                    node.Id));
                return;
            }
            var payload = EvaluateTyped(node.Payload, contract.Payload.Contract, token);
            var emissionId = ProcessReferenceIdentities.Emission(
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step);
            var envelope = new SignalEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                EnvelopeContext(token, node.Id, emissionId, activation.Context.CausationId),
                node.Contract,
                payload,
                resolved.Target!);
            emissions.Add(envelope);
            AddTrace(
                ProcessTraceEventKind.InteractionEmitted,
                token,
                node.Id,
                emission: emissionId,
                detail: "signal",
                emissionFingerprint: InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope));
            Advance(token, node.Next);
        }

        void ExecuteChoice(ProcessTokenState token, ChoiceProcessNode node)
        {
            foreach (var choiceCase in node.Cases)
            {
                if (!EvaluateBoolean(choiceCase.Predicate, token, "Choice predicate"))
                {
                    continue;
                }

                AddTrace(ProcessTraceEventKind.BranchSelected, token, node.Id, choiceCase.Id);
                Advance(token, choiceCase.Next);
                return;
            }
            if (node.Fallback is not null)
            {
                AddTrace(ProcessTraceEventKind.BranchSelected, token, node.Id, node.Fallback.Id);
                Advance(token, node.Fallback.Next);
                return;
            }
            FailToken(token, Diagnostic(
                ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                "Exhaustive Choice reached no matching case.",
                node.Id));
        }

        void ExecuteMatch(ProcessTokenState token, MatchProcessNode node)
        {
            var selected = EvaluateTyped(node.Value, node.Contract, token);
            foreach (var matchCase in node.Cases)
            {
                if (selected != matchCase.Pattern)
                {
                    continue;
                }
                AddTrace(ProcessTraceEventKind.BranchSelected, token, node.Id, matchCase.Id);
                Advance(token, matchCase.Next);
                return;
            }
            if (node.Fallback is not null)
            {
                AddTrace(ProcessTraceEventKind.BranchSelected, token, node.Id, node.Fallback.Id);
                Advance(token, node.Fallback.Next);
                return;
            }
            FailToken(token, Diagnostic(
                ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                "Exhaustive Match reached no matching case.",
                node.Id));
        }

        void ExecuteFork(ProcessTokenState token, ForkProcessNode node)
        {
            var occurrence = token.Step;
            var registrationId = ProcessReferenceIdentities.ForkRegistration(
                original.Continuation,
                token.Id,
                node.Id,
                occurrence);
            var branchStates = ImmutableArray.CreateBuilder<ProcessForkBranchState>(node.Branches.Length);
            foreach (var branch in node.Branches)
            {
                var childId = ProcessReferenceIdentities.ForkToken(
                    original.Continuation,
                    token.Id,
                    node.Id,
                    occurrence,
                    branch.Id);
                var child = new ProcessTokenState(
                    childId,
                    branch.Start.Target,
                    ExecutionTokenDisposition.Ready,
                    step: 0,
                    token.Bindings,
                    token.RequestObligations,
                    new(registrationId, branch.Id),
                    failure: null);
                tokens.Add(child);
                branchStates.Add(new(branch.Id, childId, ExecutionTokenDisposition.Ready));
            }
            forks.Add(new(
                registrationId,
                token.Id,
                node.Id,
                node.Join,
                occurrence,
                token.Bindings,
                token.RequestObligations,
                branchStates.MoveToImmutable(),
                selectedBranches: [],
                resolved: false));
            ReplaceToken(token with
            {
                Node = node.Join,
                Disposition = ExecutionTokenDisposition.Waiting,
                Step = token.Step + 1
            });
            AddTrace(ProcessTraceEventKind.ForkCreated, token, node.Id, detail: registrationId);
        }

        void ExecuteJoinArrival(ProcessTokenState token, JoinProcessNode node)
        {
            if (token.ForkMembership is null)
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    "Only a reciprocal Fork branch may arrive at a Join.",
                    node.Id));
                return;
            }
            ReplaceToken(token with
            {
                Disposition = ExecutionTokenDisposition.Completed,
                Step = token.Step + 1
            });
            AddTrace(ProcessTraceEventKind.JoinArrived, token, node.Id, token.ForkMembership.Branch);
        }

        void ExecuteAwait(ProcessTokenState token, AwaitMatchProcessNode node)
        {
            var timers = node.Clauses
                .OfType<ProcessAwaitTimerClause>()
                .Select(clause => new ProcessTimerState(
                    clause.Id,
                    EvaluateInstant(clause.DueAt, token, clause.Id),
                    clause.Priority))
                .ToImmutableArray();
            var wait = RegisterWait(token, node.Id, ProcessWaitKind.AwaitMatch, timers);
            AddTrace(ProcessTraceEventKind.WaitRegistered, token, node.Id, detail: wait.RegistrationId.Value);
            _ = ResolveAwait(wait, GetToken(token.Id));
            Cut(node.Id);
        }

        void ExecuteTimer(ProcessTokenState token, TimerProcessNode node)
        {
            var dueAt = EvaluateInstant(node.DueAt, token, node.Id);
            var wait = RegisterWait(
                token,
                node.Id,
                ProcessWaitKind.Timer,
                [new(node.Id, dueAt, Priority: 0)]);
            AddTrace(ProcessTraceEventKind.WaitRegistered, token, node.Id, detail: wait.RegistrationId.Value);
            Cut(node.Id);
        }

        void ExecuteReply(ProcessTokenState token, ReplyProcessNode node)
        {
            var obligation = token.RequestObligations.FirstOrDefault(candidate => candidate.Binding == node.Request);
            if (obligation is null)
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    $"Reply obligation binding '{node.Request.Value}' is unavailable.",
                    node.Id));
                return;
            }
            var replyContract = ResolveContract<ReplyContractDefinition>(node.Contract, node.Id);
            var requestContract = ResolveContract<RequestContractDefinition>(replyContract.Request, node.Id);
            var outcome = requestContract.Response.Find(replyContract.Outcome)
                ?? throw new InvalidOperationException("Linked Reply outcome is unavailable from its Request contract.");
            var payload = EvaluateTyped(node.Payload, outcome.Schema.Contract, token);
            var emissionId = ProcessReferenceIdentities.Emission(
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                token.Step);
            var terminalOutcome = CreateOutcome(outcome, payload);
            var envelope = new ReplyEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                EnvelopeContext(token, node.Id, emissionId, obligation.Request.Context.EmissionId),
                node.Contract,
                obligation.Request.Context.EmissionId,
                terminalOutcome);
            emissions.Add(envelope);
            AddTrace(
                ProcessTraceEventKind.InteractionEmitted,
                token,
                node.Id,
                emission: emissionId,
                detail: "reply",
                emissionFingerprint: InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope));
            DischargeRequestObligation(obligation);
            Advance(GetToken(token.Id), node.Next);
        }

        void DischargeRequestObligation(ProcessRequestObligation obligation)
        {
            var request = obligation.Request.Context.EmissionId;
            for (var index = 0; index < tokens.Count; index++)
            {
                var candidate = tokens[index];
                var retained = candidate.RequestObligations
                    .Where(item => item.Request.Context.EmissionId != request)
                    .ToImmutableArray();
                if (retained.Length != candidate.RequestObligations.Length)
                {
                    tokens[index] = candidate with { RequestObligations = retained };
                }
            }

            for (var index = 0; index < forks.Count; index++)
            {
                var fork = forks[index];
                var retained = fork.ParentRequestObligations
                    .Where(item => item.Request.Context.EmissionId != request)
                    .ToImmutableArray();
                if (retained.Length != fork.ParentRequestObligations.Length)
                {
                    forks[index] = fork with { ParentRequestObligations = retained };
                }
            }
        }

        void ExecuteDurableCut(ProcessTokenState token, DurableCutProcessNode node)
        {
            var wait = RegisterWait(token, node.Id, ProcessWaitKind.DurableCut, timers: []);
            AddTrace(ProcessTraceEventKind.WaitRegistered, token, node.Id, detail: wait.RegistrationId.Value);
            Cut(node.Id);
        }

        void ExecuteTerminal(
            ProcessTokenState token,
            ExecutionNodeId node,
            Expr expression,
            ExecutionTerminalOutcomeKind kind)
        {
            var value = EvaluateTyped(expression, plan.Definition.Result, token);
            ReplaceToken(token with
            {
                Disposition = kind == ExecutionTerminalOutcomeKind.Completed
                    ? ExecutionTokenDisposition.Completed
                    : ExecutionTokenDisposition.Failed,
                Step = token.Step + 1
            });
            CancelLiveTokens(token.Id);
            CloseAllTokenWork();
            terminal = new(kind, activation.ObservedAtUtc, ExecutionStatusValue.Disclose(value));
            AddTrace(ProcessTraceEventKind.TerminalReached, token, node, detail: kind.ToString());
        }

        ProcessWaitState RegisterWait(
            ProcessTokenState token,
            ExecutionNodeId node,
            ProcessWaitKind kind,
            ImmutableArray<ProcessTimerState> timers,
            EmissionId? obligationEmission = null)
        {
            var registration = ProcessReferenceIdentities.WaitRegistration(
                original.Continuation,
                token.Id,
                node,
                token.Step);
            var wait = new ProcessWaitState(
                registration,
                token.Id,
                node,
                kind,
                activation.ObservedAtUtc,
                timers,
                active: true,
                obligationEmission: obligationEmission);
            waits.Add(wait);
            ReplaceToken(token with
            {
                Disposition = ExecutionTokenDisposition.Waiting,
                Step = token.Step + 1
            });
            return wait;
        }

        bool ResolveAwait(ProcessWaitState wait, ProcessTokenState token)
        {
            if (!wait.Active)
            {
                return false;
            }

            var node = (AwaitMatchProcessNode)plan.GetNode(wait.Node);
            List<AwaitCandidate> candidates = [];
            HashSet<EmissionId> compatibleInputs = [];
            HashSet<EmissionId> eligibleInputs = [];
            foreach (var clause in node.Clauses)
            {
                switch (clause)
                {
                    case ProcessAwaitInteractionClause interaction:
                        foreach (var buffered in bufferedInputs.Where(item =>
                                     item.Input.Target.Token == token.Id
                                     && TargetMatchesWait(item.Input.Target, wait)
                                     && Contract(item.Input.Envelope) == interaction.Contract))
                        {
                            compatibleInputs.Add(buffered.Input.Envelope.Context.EmissionId);
                            var payload = Payload(buffered.Input.Envelope);
                            var candidateToken = Bind(token, interaction.Input, payload);
                            if (interaction.Guard is null
                                || EvaluateBoolean(interaction.Guard, candidateToken, "AwaitMatch guard"))
                            {
                                candidates.Add(new(clause, buffered, payload));
                                eligibleInputs.Add(buffered.Input.Envelope.Context.EmissionId);
                            }
                        }
                        break;
                    case ProcessAwaitTimerClause timer:
                        var deadline = wait.Timers.Single(candidate => candidate.Clause == timer.Id);
                        if (activation.ObservedAtUtc >= deadline.DueAtUtc)
                        {
                            candidates.Add(new(clause, Input: null, Value: null));
                        }

                        break;
                }
            }

            foreach (var stale in bufferedInputs
                         .Where(input => compatibleInputs.Contains(input.Input.Envelope.Context.EmissionId)
                                         && !eligibleInputs.Contains(input.Input.Envelope.Context.EmissionId))
                         .ToArray())
            {
                DispositionInput(
                    stale,
                    Map(node.StaleInput, ProcessInputAdmissionDisposition.Stale),
                    wait.RegistrationId,
                    "await-stale");
            }
            if (candidates.Count == 0)
            {
                return false;
            }

            var winner = candidates
                .OrderByDescending(static candidate => candidate.Clause.Priority)
                .ThenBy(static candidate => candidate.Clause.Id.Value, StringComparer.Ordinal)
                .ThenBy(static candidate => candidate.Input?.Input.Envelope.Context.EmissionId.Value ?? string.Empty, StringComparer.Ordinal)
                .First();
            var winnerEmission = winner.Input?.Input.Envelope.Context.EmissionId;
            DeactivateWait(wait, winner.Clause.Id, winnerEmission);
            var updated = token;
            if (winner.Clause is ProcessAwaitInteractionClause interactionClause)
            {
                updated = Bind(updated, interactionClause.Input, winner.Value!);
                if (interactionClause.RequestObligation is not null
                    && winner.Input!.Input.Envelope is RequestEnvelope request)
                {
                    updated = AddObligation(updated, interactionClause.RequestObligation.Binding, request);
                }
                DispositionInput(
                    winner.Input!,
                    ProcessInputAdmissionDisposition.Consumed,
                    wait.RegistrationId,
                    "await-winner");
            }
            AddTrace(
                ProcessTraceEventKind.WaitResolved,
                updated,
                node.Id,
                winner.Clause.Id,
                winnerEmission,
                winner.Input is null ? "timer" : "interaction");
            Resume(updated, winner.Clause.Continuation, winner.Value);
            DispositionAwaitLosers(node, wait, token.Id, winner);
            return true;
        }

        void DispositionAwaitLosers(
            AwaitMatchProcessNode node,
            ProcessWaitState wait,
            TokenId token,
            AwaitCandidate winner)
        {
            var interactionContracts = node.Clauses
                .OfType<ProcessAwaitInteractionClause>()
                .Select(static clause => clause.Contract)
                .ToHashSet();
            foreach (var candidate in bufferedInputs
                         .Where(item => item.Input.Target.Token == token
                                        && TargetMatchesWait(item.Input.Target, wait)
                                        && interactionContracts.Contains(Contract(item.Input.Envelope)))
                         .ToArray())
            {
                if (winner.Input == candidate)
                {
                    continue;
                }

                DispositionInput(
                    candidate,
                    Map(node.LateInput, ProcessInputAdmissionDisposition.Late),
                    wait.RegistrationId,
                    "await-loser");
            }
        }

        bool ResolveJoins()
        {
            var progressed = false;
            foreach (var registrationId in forks
                         .Where(static fork => !fork.Resolved)
                         .OrderBy(static fork => fork.Join.Value, StringComparer.Ordinal)
                         .ThenBy(static fork => fork.Owner.Value, StringComparer.Ordinal)
                         .Select(static fork => fork.RegistrationId)
                         .ToArray())
            {
                var fork = GetFork(registrationId);
                var join = (JoinProcessNode)plan.GetNode(fork.Join);
                var completed = fork.Branches
                    .Where(static branch => branch.Disposition == ExecutionTokenDisposition.Completed)
                    .ToArray();
                var failed = fork.Branches
                    .Where(static branch => branch.Disposition == ExecutionTokenDisposition.Failed)
                    .ToArray();
                var terminalBranches = fork.Branches.Count(static branch => branch.Disposition is
                    ExecutionTokenDisposition.Completed or ExecutionTokenDisposition.Failed or ExecutionTokenDisposition.Cancelled);
                if (failed.Length > 0 && join.Policy.Failure == ProcessJoinFailurePolicy.FailFast)
                {
                    var failedToken = GetToken(failed.OrderBy(static branch => branch.Branch.Value, StringComparer.Ordinal).First().Token);
                    terminal = new(ExecutionTerminalOutcomeKind.Failed, activation.ObservedAtUtc);
                    diagnostics.Add(failedToken.Failure ?? Diagnostic(
                        ProcessExecutionDiagnosticCodes.OperationFailed,
                        "A Fork branch failed under FailFast Join policy.",
                        join.Id));
                    CancelLiveTokens(except: null);
                    CloseAllTokenWork();
                    progressed = true;
                    break;
                }

                var threshold = join.Policy.Mode switch
                {
                    ProcessJoinMode.All => fork.Branches.Length,
                    ProcessJoinMode.Any => 1,
                    ProcessJoinMode.RequiredCount => join.Policy.RequiredCount,
                    _ => throw new InvalidOperationException("Compiled Join has an unsupported completion mode.")
                };
                var thresholdReached = completed.Length >= threshold;
                if (thresholdReached && fork.SelectedBranches.IsDefaultOrEmpty)
                {
                    var provisional = OrderEligible(completed, join.Policy).Take(threshold).ToArray();
                    fork = fork with
                    {
                        SelectedBranches = [.. provisional.Select(static branch => branch.Branch)]
                    };
                    ReplaceFork(fork);
                }

                var satisfied = thresholdReached;
                if (satisfied && join.Policy.Cancellation == ProcessJoinCancellationPolicy.AwaitRemaining)
                {
                    satisfied = terminalBranches == fork.Branches.Length;
                }

                if (!satisfied)
                {
                    if (terminalBranches == fork.Branches.Length)
                    {
                        terminal = new(ExecutionTerminalOutcomeKind.Failed, activation.ObservedAtUtc);
                        diagnostics.Add(Diagnostic(
                            ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                            "Join completion threshold became impossible after every branch terminated.",
                            join.Id));
                        CancelLiveTokens(except: null);
                        CloseAllTokenWork();
                        progressed = true;
                        break;
                    }
                    continue;
                }

                var selected = fork.SelectedBranches.IsDefaultOrEmpty
                    ? OrderEligible(completed, join.Policy).Take(threshold).ToArray()
                    : fork.SelectedBranches
                        .Select(branch => fork.Branches.Single(candidate => candidate.Branch == branch))
                        .ToArray();
                if (join.Policy.Cancellation == ProcessJoinCancellationPolicy.CancelRemaining)
                {
                    var selectedIds = selected.Select(static branch => branch.Branch).ToHashSet();
                    foreach (var branch in fork.Branches.Where(branch => !selectedIds.Contains(branch.Branch)))
                    {
                        CancelToken(GetToken(branch.Token));
                    }

                    fork = GetFork(registrationId);
                }

                var owner = GetToken(fork.Owner);
                var bindings = join.Policy.Mode == ProcessJoinMode.All
                    ? MergeAllBranchValues(
                        fork.ParentBindings,
                        selected,
                        static token => token.Bindings,
                        static binding => binding.Binding,
                        static binding => binding.Binding.Value)
                    : fork.ParentBindings;
                var requestObligations = join.Policy.Mode == ProcessJoinMode.All
                    ? MergeAllRequestObligations(fork, selected)
                    : fork.ParentRequestObligations;
                ReplaceToken(owner with
                {
                    Node = join.Next.Target,
                    Disposition = ExecutionTokenDisposition.Ready,
                    Bindings = bindings,
                    RequestObligations = requestObligations
                });
                ReplaceFork(fork with
                {
                    Resolved = true,
                    SelectedBranches = [.. selected.Select(static branch => branch.Branch)]
                });
                AddTrace(
                    ProcessTraceEventKind.JoinResolved,
                    owner,
                    join.Id,
                    detail: string.Join(",", selected.Select(static branch => branch.Branch.Value)));
                progressed = true;
            }
            return progressed;
        }

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
                _ => throw new InvalidOperationException("Compiled Join has an unsupported tie-break policy.")
            };

        ImmutableArray<TValue> MergeAllBranchValues<TKey, TValue>(
            ImmutableArray<TValue> parentValues,
            IReadOnlyList<ProcessForkBranchState> selected,
            Func<ProcessTokenState, ImmutableArray<TValue>> selectValues,
            Func<TValue, TKey> selectKey,
            Func<TValue, string> selectStableIdentity)
            where TKey : notnull
        {
            if (selected.Count == 0)
            {
                return parentValues;
            }

            var merged = parentValues.ToDictionary(selectKey);
            foreach (var branch in selected)
            {
                foreach (var value in selectValues(GetToken(branch.Token)))
                {
                    var key = selectKey(value);
                    if (merged.TryGetValue(key, out var existing)
                        && !EqualityComparer<TValue>.Default.Equals(existing, value))
                    {
                        throw new InvalidOperationException(
                            $"All-Join branch state diverged for '{selectStableIdentity(value)}'.");
                    }
                    merged[key] = value;
                }
            }
            return [.. merged.Values.OrderBy(selectStableIdentity, StringComparer.Ordinal)];
        }

        ImmutableArray<ProcessRequestObligation> MergeAllRequestObligations(
            ProcessForkState fork,
            IReadOnlyList<ProcessForkBranchState> selected)
        {
            if (selected.Count == 0)
            {
                return fork.ParentRequestObligations;
            }

            var parent = fork.ParentRequestObligations.ToDictionary(static obligation => obligation.Binding);
            Dictionary<RequestObligationBindingId, ProcessRequestObligation> merged = [];
            foreach (var branch in selected)
            {
                foreach (var obligation in GetToken(branch.Token).RequestObligations)
                {
                    if (parent.ContainsKey(obligation.Binding))
                    {
                        continue;
                    }

                    if (merged.TryGetValue(obligation.Binding, out var existing) && existing != obligation)
                    {
                        throw new InvalidOperationException(
                            $"All-Join branch state diverged for Request obligation '{obligation.Binding.Value}'.");
                    }
                    merged[obligation.Binding] = obligation;
                }
            }

            foreach (var obligation in fork.ParentRequestObligations)
            {
                var retainedByEveryBranch = selected.All(branch => GetToken(branch.Token).RequestObligations.Any(
                    candidate => candidate.Binding == obligation.Binding && candidate == obligation));
                if (retainedByEveryBranch)
                {
                    merged[obligation.Binding] = obligation;
                }
            }
            return [.. merged.Values.OrderBy(static obligation => obligation.Binding.Value, StringComparer.Ordinal)];
        }

        void FailToken(ProcessTokenState token, DocumentValidationDiagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
            var failed = token with
            {
                Disposition = ExecutionTokenDisposition.Failed,
                Step = token.Step + 1,
                Failure = diagnostic
            };
            ReplaceToken(failed);
            CloseTokenWork(failed.Id);
            AddTrace(ProcessTraceEventKind.TerminalReached, failed, token.Node, detail: "failed");
            if (token.ForkMembership is not null)
            {
                return;
            }

            terminal = new(ExecutionTerminalOutcomeKind.Failed, activation.ObservedAtUtc);
            CancelLiveTokens(token.Id);
            CloseAllTokenWork();
        }

        void Advance(ProcessTokenState token, ProcessContinuation continuation, PortableValue? output) =>
            Advance(token, continuation.Edge, continuation.Output is null ? null : output, continuation.Output);

        void Advance(ProcessTokenState token, ProcessEdge edge) => Advance(token, edge, output: null, binding: null);

        void Advance(
            ProcessTokenState token,
            ProcessEdge edge,
            PortableValue? output,
            ProcessOutputBinding? binding)
        {
            var updated = binding is null ? token : Bind(token, binding, output!);
            updated = updated with
            {
                Node = edge.Target,
                Disposition = ExecutionTokenDisposition.Ready,
                Step = token.Step + 1
            };
            ReplaceToken(updated);
            AddTrace(ProcessTraceEventKind.TokenAdvanced, updated, token.Node, detail: edge.Id.Value);
        }

        void Resume(ProcessTokenState token, ProcessContinuation continuation, PortableValue? output)
        {
            var updated = continuation.Output is null ? token : Bind(token, continuation.Output, output!);
            Resume(updated, continuation.Edge, output: null);
        }

        void Resume(ProcessTokenState token, ProcessEdge edge, PortableValue? output)
        {
            var updated = token with
            {
                Node = edge.Target,
                Disposition = ExecutionTokenDisposition.Ready
            };
            ReplaceToken(updated);
            AddTrace(ProcessTraceEventKind.TokenAdvanced, updated, token.Node, detail: edge.Id.Value);
        }

        ProcessTokenState Bind(ProcessTokenState token, ProcessOutputBinding binding, PortableValue value)
        {
            var validated = ValidateValue(value, binding.Contract, token.Node);
            var values = token.Bindings.Where(candidate => candidate.Binding != binding.Binding).ToList();
            values.Add(new(binding.Binding, validated));
            values.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Binding.Value, right.Binding.Value));
            return token with { Bindings = [.. values] };
        }

        ProcessTokenState AddObligation(
            ProcessTokenState token,
            RequestObligationBindingId binding,
            RequestEnvelope request)
        {
            var obligations = token.RequestObligations.Where(candidate => candidate.Binding != binding).ToList();
            obligations.Add(new(binding, request));
            obligations.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Binding.Value, right.Binding.Value));
            return token with { RequestObligations = [.. obligations] };
        }

        PortableValue EvaluateTyped(Expr expression, ValueContract contract, ProcessTokenState token) =>
            ValidateRuntimeValue(Evaluate(expression, token), contract, token.Node);

        PortableValue EvaluateUntyped(Expr expression, ProcessTokenState token) =>
            Evaluate(expression, token).ToPortable(UntypedValueContract);

        PortableExpressionValue Evaluate(Expr expression, ProcessTokenState token)
        {
            var bindings = token.Bindings.ToDictionary(static binding => binding.Binding, static binding => binding.Value);
            return evaluator.Evaluate(expression, new()
            {
                ResolveBinding = binding => bindings.TryGetValue(binding, out var value)
                    ? PortableExpressionValue.FromPortable(value)
                    : PortableExpressionValue.Absent,
                ResolveField = (binding, path) =>
                {
                    var selected = binding ?? ProcessBindingIds.Input;
                    return bindings.TryGetValue(selected, out var value)
                        ? PortableExpressionValue.FromPortable(value).Project(path)
                        : PortableExpressionValue.Absent;
                },
                ResolveParameter = _ => PortableExpressionValue.Absent
            });
        }

        bool EvaluateBoolean(Expr expression, ProcessTokenState token, string operation) =>
            PortableExpressionReferenceEvaluator.RequireBoolean(Evaluate(expression, token), operation);

        DateTimeOffset EvaluateInstant(Expr expression, ProcessTokenState token, ExecutionNodeId node)
        {
            var value = Evaluate(expression, token).RequireConcrete("Process timer deadline");
            if (!value.TryGetInstant(out var instant))
            {
                throw new InvalidOperationException($"Timer expression at node '{node.Value}' did not produce an instant.");
            }

            return instant.ToUniversalTime();
        }

        PortableValue ValidateRuntimeValue(
            PortableExpressionValue value,
            ValueContract contract,
            ExecutionNodeId node)
        {
            if (value.State is PortableValueState.Missing or PortableValueState.Unknown or PortableValueState.Failed)
            {
                throw new InvalidOperationException(
                    $"Node '{node.Value}' produced non-materialized value state '{value.State}'.");
            }
            return ValidateValue(value.ToPortable(contract), contract, node);
        }

        PortableValue ValidateValue(PortableValue value, ValueContract contract, ExecutionNodeId node)
        {
            if (value.Contract != contract)
            {
                throw new InvalidOperationException(
                    $"Node '{node.Value}' produced a value with a different compiled contract.");
            }
            var validation = PortableExecutionValidator.Validate(value, plan.ValidationContext.ShapeGraph);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    $"Node '{node.Value}' produced a value outside its compiled contract: "
                    + string.Join("; ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            }
            return value;
        }

        TDefinition ResolveContract<TDefinition>(
            InteractionContractReference reference,
            ExecutionNodeId node)
            where TDefinition : InteractionContractDefinition
        {
            var catalog = plan.ValidationContext.InteractionContracts;
            if (catalog is null || !catalog.TryResolve(reference, out var contract) || contract is not TDefinition typed)
            {
                throw new InvalidOperationException(
                    $"Node '{node.Value}' has no exact linked {typeof(TDefinition).Name} contract.");
            }
            return typed;
        }

        InteractionEnvelopeContext EnvelopeContext(
            ProcessTokenState token,
            ExecutionNodeId node,
            EmissionId emission,
            EmissionId? causation) => new(
            emission,
            new ProcessInteractionOrigin(
                plan.DefinitionReference,
                node,
                original.Continuation,
                activation.Id,
                token.Id),
            activation.Context.CorrelationId,
            causation,
            activation.Context.AuthorityScope,
            ProcessReferenceIdentities.Idempotency(emission),
            activation.Context.Ordering,
            activation.Context.Delivery,
            activation.Context.Provenance);

        static RequestTerminalOutcome CreateOutcome(
            RequestTerminalOutcomeDefinition definition,
            PortableValue value) => definition switch
            {
                RequestResultDefinition result => new RequestResultOutcome(result.Id, value),
                RequestFailureDefinition failure => new RequestFailureOutcome(failure.Id, value),
                RequestTimeoutDefinition timeout => new RequestTimeoutOutcome(timeout.Id, value),
                RequestCancellationDefinition cancellation => new RequestCancellationOutcome(cancellation.Id, value),
                _ => throw new InvalidOperationException("Unsupported canonical Request terminal outcome definition.")
            };

        static InteractionContractReference Contract(InteractionEnvelope envelope) => envelope switch
        {
            DomainEventEnvelope domainEvent => domainEvent.Contract,
            RequestEnvelope request => request.Contract,
            SignalEnvelope signal => signal.Contract,
            ReplyEnvelope reply => reply.Contract,
            _ => throw new InvalidOperationException("Unsupported canonical interaction envelope.")
        };

        static PortableValue Payload(InteractionEnvelope envelope) => envelope switch
        {
            DomainEventEnvelope domainEvent => domainEvent.Payload,
            RequestEnvelope request => request.Payload,
            SignalEnvelope signal => signal.Payload,
            ReplyEnvelope reply => reply.Outcome.Value,
            _ => throw new InvalidOperationException("Unsupported canonical interaction envelope.")
        };

        ProcessInputAdmissionDisposition LateDisposition(
            ProcessWaitState wait,
            ProcessActivationInput input)
        {
            if (wait.Kind == ProcessWaitKind.AwaitMatch
                && plan.GetNode(wait.Node) is AwaitMatchProcessNode awaitMatch)
            {
                return Map(
                    awaitMatch.LateInput,
                    wait,
                    input);
            }
            if (wait.Kind == ProcessWaitKind.Request
                && plan.GetNode(wait.Node) is RequestProcessNode request
                && plan.ValidationContext.InteractionContracts?.TryResolve(request.Contract, out var contract) == true
                && contract is RequestContractDefinition requestDefinition)
            {
                if (!IsValidRequestResult(wait, request, input))
                {
                    return ProcessInputAdmissionDisposition.Rejected;
                }

                return Map(
                    requestDefinition.Response.LateResult,
                    wait,
                    input);
            }
            return ProcessInputAdmissionDisposition.Late;
        }

        ProcessInputAdmissionDisposition StaleDisposition(
            ProcessWaitState wait,
            ProcessActivationInput input)
        {
            if (wait.Kind == ProcessWaitKind.AwaitMatch
                && plan.GetNode(wait.Node) is AwaitMatchProcessNode awaitMatch)
            {
                return Map(
                    awaitMatch.StaleInput,
                    wait,
                    input);
            }
            if (wait.Kind == ProcessWaitKind.Request
                && plan.GetNode(wait.Node) is RequestProcessNode request
                && plan.ValidationContext.InteractionContracts?.TryResolve(request.Contract, out var contract) == true
                && contract is RequestContractDefinition requestDefinition)
            {
                if (!IsValidRequestResult(wait, request, input))
                {
                    return ProcessInputAdmissionDisposition.Rejected;
                }

                return Map(
                    requestDefinition.Response.StaleResult,
                    wait,
                    input);
            }
            return ProcessInputAdmissionDisposition.Stale;
        }

        ProcessInputAdmissionDisposition? MissingDisposition(ProcessWaitState wait)
        {
            if (wait.Kind == ProcessWaitKind.AwaitMatch
                && plan.GetNode(wait.Node) is AwaitMatchProcessNode awaitMatch)
            {
                return Map(awaitMatch.MissingTarget);
            }
            return null;
        }

        bool IsValidRequestResult(
            ProcessWaitState wait,
            RequestProcessNode request,
            ProcessActivationInput input)
        {
            var valid = input.Envelope is ReplyEnvelope reply
                        && wait.ObligationEmission == reply.InReplyTo
                        && ReplyMatchesRequest(reply, request.Contract)
                        && request.Outcomes.Any(outcome => outcome.Outcome == reply.Outcome.Id);
            if (valid)
            {
                return true;
            }

            diagnostics.Add(Diagnostic(
                ProcessExecutionDiagnosticCodes.InputNotAdmitted,
                "A correlated Request result does not satisfy the exact Request contract and authored outcome set.",
                request.Id));
            return false;
        }

        ProcessInputAdmissionDisposition PriorWinnerDisposition(
            ProcessWaitState wait,
            ProcessActivationInput input)
        {
            if (wait.WinnerInput is { } winner
                && receipts.FirstOrDefault(candidate => candidate.Emission == winner) is { } prior)
            {
                return prior.Disposition;
            }

            diagnostics.Add(Diagnostic(
                ProcessExecutionDiagnosticCodes.PriorDispositionUnavailable,
                $"Wait '{wait.RegistrationId}' has no winning input disposition to reuse for emission '{input.Envelope.Context.EmissionId.Value}'.",
                wait.Node));
            return ProcessInputAdmissionDisposition.Rejected;
        }

        ProcessInputAdmissionDisposition DuplicateDisposition(ProcessInputReceipt prior)
        {
            if (prior.WaitRegistrationId is null)
            {
                return ProcessInputAdmissionDisposition.Duplicate;
            }

            var wait = waits.FirstOrDefault(candidate =>
                candidate.RegistrationId == prior.WaitRegistrationId);
            if (wait is null)
            {
                return ProcessInputAdmissionDisposition.Duplicate;
            }

            if (wait.Kind == ProcessWaitKind.AwaitMatch
                && plan.GetNode(wait.Node) is AwaitMatchProcessNode awaitMatch)
            {
                return Map(awaitMatch.DuplicateInput, prior.Disposition);
            }
            if (wait.Kind == ProcessWaitKind.Request
                && plan.GetNode(wait.Node) is RequestProcessNode request
                && plan.ValidationContext.InteractionContracts?.TryResolve(request.Contract, out var contract) == true
                && contract is RequestContractDefinition requestDefinition)
            {
                return Map(requestDefinition.Response.DuplicateResult, prior.Disposition);
            }
            return ProcessInputAdmissionDisposition.Duplicate;
        }

        bool WaitAccepts(ProcessWaitState wait, InteractionEnvelope envelope)
        {
            return wait.Kind switch
            {
                ProcessWaitKind.Request when envelope is ReplyEnvelope reply =>
                    wait.ObligationEmission == reply.InReplyTo,
                ProcessWaitKind.AwaitMatch when plan.GetNode(wait.Node) is AwaitMatchProcessNode awaitMatch =>
                    awaitMatch.Clauses
                        .OfType<ProcessAwaitInteractionClause>()
                        .Any(clause => clause.Contract == Contract(envelope)),
                _ => false
            };
        }

        static bool TargetMatchesWait(
            ProcessTokenInteractionTarget target,
            ProcessWaitState wait) =>
            target.WaitRegistrationId is null || target.WaitRegistrationId == wait.RegistrationId;

        static ProcessInputAdmissionDisposition Map(
            ProcessAwaitInputDisposition disposition,
            ProcessInputAdmissionDisposition reuse) => disposition switch
            {
                ProcessAwaitInputDisposition.Reject => ProcessInputAdmissionDisposition.Rejected,
                ProcessAwaitInputDisposition.Observe => ProcessInputAdmissionDisposition.Observed,
                ProcessAwaitInputDisposition.ReusePriorDisposition => reuse,
                _ => ProcessInputAdmissionDisposition.Rejected
            };

        ProcessInputAdmissionDisposition Map(
            ProcessAwaitInputDisposition disposition,
            ProcessWaitState wait,
            ProcessActivationInput input) => disposition switch
            {
                ProcessAwaitInputDisposition.Reject => ProcessInputAdmissionDisposition.Rejected,
                ProcessAwaitInputDisposition.Observe => ProcessInputAdmissionDisposition.Observed,
                ProcessAwaitInputDisposition.ReusePriorDisposition => PriorWinnerDisposition(wait, input),
                _ => ProcessInputAdmissionDisposition.Rejected
            };

        ProcessInputAdmissionDisposition Map(
            RequestResultDisposition disposition,
            ProcessWaitState wait,
            ProcessActivationInput input) => disposition switch
            {
                RequestResultDisposition.Reject => ProcessInputAdmissionDisposition.Rejected,
                RequestResultDisposition.Observe => ProcessInputAdmissionDisposition.Observed,
                RequestResultDisposition.ReusePriorDisposition => PriorWinnerDisposition(wait, input),
                _ => ProcessInputAdmissionDisposition.Rejected
            };

        static ProcessInputAdmissionDisposition Map(
            RequestResultDisposition disposition,
            ProcessInputAdmissionDisposition reuse) => disposition switch
            {
                RequestResultDisposition.Reject => ProcessInputAdmissionDisposition.Rejected,
                RequestResultDisposition.Observe => ProcessInputAdmissionDisposition.Observed,
                RequestResultDisposition.ReusePriorDisposition => reuse,
                _ => ProcessInputAdmissionDisposition.Rejected
            };

        static ProcessInputAdmissionDisposition Map(
            ProcessAwaitMissingTargetDisposition disposition) => disposition switch
            {
                ProcessAwaitMissingTargetDisposition.Reject => ProcessInputAdmissionDisposition.Rejected,
                ProcessAwaitMissingTargetDisposition.Observe => ProcessInputAdmissionDisposition.Observed,
                ProcessAwaitMissingTargetDisposition.DeadLetter => ProcessInputAdmissionDisposition.DeadLettered,
                _ => ProcessInputAdmissionDisposition.MissingTarget
            };

        ProcessInputReceipt Receipt(
            ProcessActivationInput input,
            ProcessInputAdmissionDisposition disposition,
            ProcessWaitRegistrationId? waitRegistrationId = null) => new(
            input,
            disposition,
            activation.ObservedAtUtc,
            waitRegistrationId);

        static bool SameInput(ProcessActivationInput left, ProcessActivationInput right) =>
            left.Target == right.Target
            && InteractionEnvelopeJsonSerializer.GetCanonicalBytes(left.Envelope).AsSpan().SequenceEqual(
                InteractionEnvelopeJsonSerializer.GetCanonicalBytes(right.Envelope));

        void UpsertActivationAdmission(ProcessInputReceipt receipt)
        {
            var index = activationAdmissions.FindIndex(candidate => candidate.Emission == receipt.Emission);
            if (index >= 0)
            {
                activationAdmissions[index] = receipt;
            }
            else
            {
                activationAdmissions.Add(receipt);
            }
        }

        void DispositionInput(
            ProcessBufferedInput buffered,
            ProcessInputAdmissionDisposition disposition,
            ProcessWaitRegistrationId? registrationId,
            string detail)
        {
            bufferedInputs.Remove(buffered);
            var receipt = Receipt(buffered.Input, disposition, registrationId);
            var index = receipts.FindIndex(candidate => candidate.Emission == receipt.Emission);
            if (index >= 0)
            {
                receipts[index] = receipt;
            }
            else
            {
                receipts.Add(receipt);
            }

            UpsertActivationAdmission(receipt);
            AddInputTrace(buffered.Input, receipt, detail);
        }

        void AddInputTrace(ProcessActivationInput input, ProcessInputReceipt receipt, string detail)
        {
            var token = tokens.FirstOrDefault(candidate => candidate.Id == input.Target.Token);
            AddTrace(
                ProcessTraceEventKind.InputAdmitted,
                token ?? new ProcessTokenState(
                    input.Target.Token,
                    plan.Definition.Entry,
                    ExecutionTokenDisposition.Waiting,
                    step: 0,
                    bindings: [],
                    requestObligations: [],
                    forkMembership: null,
                    failure: null),
                token?.Node ?? plan.Definition.Entry,
                emission: input.Envelope.Context.EmissionId,
                detail: $"{detail}:{receipt.Disposition}",
                inputDisposition: receipt.Disposition,
                waitRegistrationId: receipt.WaitRegistrationId);
        }

        void Cut(ExecutionNodeId node)
        {
            safePointNode = node;
            stopAtDurableCut = true;
        }

        void DeactivateWait(
            ProcessWaitState wait,
            ExecutionNodeId? winnerClause = null,
            EmissionId? winnerInput = null) => ReplaceWait(wait with
            {
                Active = false,
                WinnerClause = winnerClause,
                WinnerInput = winnerInput
            });

        void CancelLiveTokens(TokenId? except)
        {
            foreach (var tokenId in tokens
                         .Where(token => token.Id != except
                                         && token.Disposition is ExecutionTokenDisposition.Ready
                                             or ExecutionTokenDisposition.Active
                                             or ExecutionTokenDisposition.Waiting)
                         .Select(static token => token.Id)
                         .ToArray())
            {
                CancelToken(GetToken(tokenId));
            }
        }

        void CancelToken(ProcessTokenState token)
        {
            if (token.Disposition is not (ExecutionTokenDisposition.Ready
                or ExecutionTokenDisposition.Active
                or ExecutionTokenDisposition.Waiting))
            {
                return;
            }

            ReplaceToken(token with { Disposition = ExecutionTokenDisposition.Cancelled });
            CloseTokenWork(token.Id);
        }

        void CloseTokenWork(TokenId token)
        {
            foreach (var waitId in waits
                         .Where(wait => wait.Token == token && wait.Active)
                         .Select(static wait => wait.RegistrationId)
                         .ToArray())
            {
                DeactivateWait(GetWait(waitId));
            }
            requests.RemoveAll(request => request.Token == token);

            foreach (var buffered in bufferedInputs
                         .Where(candidate => candidate.Input.Target.Token == token)
                         .ToArray())
            {
                var tombstone = waits
                    .Where(wait => wait.Token == token
                                   && TargetMatchesWait(buffered.Input.Target, wait)
                                   && !wait.Active
                                   && WaitAccepts(wait, buffered.Input.Envelope))
                    .OrderByDescending(static wait => wait.RegisteredAtUtc)
                    .ThenByDescending(static wait => wait.RegistrationId.Value, StringComparer.Ordinal)
                    .FirstOrDefault();
                DispositionInput(
                    buffered,
                    tombstone is null
                        ? ProcessInputAdmissionDisposition.TerminalUnconsumed
                        : LateDisposition(tombstone, buffered.Input),
                    tombstone?.RegistrationId,
                    tombstone is null ? "terminal-unconsumed" : "terminal-late");
            }
        }

        void CloseAllTokenWork()
        {
            foreach (var token in tokens
                         .Select(static candidate => candidate.Id)
                         .OrderBy(static candidate => candidate.Value, StringComparer.Ordinal))
            {
                CloseTokenWork(token);
            }
        }

        ProcessTokenState GetToken(TokenId id) => tokens.Single(token => token.Id == id);

        ProcessWaitState GetWait(ProcessWaitRegistrationId id) =>
            waits.Single(wait => wait.RegistrationId == id);

        ProcessForkState GetFork(string id) => forks.Single(fork => string.Equals(fork.RegistrationId, id, StringComparison.Ordinal));

        void ReplaceToken(ProcessTokenState value)
        {
            var index = tokens.FindIndex(candidate => candidate.Id == value.Id);
            if (index < 0)
            {
                tokens.Add(value);
            }
            else
            {
                tokens[index] = value;
            }

            if (value.ForkMembership is null)
            {
                return;
            }

            var fork = GetFork(value.ForkMembership.RegistrationId);
            var branchIndex = -1;
            for (var candidateIndex = 0; candidateIndex < fork.Branches.Length; candidateIndex++)
            {
                if (fork.Branches[candidateIndex].Token != value.Id)
                {
                    continue;
                }

                branchIndex = candidateIndex;
                break;
            }
            if (branchIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Fork registration '{fork.RegistrationId}' does not contain token '{value.Id.Value}'.");
            }
            var branch = fork.Branches[branchIndex];
            var completionOrder = ((JoinProcessNode)plan.GetNode(fork.Join)).Policy.CompletionOrder;
            var completionSequence = completionOrder == ProcessJoinCompletionOrder.Observable
                ? branch.CompletionSequence
                : null;
            if (completionOrder == ProcessJoinCompletionOrder.Observable
                && completionSequence is null
                && value.Disposition is ExecutionTokenDisposition.Completed
                    or ExecutionTokenDisposition.Failed
                    or ExecutionTokenDisposition.Cancelled)
            {
                completionSequence = fork.Branches
                    .Where(static candidate => candidate.CompletionSequence.HasValue)
                    .Select(static candidate => candidate.CompletionSequence!.Value)
                    .DefaultIfEmpty()
                    .Max() + 1L;
            }
            var branches = fork.Branches.SetItem(
                branchIndex,
                branch with
                {
                    Disposition = value.Disposition,
                    CompletionSequence = completionSequence
                });
            ReplaceFork(fork with { Branches = branches });
        }

        void ReplaceWait(ProcessWaitState value)
        {
            var index = waits.FindIndex(candidate => candidate.RegistrationId == value.RegistrationId);
            if (index < 0)
            {
                waits.Add(value);
            }
            else
            {
                waits[index] = value;
            }
        }

        void ReplaceFork(ProcessForkState value)
        {
            var index = forks.FindIndex(candidate => string.Equals(candidate.RegistrationId, value.RegistrationId, StringComparison.Ordinal));
            if (index < 0)
            {
                forks.Add(value);
            }
            else
            {
                forks[index] = value;
            }
        }

        void AddTrace(
            ProcessTraceEventKind kind,
            ProcessTokenState token,
            ExecutionNodeId node,
            ExecutionNodeId? branchOrClause = null,
            EmissionId? emission = null,
            string? detail = null,
            InteractionEnvelopeContentFingerprint? emissionFingerprint = null,
            long? operationOccurrence = null,
            ProcessInputAdmissionDisposition? inputDisposition = null,
            ProcessWaitRegistrationId? waitRegistrationId = null)
        {
            var location = nodeIndexes.TryGetValue(node, out var index) ? $"/nodes/{index}" : null;
            trace.Add(new(
                trace.Count,
                kind,
                plan.DefinitionReference,
                original.Continuation,
                activation.Id,
                token.Id,
                node,
                branchOrClause,
                emission,
                detail,
                plan.Document.Metadata.SourceMap.ResolveReferences(
                    location,
                    plan.Document.Metadata.Provenance.Source.Reference),
                emissionFingerprint,
                operationOccurrence,
                inputDisposition,
                waitRegistrationId));
        }

        DocumentValidationDiagnostic Diagnostic(
            string code,
            string message,
            ExecutionNodeId? node) => new(
            code,
            DiagnosticSeverity.Error,
            message,
            node is { } nodeId && nodeIndexes.TryGetValue(nodeId, out var index)
                ? $"/definition/nodes/{index}"
                : "/activation",
            Evidence: new(
                stage: "processReferenceInterpretation",
                subject: node?.Value,
                sourceReferences: node is { } sourceNode && nodeIndexes.TryGetValue(sourceNode, out var sourceIndex)
                    ? plan.Document.Metadata.SourceMap.ResolveReferences(
                        $"/nodes/{sourceIndex}",
                        plan.Document.Metadata.Provenance.Source.Reference)
                    : [plan.Document.Metadata.Provenance.Source.Reference]));

        ProcessActivationDecision Rejected(DocumentValidationDiagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
            return new(
                ProcessActivationDisposition.Rejected,
                original,
                emissions: [],
                inputAdmissions: [],
                [diagnostic],
                new(
                    plan.DefinitionReference,
                    activation.Id,
                    activation.Cause,
                    SafePointNode: null,
                    Trace: []));
        }

        ProcessActivationDecision CompleteDecision(ProcessActivationDisposition disposition)
        {
            safePointNode ??= waits
                .Where(static wait => wait.Active)
                .OrderBy(static wait => wait.Token.Value, StringComparer.Ordinal)
                .ThenBy(static wait => wait.Node.Value, StringComparer.Ordinal)
                .Select(static wait => (ExecutionNodeId?)wait.Node)
                .FirstOrDefault()
                ?? tokens
                    .OrderBy(static token => token.Id.Value, StringComparer.Ordinal)
                    .Where(static token => token.Disposition is ExecutionTokenDisposition.Completed
                        or ExecutionTokenDisposition.Failed
                        or ExecutionTokenDisposition.Cancelled)
                    .Select(static token => (ExecutionNodeId?)token.Node)
                    .FirstOrDefault();
            var state = new ProcessContinuationState(
                plan.DefinitionReference,
                original.Continuation,
                original.CompletedActivationCount + 1,
                [.. tokens.OrderBy(static token => token.Id.Value, StringComparer.Ordinal)],
                [.. forks.OrderBy(static fork => fork.RegistrationId, StringComparer.Ordinal)],
                [.. waits.OrderBy(static wait => wait.RegistrationId.Value, StringComparer.Ordinal)],
                [.. bufferedInputs
                    .OrderBy(static input => input.Input.Envelope.Context.EmissionId.Value, StringComparer.Ordinal)],
                [.. receipts.OrderBy(static receipt => receipt.Emission.Value, StringComparer.Ordinal)],
                [.. requests.OrderBy(static request => request.Emission.Value, StringComparer.Ordinal)],
                terminal);
            return new(
                disposition,
                state,
                [.. emissions],
                [.. activationAdmissions],
                [.. diagnostics.OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal)],
                new(
                    plan.DefinitionReference,
                    activation.Id,
                    activation.Cause,
                    safePointNode,
                    [.. trace]));
        }

        ProcessActivationDisposition DispositionFromTerminal() => terminal.Kind switch
        {
            ExecutionTerminalOutcomeKind.Completed => ProcessActivationDisposition.Completed,
            ExecutionTerminalOutcomeKind.Failed => ProcessActivationDisposition.Failed,
            ExecutionTerminalOutcomeKind.Cancelled => ProcessActivationDisposition.Cancelled,
            ExecutionTerminalOutcomeKind.Terminated => ProcessActivationDisposition.Failed,
            _ => ProcessActivationDisposition.Quiescent
        };

        sealed record AwaitCandidate(
            ProcessAwaitClause Clause,
            ProcessBufferedInput? Input,
            PortableValue? Value);
    }
}

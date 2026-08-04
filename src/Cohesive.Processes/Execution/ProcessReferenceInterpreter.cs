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

    /// <summary>Canonical control flow reached an authored failed terminal.</summary>
    public const string AuthoredFailure = "processes.execution.authored.failed";

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
            children: [],
            partitions: [],
            recurrences: [],
            waits: [],
            bufferedInputs: [],
            inputReceipts: [],
            outstandingRequests: [],
            terminal: new(ExecutionTerminalOutcomeKind.None));
    }

    /// <summary>Creates the clean replacement attempt required by a restart-on-recovery Process definition.</summary>
    /// <param name="plan">Successfully compiled exact Process plan.</param>
    /// <param name="abandoned">Interrupted continuation whose attempt must not resume.</param>
    /// <param name="replacementAttempt">New stable attempt identity allocated by the controlling runtime.</param>
    /// <returns>
    /// Initial state for the same Process instance and definition under <paramref name="replacementAttempt"/>.
    /// Tokens, Forks, child invocations, partition work, recurrences, waits, receipts, Requests, and terminal state
    /// from <paramref name="abandoned"/> are not copied.
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

        if (diagnostic is null)
        {
            foreach (var point in activation.AdmissionOperatingPoints)
            {
                var node = plan.Definition.Nodes.FirstOrDefault(candidate => candidate.Id == point.Node);
                if (node is not ForkProcessNode fork)
                {
                    diagnostic = ActivationDiagnostic(
                        plan,
                        ProcessExecutionDiagnosticCodes.ActivationInvalid,
                        $"Admission operating point targets non-Fork node '{point.Node.Value}'.");
                    break;
                }
                if (point.MaximumParallelism < fork.Limits.MinimumParallelism
                    || point.MaximumParallelism > fork.Limits.MaximumParallelism)
                {
                    diagnostic = ActivationDiagnostic(
                        plan,
                        ProcessExecutionDiagnosticCodes.ActivationInvalid,
                        $"Admission operating point for Fork '{point.Node.Value}' lies outside its canonical parallelism bounds.");
                    break;
                }
            }
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
        readonly List<ProcessChildState> children;
        readonly List<ProcessPartitionState> partitions;
        readonly List<ProcessRecurrenceState> recurrences;
        readonly List<ProcessWaitState> waits;
        readonly List<ProcessBufferedInput> bufferedInputs;
        readonly List<ProcessInputReceipt> receipts;
        readonly List<ProcessOutstandingRequest> requests;
        readonly List<InteractionEnvelope> emissions = [];
        readonly List<ProcessInputReceipt> activationAdmissions = [];
        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly List<ProcessTraceEvent> trace = [];
        readonly Dictionary<string, int> partitionStarts = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> forkStarts = new(StringComparer.Ordinal);
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
            children = [.. state.Children];
            partitions = [.. state.Partitions];
            recurrences = [.. state.Recurrences];
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
            _ = AdmitForkBranches();
            while (!stopAtDurableCut && terminal.Kind == ExecutionTerminalOutcomeKind.None)
            {
                var ready = tokens
                    .Where(static token => token.Disposition == ExecutionTokenDisposition.Ready)
                    .OrderBy(static token => token.Id.Value, StringComparer.Ordinal)
                    .Select(static token => token.Id)
                    .ToArray();
                if (ready.Length == 0)
                {
                    var progressed = ResolveJoins();
                    progressed |= AdmitForkBranches();
                    progressed |= ResolvePartitions();
                    if (!progressed)
                    {
                        _ = CutForDeferredForkAdmission();
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
                    _ = AdmitForkBranches();
                    _ = ResolvePartitions();
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
            if (!validation.IsValid)
                return validation.Diagnostics[0];

            foreach (var point in activation.AdmissionOperatingPoints)
            {
                foreach (var fork in forks.Where(candidate => candidate.Fork == point.Node))
                {
                    var retained = fork.AdmissionOperatingPoint;
                    if (retained is null)
                        continue;
                    var authorityChanged = !string.Equals(
                        retained.Authority,
                        point.Authority,
                        StringComparison.Ordinal);
                    var canonicalMayYield = string.Equals(
                        retained.Authority,
                        ProcessAdmissionOperatingPoint.CanonicalAuthority,
                        StringComparison.Ordinal);
                    if (authorityChanged && !canonicalMayYield
                        || !authorityChanged && point.Revision < retained.Revision
                        || !authorityChanged && point.Revision == retained.Revision && point != retained)
                    {
                        return Diagnostic(
                            ProcessExecutionDiagnosticCodes.ActivationInvalid,
                            $"Admission operating point for Fork '{point.Node.Value}' conflicts with retained authority or revision evidence.",
                            point.Node);
                    }
                }
            }
            return null;
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
                    var conflict = Receipt(
                        input,
                        ProcessInputAdmissionDisposition.IdentityConflict,
                        ProcessInputAdmissionReason.IdentityConflict);
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
                    var rejected = Receipt(
                        input,
                        ProcessInputAdmissionDisposition.Rejected,
                        ProcessInputAdmissionReason.InvalidEnvelope);
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
                        var conflict = Receipt(
                            input,
                            ProcessInputAdmissionDisposition.IdentityConflict,
                            ProcessInputAdmissionReason.IdentityConflict);
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
                        Reason = ProcessInputAdmissionReason.Duplicate,
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
                        ProcessInputAdmissionReason.Stale,
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
                    var missing = Receipt(
                        input,
                        ProcessInputAdmissionDisposition.MissingTarget,
                        ProcessInputAdmissionReason.MissingTarget);
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
                        var missing = Receipt(
                            input,
                            ProcessInputAdmissionDisposition.MissingTarget,
                            ProcessInputAdmissionReason.MissingTarget);
                        UpsertActivationAdmission(missing);
                        receipts.Add(missing);
                        AddInputTrace(input, missing, "missing-wait-occurrence");
                        continue;
                    }

                    if (!addressedWait.Active)
                    {
                        var acceptedByWait = WaitAccepts(addressedWait, input.Envelope);
                        var disposition = acceptedByWait
                            ? LateDisposition(addressedWait, input)
                            : MissingDisposition(addressedWait)
                              ?? ProcessInputAdmissionDisposition.MissingTarget;
                        var closed = Receipt(
                            input,
                            disposition,
                            acceptedByWait
                                ? ProcessInputAdmissionReason.Late
                                : ProcessInputAdmissionReason.MissingTarget,
                            addressedWait.RegistrationId);
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
                        var ambiguous = Receipt(
                            input,
                            ProcessInputAdmissionDisposition.MissingTarget,
                            ProcessInputAdmissionReason.MissingTarget);
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
                            ProcessInputAdmissionReason.Late,
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
                    var missing = Receipt(
                        input,
                        resolvedMissingDisposition,
                        ProcessInputAdmissionReason.MissingTarget,
                        activeWait!.RegistrationId);
                    UpsertActivationAdmission(missing);
                    receipts.Add(missing);
                    AddInputTrace(input, missing, "incompatible-active-wait");
                    continue;
                }

                if (target.Disposition is ExecutionTokenDisposition.Completed
                    or ExecutionTokenDisposition.Failed
                    or ExecutionTokenDisposition.Cancelled)
                {
                    var missing = Receipt(
                        input,
                        ProcessInputAdmissionDisposition.MissingTarget,
                        ProcessInputAdmissionReason.MissingTarget);
                    UpsertActivationAdmission(missing);
                    receipts.Add(missing);
                    AddInputTrace(input, missing, "terminal-target");
                    continue;
                }

                var isWaitCandidate = activeWait is not null && WaitAccepts(activeWait, input.Envelope);
                var buffered = Receipt(
                    input,
                    ProcessInputAdmissionDisposition.Buffered,
                    isWaitCandidate
                        ? ProcessInputAdmissionReason.WaitCandidate
                        : ProcessInputAdmissionReason.Early,
                    isWaitCandidate
                        ? activeWait!.RegistrationId
                        : null);
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
                case ProcessWaitKind.PartitionBatch:
                    break;
                case ProcessWaitKind.RepeatAcrossActivation:
                    ResumeRecurrence(wait, token);
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
            var node = plan.GetNode(wait.Node);
            if (node is ForEachPartitionProcessNode partitionNode)
            {
                ResumePartitionRequest(wait, token, outstanding, partitionNode, candidates);
                return;
            }
            if (!ProcessRequestSemantics.TryProject(node, out var semantics))
            {
                FailToken(token, Diagnostic(
                    ProcessExecutionDiagnosticCodes.ContinuationInvalid,
                    "A persisted Request wait does not refer to a Request-bearing Process node.",
                    wait.Node));
                return;
            }
            foreach (var candidate in candidates)
            {
                var reply = (ReplyEnvelope)candidate.Input.Envelope;
                var branch = semantics.Outcomes.FirstOrDefault(outcome => outcome.Outcome == reply.Outcome.Id);
                if (branch is null
                    || !ReplyMatchesRequest(reply, semantics.Contract)
                    || !ReplyMatchesChildRequest(token.Id, node.Id, outstanding.Emission, reply))
                {
                    DispositionInput(
                        candidate,
                        ProcessInputAdmissionDisposition.Rejected,
                        ProcessInputAdmissionReason.ContractMismatch,
                        wait.RegistrationId,
                        "request-result-rejected");
                    continue;
                }

                DispositionInput(
                    candidate,
                    ProcessInputAdmissionDisposition.Consumed,
                    ProcessInputAdmissionReason.Consumed,
                    wait.RegistrationId,
                    "request-result");
                requests.Remove(outstanding);
                DeactivateWait(wait, winnerClause: branch.Id, winnerInput: reply.Context.EmissionId);
                ResolveChildResult(token, node.Id, outstanding.Emission, semantics.Contract, reply);
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

        void ResumePartitionRequest(
            ProcessWaitState wait,
            ProcessTokenState token,
            ProcessOutstandingRequest outstanding,
            ForEachPartitionProcessNode node,
            IReadOnlyList<ProcessBufferedInput> candidates)
        {
            foreach (var candidate in candidates)
            {
                var reply = (ReplyEnvelope)candidate.Input.Envelope;
                if (!ReplyMatchesRequest(reply, node.Contract)
                    || !ReplyMatchesChildRequest(token.Id, node.Id, outstanding.Emission, reply))
                {
                    DispositionInput(
                        candidate,
                        ProcessInputAdmissionDisposition.Rejected,
                        ProcessInputAdmissionReason.ContractMismatch,
                        wait.RegistrationId,
                        "partition-result-rejected");
                    continue;
                }

                DispositionInput(
                    candidate,
                    ProcessInputAdmissionDisposition.Consumed,
                    ProcessInputAdmissionReason.Consumed,
                    wait.RegistrationId,
                    "partition-result");
                requests.Remove(outstanding);
                DeactivateWait(wait, winnerInput: reply.Context.EmissionId);
                ResolveChildResult(token, node.Id, outstanding.Emission, node.Contract, reply);
                ReplaceToken(token with
                {
                    Disposition = ExecutionTokenDisposition.Completed,
                    Step = token.Step + 1
                });
                AddTrace(
                    ProcessTraceEventKind.WaitResolved,
                    token,
                    node.Id,
                    emission: reply.Context.EmissionId,
                    detail: "partition-request-reply");
                DispositionOtherRequestResults(
                    token.Id,
                    outstanding.Emission,
                    GetWait(wait.RegistrationId));
                return;
            }
        }

        void ResolveChildResult(
            ProcessTokenState token,
            ExecutionNodeId node,
            EmissionId request,
            RequestContractReference contract,
            ReplyEnvelope reply)
        {
            var matchingChildren = children.Where(candidate =>
                candidate.Token == token.Id
                && candidate.Node == node
                && candidate.RequestEmission == request).Take(2).ToArray();
            if (matchingChildren.Length == 0
                && !ProcessRequestSemantics.TryProjectChild(plan.GetNode(node), out _))
            {
                return;
            }
            if (matchingChildren is not [var child])
            {
                throw new InvalidOperationException(
                    $"Child Request '{request.Value}' does not map to exactly one child occurrence.");
            }

            var requestContract = ResolveContract<RequestContractDefinition>(contract, node);
            _ = requestContract.Response.Find(reply.Outcome.Id)
                ?? throw new InvalidOperationException("A child Reply outcome is absent from its Request contract.");
            if (!ProcessRequestSemantics.TryProjectChild(plan.GetNode(node), out var semantics)
                || !semantics.OutcomeMapping.Contains(reply.Outcome.Id))
            {
                throw new InvalidOperationException("A child Reply outcome is absent from its authored terminal mapping.");
            }
            var completed = reply.Outcome.Id == semantics.OutcomeMapping.Completed;
            ReplaceChild(child with
            {
                Disposition = completed
                    ? ProcessChildDisposition.Completed
                    : ProcessChildDisposition.Failed,
                TerminalOutcome = reply.Outcome.Id,
                Result = reply.Outcome.Value
            });
            AddTrace(
                ProcessTraceEventKind.ChildResolved,
                token,
                node,
                emission: reply.Context.EmissionId,
                detail: completed ? "completed" : "failed");
        }

        void ResumeRecurrence(ProcessWaitState wait, ProcessTokenState token)
        {
            var node = (RepeatAcrossActivationProcessNode)plan.GetNode(wait.Node);
            DeactivateWait(wait);
            Resume(token, node.Repeat, output: null);
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

        bool ReplyMatchesChildRequest(
            TokenId token,
            ExecutionNodeId node,
            EmissionId request,
            ReplyEnvelope reply)
        {
            var matchingChildren = children.Where(candidate =>
                candidate.Token == token
                && candidate.Node == node
                && candidate.RequestEmission == request).Take(2).ToArray();
            if (matchingChildren.Length == 0)
            {
                return !ProcessRequestSemantics.TryProjectChild(plan.GetNode(node), out _);
            }
            if (matchingChildren is not [var child])
            {
                return false;
            }

            return ProcessRequestSemantics.TryProjectChild(plan.GetNode(node), out var semantics)
                && semantics.OutcomeMapping.Contains(reply.Outcome.Id)
                && reply.Context.Origin is ProcessInteractionOrigin origin
                && origin.Definition == child.Process
                && origin.Continuation == child.Continuation;
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
                    ProcessInputAdmissionReason.Late,
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
                case InvokeProcessProcessNode child:
                    ExecuteRequest(token, child);
                    break;
                case ForEachPartitionProcessNode partition:
                    ExecutePartitionBatch(token, partition);
                    break;
                case RepeatAcrossActivationProcessNode recurrence:
                    ExecuteRecurrence(token, recurrence);
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

        void ExecuteRequest(ProcessTokenState token, CanonicalProcessNode node)
        {
            if (!ProcessRequestSemantics.TryProject(node, out var semantics))
            {
                throw new InvalidOperationException(
                    $"Node '{node.Id.Value}' does not carry shared Request semantics.");
            }

            var contract = ResolveContract<RequestContractDefinition>(semantics.Contract, node.Id);
            var payload = EvaluateTyped(semantics.Payload, contract.Payload.Contract, token);
            var occurrence = token.Step;
            var emissionId = ProcessReferenceIdentities.Emission(
                original.Continuation,
                activation.Id,
                token.Id,
                node.Id,
                occurrence);
            ProcessChildRequestTarget? childTarget = null;
            if (semantics.ChildProcess is { } childProcess)
            {
                var registration = ProcessReferenceIdentities.ChildRegistration(
                    original.Continuation,
                    token.Id,
                    node.Id,
                    occurrence,
                    progressIdentity: null);
                var childContinuation = ProcessReferenceIdentities.ChildContinuation(
                    original.Continuation,
                    token.Id,
                    node.Id,
                    occurrence,
                    progressIdentity: null,
                    childProcess);
                childTarget = new(
                    childProcess,
                    childContinuation,
                    semantics.ChildOutcomeMapping
                    ?? throw new InvalidOperationException("Child Request semantics require an exact outcome mapping."),
                    ownerToken: token.Id,
                    occurrence,
                    progressIdentity: null);
                children.Add(new(
                    registration,
                    token.Id,
                    token.Id,
                    node.Id,
                    occurrence,
                    progressIdentity: null,
                    childProcess,
                    childContinuation,
                    semantics.ChildPurpose,
                    semantics.ChildCancellation,
                    ProcessChildDisposition.Active,
                    emissionId));
                AddTrace(ProcessTraceEventKind.ChildRegistered, token, node.Id, detail: registration);
            }

            EmitRequest(token, node.Id, semantics.Contract, payload, emissionId, childTarget);
        }

        void EmitRequest(
            ProcessTokenState token,
            ExecutionNodeId node,
            RequestContractReference contract,
            PortableValue payload,
            EmissionId emissionId,
            ProcessChildRequestTarget? childTarget = null)
        {
            var wait = RegisterWait(
                token,
                node,
                ProcessWaitKind.Request,
                timers: [],
                obligationEmission: emissionId);
            var envelope = new RequestEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                EnvelopeContext(token, node, emissionId, activation.Context.CausationId),
                contract,
                payload,
                new ProcessTokenInteractionTarget(original.Continuation, token.Id, wait.RegistrationId),
                childTarget);
            emissions.Add(envelope);
            requests.Add(new(token.Id, node, emissionId, contract, activation.ObservedAtUtc));
            AddTrace(
                ProcessTraceEventKind.InteractionEmitted,
                token,
                node,
                emission: emissionId,
                detail: "request",
                emissionFingerprint: InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope));
            AddTrace(ProcessTraceEventKind.WaitRegistered, token, node, detail: wait.RegistrationId.Value);
            Cut(node);
        }

        void ExecutePartitionBatch(ProcessTokenState token, ForEachPartitionProcessNode node)
        {
            var occurrence = token.Step;
            var evaluated = Evaluate(node.Partitions, token).RequireConcrete("ForEachPartition partitions");
            if (evaluated.Kind != ObservationValueKind.Array || evaluated.Array.IsDefault)
            {
                throw new InvalidOperationException(
                    $"ForEachPartition node '{node.Id.Value}' requires one concrete finite Array value.");
            }
            if (evaluated.Array.Length > node.Limits.MaximumItems)
            {
                throw new InvalidOperationException(
                    $"ForEachPartition node '{node.Id.Value}' produced {evaluated.Array.Length} items, exceeding its explicit maximum of {node.Limits.MaximumItems}.");
            }

            var registration = ProcessReferenceIdentities.PartitionRegistration(
                original.Continuation,
                token.Id,
                node.Id,
                occurrence);
            var capacityDomains = node.CapacityIdentity is null
                ? null
                : node.CapacityDomains.ToDictionary(
                    static domain => domain.Identity,
                    static domain => domain.MaximumParallelism,
                    StringComparer.Ordinal);
            List<(string ProgressIdentity, string? CapacityIdentity, PortableValue Partition)> evaluatedWork =
                new(evaluated.Array.Length);
            foreach (var item in evaluated.Array)
            {
                var partition = ValidateValue(
                    PortableExpressionValue.FromObservation(item).ToPortable(node.Partition.Contract),
                    node.Partition.Contract,
                    node.Id);
                var itemToken = Bind(token, node.Partition, partition);
                var progress = Evaluate(node.ProgressIdentity, itemToken)
                    .RequireConcrete("ForEachPartition progress identity");
                if (progress.Kind != ObservationValueKind.String
                    || string.IsNullOrWhiteSpace(progress.String))
                {
                    throw new InvalidOperationException(
                        $"ForEachPartition node '{node.Id.Value}' requires every progress identity to be a non-empty String.");
                }
                string? capacityIdentity = null;
                if (node.CapacityIdentity is not null)
                {
                    var capacity = Evaluate(node.CapacityIdentity, itemToken)
                        .RequireConcrete("ForEachPartition capacity identity");
                    if (capacity.Kind != ObservationValueKind.String
                        || string.IsNullOrWhiteSpace(capacity.String))
                    {
                        throw new InvalidOperationException(
                            $"ForEachPartition node '{node.Id.Value}' requires every capacity identity to be a non-empty String.");
                    }
                    if (capacityDomains is null || !capacityDomains.ContainsKey(capacity.String))
                    {
                        throw new InvalidOperationException(
                            $"ForEachPartition node '{node.Id.Value}' produced undeclared capacity identity '{capacity.String}'.");
                    }
                    capacityIdentity = capacity.String;
                }
                evaluatedWork.Add((progress.String, capacityIdentity, partition));
            }

            evaluatedWork.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.ProgressIdentity, right.ProgressIdentity));
            for (var index = 1; index < evaluatedWork.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(
                        evaluatedWork[index - 1].ProgressIdentity,
                        evaluatedWork[index].ProgressIdentity))
                {
                    throw new InvalidOperationException(
                        $"ForEachPartition node '{node.Id.Value}' produced duplicate progress identity '{evaluatedWork[index].ProgressIdentity}'.");
                }
            }

            var work = ImmutableArray.CreateBuilder<ProcessPartitionWorkState>(evaluatedWork.Count);
            foreach (var (progressIdentity, capacityIdentity, partition) in evaluatedWork)
            {
                var childRegistration = ProcessReferenceIdentities.ChildRegistration(
                    original.Continuation,
                    token.Id,
                    node.Id,
                    occurrence,
                    progressIdentity);
                var childToken = ProcessReferenceIdentities.PartitionToken(
                    original.Continuation,
                    token.Id,
                    node.Id,
                    occurrence,
                    progressIdentity);
                children.Add(new(
                    childRegistration,
                    token.Id,
                    childToken,
                    node.Id,
                    occurrence,
                    progressIdentity,
                    node.Process,
                    ProcessReferenceIdentities.ChildContinuation(
                        original.Continuation,
                        token.Id,
                        node.Id,
                        occurrence,
                        progressIdentity,
                        node.Process),
                    ProcessChildPurpose.Work,
                    node.Cancellation,
                    ProcessChildDisposition.Pending));
                work.Add(new(progressIdentity, capacityIdentity, partition, childRegistration));
                AddTrace(
                    ProcessTraceEventKind.ChildRegistered,
                    token,
                    node.Id,
                    detail: childRegistration);
            }

            partitions.Add(new(
                registration,
                token.Id,
                node.Id,
                occurrence,
                work.MoveToImmutable(),
                resolved: false));
            var batchWait = RegisterWait(token, node.Id, ProcessWaitKind.PartitionBatch, timers: []);
            AddTrace(
                ProcessTraceEventKind.WaitRegistered,
                token,
                node.Id,
                detail: batchWait.RegistrationId.Value);
            AddTrace(ProcessTraceEventKind.PartitionBatchChanged, token, node.Id, detail: registration);
            StartPartitionChildren(GetPartition(registration), node);
            Cut(node.Id);
        }

        bool ResolvePartitions()
        {
            var progressed = false;
            foreach (var registration in partitions
                         .Where(static partition => !partition.Resolved)
                         .OrderBy(static partition => partition.RegistrationId, StringComparer.Ordinal)
                         .Select(static partition => partition.RegistrationId)
                         .ToArray())
            {
                var partition = GetPartition(registration);
                var node = (ForEachPartitionProcessNode)plan.GetNode(partition.Node);
                var owner = GetToken(partition.Owner);
                if (owner.Disposition != ExecutionTokenDisposition.Waiting)
                {
                    continue;
                }

                var partitionProgressed = false;
                ExecuteGuarded(
                    owner,
                    node.Id,
                    () => partitionProgressed = ResolvePartition(partition, node, owner));
                progressed |= partitionProgressed;
                if (terminal.Kind != ExecutionTerminalOutcomeKind.None || stopAtDurableCut)
                {
                    break;
                }
            }
            return progressed;
        }

        bool ResolvePartition(
            ProcessPartitionState partition,
            ForEachPartitionProcessNode node,
            ProcessTokenState owner)
        {
            var members = partition.Work
                .Select(work => GetChild(work.ChildRegistrationId))
                .ToArray();
            var failed = members.Any(static child => child.Disposition is
                ProcessChildDisposition.Failed
                or ProcessChildDisposition.CancellationRequested
                or ProcessChildDisposition.Detached
                or ProcessChildDisposition.CancelledBeforeStart);
            if (failed && node.Failure == ProcessPartitionFailurePolicy.FailFast)
            {
                foreach (var member in members.Where(static child => child.Disposition is
                             ProcessChildDisposition.Pending or ProcessChildDisposition.Active))
                {
                    CancelChild(member, owner);
                }
                ResolvePartitionOwner(partition, owner, node, successful: false);
                return true;
            }

            var started = StartPartitionChildren(partition, node);
            members = partition.Work
                .Select(work => GetChild(work.ChildRegistrationId))
                .ToArray();
            if (members.Any(static child => child.Disposition is
                ProcessChildDisposition.Pending or ProcessChildDisposition.Active))
            {
                return started;
            }
            var successful = members.All(static child =>
                child.Disposition == ProcessChildDisposition.Completed);
            ResolvePartitionOwner(partition, owner, node, successful);
            return true;
        }

        void ResolvePartitionOwner(
            ProcessPartitionState partition,
            ProcessTokenState owner,
            ForEachPartitionProcessNode node,
            bool successful)
        {
            var wait = waits.Single(candidate =>
                candidate.Active
                && candidate.Kind == ProcessWaitKind.PartitionBatch
                && candidate.Token == partition.Owner
                && candidate.Node == partition.Node);
            DeactivateWait(wait);
            ReplacePartition(partition with { Resolved = true });
            AddTrace(
                ProcessTraceEventKind.PartitionBatchChanged,
                owner,
                node.Id,
                detail: successful ? "completed" : "failed");
            Resume(owner, successful ? node.Completed : node.Failed, output: null);
        }

        bool StartPartitionChildren(
            ProcessPartitionState partition,
            ForEachPartitionProcessNode node)
        {
            var active = partition.Work.Count(work =>
                GetChild(work.ChildRegistrationId).Disposition == ProcessChildDisposition.Active);
            var capacity = node.Limits.MaximumParallelism - active;
            var alreadyStarted = partitionStarts.GetValueOrDefault(partition.RegistrationId);
            var activationCapacity = node.Limits.MaximumStartsPerActivation - alreadyStarted;
            var startCount = Math.Min(capacity, activationCapacity);
            if (startCount <= 0)
            {
                return false;
            }

            var owner = GetToken(partition.Owner);
            var contract = ResolveContract<RequestContractDefinition>(node.Contract, node.Id);
            var admitted = new List<(ProcessPartitionWorkState Work, ProcessChildState Child)>(startCount);
            Dictionary<string, int>? activeByDomain = null;
            Dictionary<string, int>? capacityLimits = null;
            if (node.CapacityIdentity is not null)
            {
                capacityLimits = node.CapacityDomains.ToDictionary(
                    static domain => domain.Identity,
                    static domain => domain.MaximumParallelism,
                    StringComparer.Ordinal);
                activeByDomain = new(StringComparer.Ordinal);
                foreach (var work in partition.Work)
                {
                    if (GetChild(work.ChildRegistrationId).Disposition != ProcessChildDisposition.Active)
                        continue;
                    var capacityIdentity = work.CapacityIdentity
                        ?? throw new InvalidOperationException(
                            $"Capacity-bound partition work '{work.ProgressIdentity}' has no retained capacity identity.");
                    activeByDomain.TryGetValue(capacityIdentity, out var count);
                    activeByDomain[capacityIdentity] = checked(count + 1);
                }
            }

            foreach (var work in partition.Work)
            {
                if (admitted.Count == startCount)
                    break;
                var child = GetChild(work.ChildRegistrationId);
                if (child.Disposition != ProcessChildDisposition.Pending)
                    continue;
                if (capacityLimits is not null && activeByDomain is not null)
                {
                    var capacityIdentity = work.CapacityIdentity
                        ?? throw new InvalidOperationException(
                            $"Capacity-bound partition work '{work.ProgressIdentity}' has no retained capacity identity.");
                    if (!capacityLimits.TryGetValue(capacityIdentity, out var limit))
                    {
                        throw new InvalidOperationException(
                            $"Partition work '{work.ProgressIdentity}' names undeclared capacity domain '{capacityIdentity}'.");
                    }
                    activeByDomain.TryGetValue(capacityIdentity, out var count);
                    if (count >= limit)
                        continue;
                    activeByDomain[capacityIdentity] = checked(count + 1);
                }
                admitted.Add((work, child));
            }
            var prepared = new List<(
                ProcessChildState Child,
                ProcessTokenState Token,
                PortableValue Payload,
                EmissionId Emission)>(admitted.Count);
            foreach (var (work, child) in admitted)
            {
                var bound = Bind(owner, node.Partition, work.Partition);
                var childToken = new ProcessTokenState(
                    child.Token,
                    node.Id,
                    ExecutionTokenDisposition.Active,
                    step: 0,
                    bound.Bindings,
                    requestObligations: [],
                    forkMembership: null,
                    failure: null);
                var payload = EvaluateTyped(node.ChildInput, contract.Payload.Contract, childToken);
                var emission = ProcessReferenceIdentities.Emission(
                    original.Continuation,
                    activation.Id,
                    childToken.Id,
                    node.Id,
                    childToken.Step);
                prepared.Add((child, childToken, payload, emission));
            }

            foreach (var start in prepared)
            {
                tokens.Add(start.Token);
                ReplaceChild(start.Child with
                {
                    Disposition = ProcessChildDisposition.Active,
                    RequestEmission = start.Emission
                });
                EmitRequest(
                    start.Token,
                    node.Id,
                    node.Contract,
                    start.Payload,
                    start.Emission,
                    new(
                        start.Child.Process,
                        start.Child.Continuation,
                        node.OutcomeMapping,
                        start.Child.Owner,
                        start.Child.Occurrence,
                        start.Child.ProgressIdentity));
            }

            if (prepared.Count > 0)
            {
                partitionStarts[partition.RegistrationId] = alreadyStarted + prepared.Count;
                AddTrace(
                    ProcessTraceEventKind.PartitionBatchChanged,
                    owner,
                    node.Id,
                    detail: $"started:{prepared.Count}");
                return true;
            }
            return false;
        }

        void ExecuteRecurrence(ProcessTokenState token, RepeatAcrossActivationProcessNode node)
        {
            var recurrence = recurrences.SingleOrDefault(candidate =>
                candidate.Active
                && candidate.Token == token.Id
                && candidate.Node == node.Id);
            if (!EvaluateBoolean(node.ContinueWhen, token, "RepeatAcrossActivation predicate"))
            {
                if (recurrence is not null)
                {
                    ReplaceRecurrence(recurrence with { Active = false });
                }
                Advance(token, node.Completed);
                return;
            }

            if (recurrence is not null
                && recurrence.RepeatCount >= node.Policy.MaximumOccurrences)
            {
                ReplaceRecurrence(recurrence with { Active = false });
                Advance(token, node.Exhausted);
                return;
            }
            var progress = EvaluateTyped(node.Progress, node.ProgressContract, token);
            recurrence ??= CreateRecurrence(token, node);
            var repeatCount = recurrence.RepeatCount + 1;

            var unchanged = recurrence.LastProgress is not null && recurrence.LastProgress == progress
                ? (long)recurrence.UnchangedProgressCount + 1L
                : 0L;
            var retainedUnchanged = unchanged > int.MaxValue ? int.MaxValue : (int)unchanged;
            if (unchanged > node.Policy.MaximumUnchangedProgressOccurrences)
            {
                ReplaceRecurrence(recurrence with
                {
                    UnchangedProgressCount = retainedUnchanged,
                    LastProgress = progress,
                    Active = false
                });
                Advance(token, node.Stalled);
                return;
            }

            ReplaceRecurrence(recurrence with
            {
                RepeatCount = repeatCount,
                UnchangedProgressCount = retainedUnchanged,
                LastProgress = progress
            });
            var wait = RegisterWait(
                token,
                node.Id,
                ProcessWaitKind.RepeatAcrossActivation,
                timers: []);
            AddTrace(ProcessTraceEventKind.WaitRegistered, token, node.Id, detail: wait.RegistrationId.Value);
            AddTrace(
                ProcessTraceEventKind.RecurrenceAdvanced,
                token,
                node.Id,
                detail: $"repeat:{repeatCount};unchanged:{retainedUnchanged}");
            Cut(node.Id);
        }

        ProcessRecurrenceState CreateRecurrence(
            ProcessTokenState token,
            RepeatAcrossActivationProcessNode node)
        {
            var recurrence = new ProcessRecurrenceState(
                ProcessReferenceIdentities.RecurrenceRegistration(
                    original.Continuation,
                    token.Id,
                    node.Id,
                    token.Step),
                token.Id,
                node.Id,
                token.Step,
                repeatCount: 0,
                unchangedProgressCount: 0,
                lastProgress: null,
                active: true);
            recurrences.Add(recurrence);
            return recurrence;
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
                    ExecutionTokenDisposition.Pending,
                    step: 0,
                    token.Bindings,
                    token.RequestObligations,
                    new(registrationId, branch.Id),
                    failure: null);
                tokens.Add(child);
                branchStates.Add(new(branch.Id, childId, ExecutionTokenDisposition.Pending));
            }
            var operatingPoint = activation.AdmissionOperatingPoints.FirstOrDefault(point => point.Node == node.Id)
                ?? ProcessAdmissionOperatingPoint.Canonical(
                    node.Id,
                    node.Limits.MaximumParallelism,
                    plan.Document.Metadata.Provenance.Source.Reference);
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
                resolved: false,
                operatingPoint));
            ReplaceToken(token with
            {
                Node = node.Join,
                Disposition = ExecutionTokenDisposition.Waiting,
                Step = token.Step + 1
            });
            AddTrace(ProcessTraceEventKind.ForkCreated, token, node.Id, detail: registrationId);
        }

        bool AdmitForkBranches()
        {
            var progressed = false;
            foreach (var registrationId in forks
                         .Where(ForkMayAdmit)
                         .Where(static fork => fork.Branches.Any(branch =>
                             branch.Disposition == ExecutionTokenDisposition.Pending))
                         .OrderBy(static fork => fork.RegistrationId, StringComparer.Ordinal)
                         .Select(static fork => fork.RegistrationId)
                         .ToArray())
            {
                var fork = GetFork(registrationId);
                var node = (ForkProcessNode)plan.GetNode(fork.Fork);
                var selected = activation.AdmissionOperatingPoints.FirstOrDefault(point => point.Node == fork.Fork);
                if (selected is not null && selected != fork.AdmissionOperatingPoint)
                {
                    fork = fork with { AdmissionOperatingPoint = selected };
                    ReplaceFork(fork);
                    AddTrace(
                        ProcessTraceEventKind.ForkAdmissionChanged,
                        GetToken(fork.Owner),
                        node.Id,
                        detail: $"operating-point:{selected.MaximumParallelism}:{selected.Revision}:{selected.Authority}");
                    progressed = true;
                }

                var active = fork.Branches.Count(static branch => IsAdmittedAndNonterminal(branch.Disposition));
                var parallelismCapacity = fork.AdmissionOperatingPoint.MaximumParallelism - active;
                var alreadyStarted = forkStarts.GetValueOrDefault(fork.RegistrationId);
                var activationCapacity = node.Limits.MaximumStartsPerActivation - alreadyStarted;
                var startCount = Math.Min(parallelismCapacity, activationCapacity);
                if (startCount <= 0)
                    continue;

                Dictionary<string, int>? capacityLimits = null;
                Dictionary<string, int>? activeByDomain = null;
                if (!node.CapacityDomains.IsEmpty)
                {
                    capacityLimits = node.CapacityDomains.ToDictionary(
                        static domain => domain.Identity,
                        static domain => domain.MaximumParallelism,
                        StringComparer.Ordinal);
                    activeByDomain = new(StringComparer.Ordinal);
                    foreach (var branch in fork.Branches.Where(static branch =>
                                 IsAdmittedAndNonterminal(branch.Disposition)))
                    {
                        var domain = node.Branches.Single(candidate => candidate.Id == branch.Branch).CapacityDomain;
                        if (domain is null)
                            continue;
                        activeByDomain.TryGetValue(domain, out var count);
                        activeByDomain[domain] = checked(count + 1);
                    }
                }

                List<ProcessForkBranchState> admitted = new(startCount);
                foreach (var branch in fork.Branches)
                {
                    if (admitted.Count == startCount)
                        break;
                    if (branch.Disposition != ExecutionTokenDisposition.Pending)
                        continue;
                    var domain = node.Branches.Single(candidate => candidate.Id == branch.Branch).CapacityDomain;
                    if (domain is not null && capacityLimits is not null && activeByDomain is not null)
                    {
                        activeByDomain.TryGetValue(domain, out var count);
                        if (count >= capacityLimits[domain])
                            continue;
                        activeByDomain[domain] = checked(count + 1);
                    }
                    admitted.Add(branch);
                }

                foreach (var branch in admitted)
                {
                    var child = GetToken(branch.Token) with { Disposition = ExecutionTokenDisposition.Ready };
                    ReplaceToken(child);
                    active++;
                    AddTrace(
                        ProcessTraceEventKind.ForkAdmissionChanged,
                        child,
                        node.Id,
                        branch.Branch,
                        detail: $"admitted:active={active};limit={fork.AdmissionOperatingPoint.MaximumParallelism}");
                }
                if (admitted.Count > 0)
                {
                    forkStarts[fork.RegistrationId] = alreadyStarted + admitted.Count;
                    progressed = true;
                }
            }
            return progressed;
        }

        bool CutForDeferredForkAdmission()
        {
            foreach (var fork in forks
                         .Where(ForkMayAdmit)
                         .Where(static candidate => candidate.Branches.Any(branch =>
                             branch.Disposition == ExecutionTokenDisposition.Pending))
                         .OrderBy(static candidate => candidate.RegistrationId, StringComparer.Ordinal))
            {
                if (fork.Branches.Any(static branch => IsAdmittedAndNonterminal(branch.Disposition)))
                    continue;
                var node = (ForkProcessNode)plan.GetNode(fork.Fork);
                if (forkStarts.GetValueOrDefault(fork.RegistrationId) < node.Limits.MaximumStartsPerActivation)
                    continue;

                AddTrace(
                    ProcessTraceEventKind.ForkAdmissionChanged,
                    GetToken(fork.Owner),
                    node.Id,
                    detail: $"activation-boundary:pending={fork.Branches.Count(static branch => branch.Disposition == ExecutionTokenDisposition.Pending)}");
                Cut(node.Id);
                return true;
            }
            return false;
        }

        bool ForkMayAdmit(ProcessForkState fork) => !fork.Resolved
            || ((JoinProcessNode)plan.GetNode(fork.Join)).Policy.Cancellation
                == ProcessJoinCancellationPolicy.ContinueRemaining;

        static bool IsAdmittedAndNonterminal(ExecutionTokenDisposition disposition) => disposition is
            ExecutionTokenDisposition.Ready
            or ExecutionTokenDisposition.Active
            or ExecutionTokenDisposition.Waiting;

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
            var failure = kind == ExecutionTerminalOutcomeKind.Failed
                ? new DocumentValidationDiagnostic(
                    ProcessExecutionDiagnosticCodes.AuthoredFailure,
                    DiagnosticSeverity.Error,
                    $"Process control flow reached authored failure node '{node.Value}'.")
                : null;
            ReplaceToken(token with
            {
                Disposition = kind == ExecutionTerminalOutcomeKind.Completed
                    ? ExecutionTokenDisposition.Completed
                    : ExecutionTokenDisposition.Failed,
                Step = token.Step + 1,
                Failure = failure
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
                token.Step,
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
                    ProcessInputAdmissionReason.Stale,
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
                    ProcessInputAdmissionReason.Consumed,
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
                    ProcessInputAdmissionReason.Superseded,
                    wait.RegistrationId,
                    "await-superseded");
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

        PortableExpressionValue Evaluate(Expr expression, ProcessTokenState token) =>
            ProcessExpressionReferenceEvaluation.Evaluate(evaluator, expression, token.Bindings);

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
                && TryGetRequestNodeSemantics(
                    plan.GetNode(wait.Node),
                    out var requestContract,
                    out var outcomes)
                && plan.ValidationContext.InteractionContracts?.TryResolve(requestContract, out var contract) == true
                && contract is RequestContractDefinition requestDefinition)
            {
                if (!IsValidRequestResult(wait, wait.Node, requestContract, outcomes, input))
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
                && TryGetRequestNodeSemantics(
                    plan.GetNode(wait.Node),
                    out var requestContract,
                    out var outcomes)
                && plan.ValidationContext.InteractionContracts?.TryResolve(requestContract, out var contract) == true
                && contract is RequestContractDefinition requestDefinition)
            {
                if (!IsValidRequestResult(wait, wait.Node, requestContract, outcomes, input))
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
            ExecutionNodeId node,
            RequestContractReference request,
            ImmutableArray<ProcessRequestOutcomeBranch>? outcomes,
            ProcessActivationInput input)
        {
            var valid = input.Envelope is ReplyEnvelope reply
                        && wait.ObligationEmission == reply.InReplyTo
                        && ReplyMatchesRequest(reply, request)
                        && ReplyMatchesChildRequest(
                            wait.Token,
                            node,
                            wait.ObligationEmission.Value,
                            reply)
                        && (outcomes is null
                            || outcomes.Value.Any(outcome => outcome.Outcome == reply.Outcome.Id));
            if (valid)
            {
                return true;
            }

            diagnostics.Add(Diagnostic(
                ProcessExecutionDiagnosticCodes.InputNotAdmitted,
                "A correlated Request result does not satisfy the exact Request contract and authored outcome set.",
                node));
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
                && TryGetRequestNodeSemantics(
                    plan.GetNode(wait.Node),
                    out var requestContract,
                    out _)
                && plan.ValidationContext.InteractionContracts?.TryResolve(requestContract, out var contract) == true
                && contract is RequestContractDefinition requestDefinition)
            {
                return Map(requestDefinition.Response.DuplicateResult, prior.Disposition);
            }
            return ProcessInputAdmissionDisposition.Duplicate;
        }

        static bool TryGetRequestNodeSemantics(
            CanonicalProcessNode node,
            out RequestContractReference contract,
            out ImmutableArray<ProcessRequestOutcomeBranch>? outcomes)
        {
            if (ProcessRequestSemantics.TryProject(node, out var semantics))
            {
                contract = semantics.Contract;
                outcomes = semantics.Outcomes;
                return true;
            }
            if (ProcessRequestSemantics.TryGetContract(node, out contract))
            {
                outcomes = null;
                return true;
            }

            contract = null!;
            outcomes = null;
            return false;
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
            ProcessInputAdmissionReason reason,
            ProcessWaitRegistrationId? waitRegistrationId = null) => new(
            input,
            disposition,
            reason,
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
            ProcessInputAdmissionReason reason,
            ProcessWaitRegistrationId? registrationId,
            string detail)
        {
            bufferedInputs.Remove(buffered);
            var receipt = Receipt(buffered.Input, disposition, reason, registrationId);
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
                inputReason: receipt.Reason,
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
                                             or ExecutionTokenDisposition.Waiting
                                             or ExecutionTokenDisposition.Pending)
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
                or ExecutionTokenDisposition.Waiting
                or ExecutionTokenDisposition.Pending))
            {
                return;
            }

            ReplaceToken(token with { Disposition = ExecutionTokenDisposition.Cancelled });
            CloseTokenWork(token.Id);
        }

        void DispositionOwnedChildren(ProcessTokenState token)
        {
            foreach (var child in children
                         .Where(candidate => candidate.Disposition is
                             ProcessChildDisposition.Pending or ProcessChildDisposition.Active)
                         .Where(candidate => candidate.Owner == token.Id || candidate.Token == token.Id)
                         .OrderBy(static candidate => candidate.RegistrationId, StringComparer.Ordinal)
                         .ToArray())
            {
                CancelChild(child, token);
            }
        }

        void CancelChild(ProcessChildState child, ProcessTokenState traceToken)
        {
            if (child.Disposition == ProcessChildDisposition.Pending)
            {
                ReplaceChild(child with { Disposition = ProcessChildDisposition.CancelledBeforeStart });
                AddTrace(
                    ProcessTraceEventKind.ChildCancelledBeforeStart,
                    traceToken,
                    child.Node,
                    detail: child.RegistrationId);
                return;
            }
            if (child.Disposition != ProcessChildDisposition.Active)
            {
                return;
            }

            var disposition = child.Cancellation switch
            {
                ProcessChildCancellationPolicy.Propagate => ProcessChildDisposition.CancellationRequested,
                ProcessChildCancellationPolicy.Detach => ProcessChildDisposition.Detached,
                _ => throw new InvalidOperationException(
                    $"Child '{child.RegistrationId}' has no supported cancellation policy.")
            };
            ReplaceChild(child with { Disposition = disposition });
            AddTrace(
                disposition == ProcessChildDisposition.Detached
                    ? ProcessTraceEventKind.ChildDetached
                    : ProcessTraceEventKind.ChildCancellationRequested,
                traceToken,
                child.Node,
                detail: child.RegistrationId);

            var childToken = tokens.FirstOrDefault(candidate => candidate.Id == child.Token);
            if (childToken is not null
                && childToken.Id != traceToken.Id
                && childToken.Disposition is ExecutionTokenDisposition.Ready
                    or ExecutionTokenDisposition.Active
                    or ExecutionTokenDisposition.Waiting)
            {
                CancelToken(childToken);
            }
        }

        void CloseTokenWork(TokenId token)
        {
            var owner = tokens.FirstOrDefault(candidate => candidate.Id == token);
            if (owner is not null)
            {
                DispositionOwnedChildren(owner);
            }

            foreach (var waitId in waits
                         .Where(wait => wait.Token == token && wait.Active)
                         .Select(static wait => wait.RegistrationId)
                         .ToArray())
            {
                DeactivateWait(GetWait(waitId));
            }
            requests.RemoveAll(request => request.Token == token);

            foreach (var recurrence in recurrences
                         .Where(candidate => candidate.Token == token && candidate.Active)
                         .ToArray())
            {
                ReplaceRecurrence(recurrence with { Active = false });
            }

            foreach (var partition in partitions
                         .Where(candidate => candidate.Owner == token && !candidate.Resolved)
                         .ToArray())
            {
                ReplacePartition(partition with { Resolved = true });
            }

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
                    tombstone is null
                        ? ProcessInputAdmissionReason.TerminalUnconsumed
                        : ProcessInputAdmissionReason.Late,
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

        ProcessChildState GetChild(string id) => children.Single(child =>
            string.Equals(child.RegistrationId, id, StringComparison.Ordinal));

        ProcessPartitionState GetPartition(string id) => partitions.Single(partition =>
            string.Equals(partition.RegistrationId, id, StringComparison.Ordinal));

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

        void ReplaceChild(ProcessChildState value)
        {
            var index = children.FindIndex(candidate =>
                string.Equals(candidate.RegistrationId, value.RegistrationId, StringComparison.Ordinal));
            if (index < 0)
            {
                children.Add(value);
            }
            else
            {
                children[index] = value;
            }
        }

        void ReplacePartition(ProcessPartitionState value)
        {
            var index = partitions.FindIndex(candidate =>
                string.Equals(candidate.RegistrationId, value.RegistrationId, StringComparison.Ordinal));
            if (index < 0)
            {
                partitions.Add(value);
            }
            else
            {
                partitions[index] = value;
            }
        }

        void ReplaceRecurrence(ProcessRecurrenceState value)
        {
            var index = recurrences.FindIndex(candidate =>
                string.Equals(candidate.RegistrationId, value.RegistrationId, StringComparison.Ordinal));
            if (index < 0)
            {
                recurrences.Add(value);
            }
            else
            {
                recurrences[index] = value;
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
            ProcessInputAdmissionReason? inputReason = null,
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
                inputReason,
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
                [.. children.OrderBy(static child => child.RegistrationId, StringComparer.Ordinal)],
                [.. partitions.OrderBy(static partition => partition.RegistrationId, StringComparer.Ordinal)],
                [.. recurrences.OrderBy(static recurrence => recurrence.RegistrationId, StringComparer.Ordinal)],
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

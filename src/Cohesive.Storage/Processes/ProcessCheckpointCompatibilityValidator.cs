using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Storage.Processes;

/// <summary>Stable diagnostics emitted while admitting a durable Process checkpoint.</summary>
public static class ProcessCheckpointDiagnosticCodes
{
    /// <summary>The physical checkpoint schema is not supported by this interpreter.</summary>
    public const string SchemaVersionUnsupported = "storage.processes.checkpoint.schemaVersionUnsupported";

    /// <summary>A committed activation receipt contradicts its definition, instance, attempt, or trace identity.</summary>
    public const string ActivationReceiptIncompatible =
        "storage.processes.checkpoint.activationReceiptIncompatible";

    /// <summary>A cached host-operation receipt contradicts the exact compiled Process plan or retained evidence.</summary>
    public const string OperationReceiptIncompatible =
        "storage.processes.checkpoint.operationReceiptIncompatible";

    /// <summary>An inbox disposition lacks exact canonical continuation and committed activation evidence.</summary>
    public const string InboxReceiptIncompatible =
        "storage.processes.checkpoint.inboxReceiptIncompatible";

    /// <summary>An outbox emission, outstanding Request, or durable operation lacks its reciprocal ledger evidence.</summary>
    public const string EmissionLedgerIncompatible =
        "storage.processes.checkpoint.emissionLedgerIncompatible";

    /// <summary>A replacement Process attempt is not a clean continuation permitted by the compiled recovery policy.</summary>
    public const string RestartAttemptIncompatible =
        "storage.processes.checkpoint.restartAttemptIncompatible";
}

/// <summary>Validates a physical Process checkpoint against an exact compiled definition before execution.</summary>
public static class ProcessCheckpointCompatibilityValidator
{
    /// <summary>Validates one durable checkpoint for recovery under an exact Process plan.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan selected for recovery.</param>
    /// <param name="checkpoint">Complete physical checkpoint to admit.</param>
    /// <returns>
    /// Deterministically ordered schema, definition, and restored-continuation diagnostics. A valid result permits
    /// the caller to invoke the reference interpreter; an invalid result must fail before host execution.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="checkpoint"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (checkpoint.SchemaVersion != ProcessDurableCheckpoint.CurrentSchemaVersion)
        {
            diagnostics.Add(Error(
                ProcessCheckpointDiagnosticCodes.SchemaVersionUnsupported,
                $"Process checkpoint schema '{checkpoint.SchemaVersion.Value}' is not supported.",
                "/schemaVersion"));
        }

        var expected = plan.DefinitionReference;
        var observed = checkpoint.Definition;
        if (observed.DefinitionId != expected.DefinitionId)
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown,
                $"Process definition '{observed.DefinitionId.Value}' is not the compiled recovery definition.",
                "/continuation/definition/definitionId"));
        }
        else if (observed.RevisionId != expected.RevisionId)
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.RevisionUnsupported,
                $"Process revision '{observed.RevisionId.Value}' is not supported for this checkpoint.",
                "/continuation/definition/revisionId"));
        }
        else if (observed.Fingerprint != expected.Fingerprint)
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible,
                "The checkpoint fingerprint differs from the exact compiled Process revision.",
                "/continuation/definition/fingerprint"));
        }

        diagnostics.AddRange(ProcessContinuationValidator.Validate(plan, checkpoint.Continuation).Diagnostics);
        ValidateActivationEvidence(plan, checkpoint, diagnostics);
        ValidateOperationReceipts(plan, checkpoint, diagnostics);
        ValidateInboxEvidence(plan, checkpoint, diagnostics);
        ValidateEmissionLedger(plan, checkpoint, diagnostics);
        ValidateAdmittedReplyEvidence(checkpoint, diagnostics);
        ValidateChildRequestEvidence(plan, checkpoint, diagnostics);
        ValidateCleanAttempt(plan, checkpoint, diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateActivationEvidence(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        for (var activationIndex = 0; activationIndex < checkpoint.Activations.Length; activationIndex++)
        {
            var receipt = checkpoint.Activations[activationIndex];
            var activationValidation = ProcessReferenceInterpreter.ValidateActivationRequest(
                plan,
                receipt.Continuation,
                receipt.Activation);
            foreach (var diagnostic in activationValidation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Location = $"/activations/{activationIndex}{diagnostic.Location ?? string.Empty}"
                });
            }

            for (var traceIndex = 0; traceIndex < receipt.Evidence.Trace.Length; traceIndex++)
            {
                var trace = receipt.Evidence.Trace[traceIndex];
                if (trace.Sequence == traceIndex
                    && trace.Definition == checkpoint.Definition
                    && trace.Continuation == receipt.Continuation
                    && trace.Continuation.ProcessInstanceId
                        == checkpoint.ContinuationIdentity.ProcessInstanceId
                    && checkpoint.Control.Attempts.Any(attempt =>
                        attempt.AttemptId == trace.Continuation.ProcessAttemptId)
                    && trace.Activation == receipt.Activation.Id
                    && (trace.Kind == ProcessTraceEventKind.InteractionEmitted
                        || trace.EmissionFingerprint is null)
                    && (trace.Kind == ProcessTraceEventKind.OperationCompleted
                        ? trace.OperationOccurrence is >= 0
                        : trace.OperationOccurrence is null)
                    && (trace.Kind == ProcessTraceEventKind.InputAdmitted
                        ? trace.InputDisposition is { } inputDisposition
                            && Enum.IsDefined(inputDisposition)
                            && inputDisposition != ProcessInputAdmissionDisposition.Unspecified
                            && trace.InputReason is { } inputReason
                            && ProcessInputReceipt.IsValidAdmissionEvidence(
                                inputDisposition,
                                inputReason,
                                trace.WaitRegistrationId)
                            && (trace.WaitRegistrationId is not { } waitRegistration
                                || !string.IsNullOrWhiteSpace(waitRegistration.Value))
                        : trace.InputDisposition is null
                            && trace.InputReason is null
                            && trace.WaitRegistrationId is null))
                {
                    continue;
                }

                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible,
                    "Committed activation trace addresses another definition, continuation, or activation.",
                    $"/activations/{activationIndex}/evidence/trace/{traceIndex}"));
            }
        }

        HashSet<(ProcessAttemptId Attempt, ActivationId Activation)> safePointActivations = [];
        for (var attemptIndex = 0; attemptIndex < checkpoint.Control.Attempts.Length; attemptIndex++)
        {
            var attempt = checkpoint.Control.Attempts[attemptIndex];
            for (var safePointIndex = 0; safePointIndex < attempt.SafePoints.Length; safePointIndex++)
            {
                var safePoint = attempt.SafePoints[safePointIndex];
                var key = (attempt.AttemptId, safePoint.ActivationId);
                var matches = checkpoint.Activations.Where(receipt =>
                    receipt.Continuation.ProcessAttemptId == attempt.AttemptId
                    && receipt.Activation.Id == safePoint.ActivationId).ToArray();
                var coherent = matches is [var receipt]
                    && receipt.Activation.ObservedAtUtc == safePoint.Activation.ObservedAtUtc
                    && receipt.Evidence.SafePointNode == safePoint.Node
                    && receipt.CommittedAtUtc >= safePoint.ObservedAtUtc;
                if (coherent && safePointActivations.Add(key))
                {
                    continue;
                }

                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible,
                    "Control safe-point evidence has no exact attempt-scoped activation receipt.",
                    $"/control/attempts/{attemptIndex}/safePoints/{safePointIndex}"));
            }
        }

        for (var activationIndex = 0; activationIndex < checkpoint.Activations.Length; activationIndex++)
        {
            var receipt = checkpoint.Activations[activationIndex];
            var key = (receipt.Continuation.ProcessAttemptId, receipt.Activation.Id);
            if (safePointActivations.Contains(key)
                || IsImmediateCancellationActivation(plan, checkpoint, receipt))
            {
                continue;
            }

            diagnostics.Add(Error(
                ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible,
                "Committed activation receipt has no exact lifecycle-control durable-cut evidence.",
                $"/activations/{activationIndex}"));
        }
    }

    static bool IsImmediateCancellationActivation(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        ProcessActivationCommitReceipt activation)
    {
        if (activation.Disposition != ProcessActivationDisposition.Cancelled
            || activation.Activation.Cause is not (ProcessActivationCause.Control or ProcessActivationCause.Recovery)
            || activation.Activation.Cancellation is not { } cancellation
            || cancellation.AttemptId != activation.Continuation.ProcessAttemptId)
        {
            return false;
        }

        if (!ProcessReferenceInterpreter.ValidateActivationRequest(
                plan,
                activation.Continuation,
                activation.Activation).IsValid)
        {
            return false;
        }

        var attempt = checkpoint.Control.Attempts.SingleOrDefault(candidate =>
            candidate.AttemptId == activation.Continuation.ProcessAttemptId);
        if (attempt is not
            {
                Disposition: ProcessControlAttemptDisposition.Cancelled,
                Closure: { InterruptedActivation: null } closure
            })
        {
            return false;
        }

        var receipt = checkpoint.Control.FindReceipt(closure.CommandId);
        return receipt is
        {
            Command: CancelProcessCommand command,
            Disposition: ProcessControlReceiptDisposition.Applied
        }
            && command.Expectation?.Continuation == activation.Continuation
            && command.Reason == cancellation.Reason
            && receipt.RecordedAtUtc == closure.OccurredAtUtc
            && receipt.RecordedAtUtc == activation.Activation.ObservedAtUtc
            && activation.CommittedAtUtc >= receipt.RecordedAtUtc;
    }

    static void ValidateInboxEvidence(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var inbox = checkpoint.Inbox.ToDictionary(static entry => entry.EmissionId);
        for (var activationIndex = 0; activationIndex < checkpoint.Activations.Length; activationIndex++)
        {
            var activation = checkpoint.Activations[activationIndex];
            for (var inputIndex = 0; inputIndex < activation.Activation.Inputs.Length; inputIndex++)
            {
                var input = activation.Activation.Inputs[inputIndex];
                var emission = input.Envelope.Context.EmissionId;
                var exactInbox = inbox.TryGetValue(emission, out var entry)
                    && ProcessStorageContentFingerprints.Input(entry.Input)
                        == ProcessStorageContentFingerprints.Input(input);
                var admissionTrace = activation.Evidence.Trace.Any(trace =>
                    trace.Kind == ProcessTraceEventKind.InputAdmitted
                    && trace.Emission == emission);
                if (exactInbox && admissionTrace)
                {
                    continue;
                }

                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                    "Committed activation input has no exact durable inbox entry and admission trace.",
                    $"/activations/{activationIndex}/activation/inputs/{inputIndex}"));
            }

            for (var traceIndex = 0; traceIndex < activation.Evidence.Trace.Length; traceIndex++)
            {
                var trace = activation.Evidence.Trace[traceIndex];
                if (trace.Kind != ProcessTraceEventKind.InputAdmitted)
                {
                    continue;
                }

                var exactInput = trace.Emission is { } emission
                    && inbox.TryGetValue(emission, out var entry)
                    && checkpoint.Activations.Take(activationIndex + 1).Any(candidate =>
                        candidate.Continuation == activation.Continuation
                        && candidate.Activation.Inputs.Any(input =>
                            input.Envelope.Context.EmissionId == emission
                            && ProcessStorageContentFingerprints.Input(input)
                                == ProcessStorageContentFingerprints.Input(entry.Input)));
                if (!exactInput)
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                        "Committed InputAdmitted trace has no exact prior presentation and durable inbox entry.",
                        $"/activations/{activationIndex}/evidence/trace/{traceIndex}/emission"));
                }
            }
        }

        for (var inboxIndex = 0; inboxIndex < checkpoint.Inbox.Length; inboxIndex++)
        {
            var entry = checkpoint.Inbox[inboxIndex];
            var contracts = plan.ValidationContext.InteractionContracts;
            if (contracts is null
                || !InteractionEnvelopeValidator.Validate(
                    entry.Input.Envelope,
                    contracts,
                    plan.ValidationContext.ShapeGraph).IsValid)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                    "Durable inbox input violates the exact interaction catalog or portable payload contract.",
                    $"/inbox/{inboxIndex}/input/envelope"));
            }

            if (entry.Receipt is not { } receipt
                || entry.DispositionContinuation is not { } continuation)
            {
                continue;
            }

            if (!receipt.IsValidAdmissionEvidence())
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                    "Inbox receipt lacks a closed semantic input-admission reason compatible with its policy disposition.",
                    $"/inbox/{inboxIndex}/receipt/reason"));
                continue;
            }

            var decidingActivation = checkpoint.Activations.FirstOrDefault(activation =>
                activation.Continuation == continuation
                && activation.Activation.ObservedAtUtc == receipt.ObservedAtUtc
                && activation.Evidence.Trace.Any(trace =>
                    trace.Kind == ProcessTraceEventKind.InputAdmitted
                    && trace.Continuation == continuation
                    && trace.Emission == entry.EmissionId
                    && trace.InputDisposition == receipt.Disposition
                    && trace.InputReason == receipt.Reason
                    && trace.WaitRegistrationId == receipt.WaitRegistrationId));
            var witnessed = decidingActivation is not null
                && checkpoint.Activations.Any(activation =>
                    activation.Continuation == continuation
                    && activation.Sequence <= decidingActivation.Sequence
                    && activation.Activation.Inputs.Any(input =>
                        ProcessStorageContentFingerprints.Input(input)
                            == ProcessStorageContentFingerprints.Input(entry.Input)));
            if (!witnessed
                && !IsRestartAbandonmentDisposition(checkpoint, receipt, continuation))
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                    "Inbox disposition has no exact committed input-admission trace in its deciding attempt.",
                    $"/inbox/{inboxIndex}/receipt"));
            }
        }
    }

    static bool IsRestartAbandonmentDisposition(
        ProcessDurableCheckpoint checkpoint,
        ProcessInputReceipt receipt,
        ProcessContinuationIdentity continuation)
    {
        if (receipt.Disposition != ProcessInputAdmissionDisposition.Stale
            || receipt.Reason != ProcessInputAdmissionReason.Stale)
        {
            return false;
        }

        var attempt = checkpoint.Control.Attempts.SingleOrDefault(candidate =>
            candidate.AttemptId == continuation.ProcessAttemptId);
        if (attempt is not
            {
                Disposition: ProcessControlAttemptDisposition.Abandoned,
                Closure: { } closure
            })
        {
            return false;
        }

        var controlReceipt = checkpoint.Control.FindReceipt(closure.CommandId);
        return controlReceipt is
        {
            Command: RestartProcessAttemptCommand command,
            Disposition: ProcessControlReceiptDisposition.Applied
        }
            && command.Expectation?.Continuation == continuation
            && command.Plan.NewAttemptId == checkpoint.Control.CurrentAttempt.AttemptId
            && controlReceipt.RecordedAtUtc == closure.OccurredAtUtc
            && receipt.ObservedAtUtc == closure.OccurredAtUtc;
    }

    static void ValidateEmissionLedger(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var emissions = checkpoint.Emissions.ToDictionary(static emission => emission.EmissionId);
        var durableOperations = checkpoint.DurableOperations.ToDictionary(static operation => operation.OperationId);
        var contracts = plan.ValidationContext.InteractionContracts;
        Dictionary<EmissionId, int> producerClaims = [];
        HashSet<EmissionId> exactOrigins = [];
        for (var activationIndex = 0; activationIndex < checkpoint.Activations.Length; activationIndex++)
        {
            var activation = checkpoint.Activations[activationIndex];
            var traces = activation.Evidence.Trace;
            for (var traceIndex = 0; traceIndex < traces.Length; traceIndex++)
            {
                var trace = traces[traceIndex];
                if (trace.Kind != ProcessTraceEventKind.InteractionEmitted)
                {
                    continue;
                }

                if (trace.Emission is not { } emission)
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                        "Committed InteractionEmitted trace has no unique exact-origin durable outbox record.",
                        $"/activations/{activationIndex}/evidence/trace/{traceIndex}/emission"));
                    continue;
                }

                AddProducerClaim(producerClaims, emission);
                if (!emissions.TryGetValue(emission, out var outbox)
                    || outbox.EnqueuedAtUtc != activation.CommittedAtUtc
                    || !TraceMatchesEnvelope(plan, trace, outbox.Envelope)
                    || !exactOrigins.Add(emission))
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                        "Committed InteractionEmitted trace has no unique exact-origin durable outbox record.",
                        $"/activations/{activationIndex}/evidence/trace/{traceIndex}/emission"));
                }
            }
        }

        foreach (var receipt in checkpoint.Operations)
        {
            if (!receipt.Result.IsValidOutcome()
                || !plan.Definition.Nodes.Any(node => node.Id == receipt.Key.Node)
                || !TryGetOperationDefinition(
                    plan.GetNode(receipt.Key.Node),
                    out _,
                    out var expectedKind))
            {
                continue;
            }

            foreach (var envelope in receipt.Result.Emissions)
            {
                var emission = envelope.Context.EmissionId;
                AddProducerClaim(producerClaims, emission);
                if (emissions.TryGetValue(emission, out var outbox)
                    && outbox.EnqueuedAtUtc == receipt.RecordedAtUtc
                    && ProcessStorageContentFingerprints.Envelope(outbox.Envelope)
                        == ProcessStorageContentFingerprints.Envelope(envelope)
                    && HostOperationOriginMatches(plan, receipt, expectedKind, envelope))
                {
                    exactOrigins.Add(emission);
                }
            }
        }

        for (var emissionIndex = 0; emissionIndex < checkpoint.Emissions.Length; emissionIndex++)
        {
            var emission = checkpoint.Emissions[emissionIndex];
            if (contracts is null
                || !InteractionEnvelopeValidator.Validate(
                    emission.Envelope,
                    contracts,
                    plan.ValidationContext.ShapeGraph).IsValid)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Durable outbox envelope violates the exact interaction catalog or portable payload contract.",
                    $"/emissions/{emissionIndex}/envelope"));
            }

            if (!producerClaims.TryGetValue(emission.EmissionId, out var producerCount)
                || producerCount != 1
                || !exactOrigins.Contains(emission.EmissionId))
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Durable outbox record must have exactly one committed Process or host-operation origin.",
                    $"/emissions/{emissionIndex}"));
            }

            if (emission.Envelope is RequestEnvelope
                && !durableOperations.ContainsKey(emission.EmissionId))
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Durable Request outbox record has no exact operation-ledger state.",
                    $"/emissions/{emissionIndex}"));
            }
        }

        if (contracts is not null)
        {
            var executor = new DurableOperationReferenceExecutor(contracts);
            for (var operationIndex = 0;
                 operationIndex < checkpoint.DurableOperations.Length;
                 operationIndex++)
            {
                var operation = checkpoint.DurableOperations[operationIndex];
                var validation = executor.TryCreate(
                    operation.Request,
                    operation.Binding,
                    operation.CreatedAtUtc,
                    out _);
                foreach (var diagnostic in validation.Diagnostics)
                {
                    diagnostics.Add(diagnostic with
                    {
                        Location = $"/durableOperations/{operationIndex}{diagnostic.Location ?? string.Empty}"
                    });
                }

                var creationAnchored = operation.Request.Context.Origin is ProcessInteractionOrigin origin
                    && checkpoint.Activations.Any(receipt =>
                        receipt.Continuation == origin.Continuation
                        && receipt.Activation.Id == origin.Activation
                        && receipt.Activation.ObservedAtUtc == operation.CreatedAtUtc);
                if (!creationAnchored)
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                        "A durable Request operation's creation time must equal its exact origin activation observation time.",
                        $"/durableOperations/{operationIndex}/createdAtUtc"));
                }
            }
        }

        for (var requestIndex = 0;
             requestIndex < checkpoint.Continuation.OutstandingRequests.Length;
             requestIndex++)
        {
            var outstanding = checkpoint.Continuation.OutstandingRequests[requestIndex];
            var coherent = emissions.TryGetValue(outstanding.Emission, out var emission)
                && emission.Envelope is RequestEnvelope request
                && request.Contract == outstanding.Contract
                && request.Context.Origin is ProcessInteractionOrigin origin
                && origin.Continuation == checkpoint.ContinuationIdentity
                && origin.Token == outstanding.Token
                && origin.Node == outstanding.Node
                && request.ResponseTarget is ProcessTokenInteractionTarget target
                && target.Continuation == checkpoint.ContinuationIdentity
                && target.Token == outstanding.Token
                && target.WaitRegistrationId is { } registration
                && checkpoint.Continuation.Waits.Any(wait =>
                    wait.Active
                    && wait.Kind == ProcessWaitKind.Request
                    && wait.RegistrationId == registration
                    && wait.ObligationEmission == outstanding.Emission)
                && durableOperations.ContainsKey(outstanding.Emission);
            if (!coherent)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Outstanding Request has no exact active wait, outbox envelope, and durable operation.",
                    $"/continuation/outstandingRequests/{requestIndex}"));
            }
        }
    }

    static void ValidateAdmittedReplyEvidence(
        ProcessDurableCheckpoint checkpoint,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<EmissionId, ProcessDurableInboxEntry> inbox = [];
        foreach (var entry in checkpoint.Inbox)
        {
            if (entry?.Input?.Envelope?.Context is { } context
                && !string.IsNullOrWhiteSpace(context.EmissionId.Value))
            {
                inbox.TryAdd(context.EmissionId, entry);
            }
        }
        for (var operationIndex = 0;
             operationIndex < checkpoint.DurableOperations.Length;
             operationIndex++)
        {
            var operation = checkpoint.DurableOperations[operationIndex];
            if (operation is null || operation.Admission is not { AdvancesTarget: true })
            {
                continue;
            }

            var replyId = ProcessDurableRuntimeIdentities.OperationReply(operation.OperationId);
            var exactReply = TryCreateAcceptedReplyInput(operation, out var expectedReplyInput)
                && expectedReplyInput is not null
                && inbox.TryGetValue(replyId, out var entry)
                && ProcessStorageContentFingerprints.Input(entry.Input)
                    == ProcessStorageContentFingerprints.Input(expectedReplyInput);
            if (exactReply)
            {
                continue;
            }

            diagnostics.Add(Error(
                ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                "An accepted durable Request admission must retain its exact canonical Reply inbox projection.",
                $"/durableOperations/{operationIndex}/admission"));
        }
    }

    static bool TryCreateAcceptedReplyInput(
        DurableOperationState? operation,
        out ProcessActivationInput? input)
    {
        input = null;
        if (operation is not
            {
                Admission.AdvancesTarget: true,
                Acknowledgement: not null,
                Request.ResponseTarget: ProcessTokenInteractionTarget target
            })
        {
            return false;
        }

        input = new(
            target,
            operation.CreateReply(
                ProcessDurableRuntimeIdentities.OperationReply(operation.OperationId),
                ProcessDurableRuntimeIdentities.OperationReplyIdempotency(operation.OperationId),
                operation.Request.Context.Ordering,
                operation.Request.Context.Provenance));
        return true;
    }

    static void ValidateChildRequestEvidence(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var continuation = checkpoint.Continuation;
        Dictionary<EmissionId, ProcessEmissionRecord> outbox = [];
        foreach (var record in checkpoint.Emissions)
        {
            if (record?.Envelope?.Context is { } context
                && !string.IsNullOrWhiteSpace(context.EmissionId.Value))
            {
                outbox.TryAdd(context.EmissionId, record);
            }
        }
        Dictionary<EmissionId, ProcessDurableInboxEntry> inbox = [];
        foreach (var entry in checkpoint.Inbox)
        {
            if (entry?.Input?.Envelope?.Context is { } context
                && !string.IsNullOrWhiteSpace(context.EmissionId.Value))
            {
                inbox.TryAdd(context.EmissionId, entry);
            }
        }
        Dictionary<EmissionId, DurableOperationState> durableOperations = [];
        foreach (var operation in checkpoint.DurableOperations)
        {
            if (operation is not null && !string.IsNullOrWhiteSpace(operation.OperationId.Value))
            {
                durableOperations.TryAdd(operation.OperationId, operation);
            }
        }

        for (var emissionIndex = 0; emissionIndex < checkpoint.Emissions.Length; emissionIndex++)
        {
            var record = checkpoint.Emissions[emissionIndex];
            if (record?.Envelope is not RequestEnvelope request
                || request.Context.Origin is not ProcessInteractionOrigin origin
                || plan.Definition.Nodes.FirstOrDefault(candidate => candidate.Id == origin.Node) is not { } node)
            {
                continue;
            }

            var childBearing = ProcessRequestSemantics.TryGetChildTarget(
                node,
                out _,
                out var childOutcomeMapping);
            var matchingChildren = continuation.Children.Where(child => child is not null
                && child.Process is not null
                && child.Continuation is not null
                && child.RequestEmission == request.Context.EmissionId
                && child.Token == origin.Token
                && child.Node == origin.Node).ToArray();
            string? childRegistration = null;
            var canonicalChildRequest = childBearing
                && ProcessReferenceIdentities.TryGetCanonicalChildRegistration(
                    plan.DefinitionReference,
                    node,
                    request,
                    out childRegistration);
            var childTargetValid = childBearing
                ? canonicalChildRequest
                  && ((matchingChildren is [var child]
                       && request.ChildTarget == new ProcessChildRequestTarget(
                           child.Process,
                           child.Continuation,
                           childOutcomeMapping,
                           child.Owner,
                           child.Occurrence,
                           child.ProgressIdentity))
                      || (matchingChildren.Length == 0
                          && IsRetainedClosedAttemptChildRequest(
                              checkpoint,
                              request,
                              origin,
                              childRegistration!,
                              durableOperations)))
                : request.ChildTarget is null;
            if (!childTargetValid)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    childBearing
                        ? "Child Request outbox evidence requires exactly one child occurrence and its exact start target."
                        : "An ordinary Request outbox envelope cannot carry a child Process start target.",
                    $"/emissions/{emissionIndex}/envelope/childTarget"));
            }
        }

        for (var childIndex = 0; childIndex < continuation.Children.Length; childIndex++)
        {
            var child = continuation.Children[childIndex];
            if (child is null
                || child.Process is null
                || child.Continuation is null
                || child.RequestEmission is not { } requestEmission)
            {
                continue;
            }

            var childNode = plan.Definition.Nodes.FirstOrDefault(candidate => candidate.Id == child.Node);
            ProcessChildRequestTarget? expectedChildTarget = null;
            if (childNode is not null
                && ProcessRequestSemantics.TryGetChildTarget(
                    childNode,
                    out _,
                    out var childOutcomeMapping))
            {
                expectedChildTarget = new(
                    child.Process,
                    child.Continuation,
                    childOutcomeMapping,
                    child.Owner,
                    child.Occurrence,
                    child.ProgressIdentity);
            }
            var requestEvidenceValid = expectedChildTarget is not null
                && outbox.TryGetValue(requestEmission, out var outboxRecord)
                && outboxRecord.Envelope is RequestEnvelope request
                && request.ChildTarget == expectedChildTarget
                && request.Context.Origin is ProcessInteractionOrigin origin
                && origin.Definition == plan.DefinitionReference
                && origin.Continuation == continuation.Continuation
                && origin.Token == child.Token
                && origin.Node == child.Node
                && request.ResponseTarget is ProcessTokenInteractionTarget target
                && target.Continuation == continuation.Continuation
                && target.Token == child.Token
                && target.WaitRegistrationId is { } registration
                && continuation.Waits.Count(wait => wait is not null
                    && wait.RegistrationId == registration
                    && wait.Kind == ProcessWaitKind.Request
                    && wait.Token == child.Token
                    && wait.Node == child.Node
                    && wait.ObligationEmission == requestEmission) == 1
                && durableOperations.ContainsKey(requestEmission);
            if (!requestEvidenceValid)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Started child state has no exact child-targeted Request outbox, wait, and durable operation evidence.",
                    $"/continuation/children/{childIndex}/requestEmission"));
            }

            if (child.Disposition is not (ProcessChildDisposition.Completed or ProcessChildDisposition.Failed))
            {
                continue;
            }

            var terminalWaits = continuation.Waits.Where(wait => wait is not null
                && !wait.Active
                && wait.Kind == ProcessWaitKind.Request
                && wait.Token == child.Token
                && wait.Node == child.Node
                && wait.ObligationEmission == requestEmission).ToArray();
            var winner = terminalWaits is [var terminalWait] ? terminalWait.WinnerInput : null;
            var matchingReceipts = winner is { } winnerEmission
                ? continuation.InputReceipts.Where(candidate => candidate is not null
                    && candidate.Input?.Envelope is not null
                    && candidate.Emission == winnerEmission
                    && candidate.Disposition == ProcessInputAdmissionDisposition.Consumed).ToArray()
                : [];
            var receipt = matchingReceipts is [var exactReceipt] ? exactReceipt : null;
            ProcessActivationInput? expectedReplyInput = null;
            if (durableOperations.TryGetValue(requestEmission, out var operation)
                && TryCreateAcceptedReplyInput(operation, out var admittedReplyInput))
            {
                expectedReplyInput = admittedReplyInput;
            }
            var inboxEntry = receipt is not null
                && inbox.TryGetValue(receipt.Emission, out var retainedEntry)
                    ? retainedEntry
                    : null;
            var terminalEvidenceValid = receipt is not null
                && inboxEntry is not null
                && inboxEntry.Input == receipt.Input
                && inboxEntry.Receipt == receipt
                && inboxEntry.DispositionContinuation == continuation.Continuation;
            if (!terminalEvidenceValid)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                    "Terminal child state has no exact durable inbox and deciding-attempt evidence for its consumed Reply.",
                    $"/continuation/children/{childIndex}/result"));
                continue;
            }

            var operationReplyEvidenceValid = receipt!.Emission
                    == ProcessDurableRuntimeIdentities.OperationReply(requestEmission)
                && expectedReplyInput is not null
                && inboxEntry is not null
                && ProcessStorageContentFingerprints.Input(inboxEntry.Input)
                    == ProcessStorageContentFingerprints.Input(expectedReplyInput);
            if (!operationReplyEvidenceValid)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Terminal child state requires the exact accepted operation and its deterministic canonical Reply.",
                    $"/continuation/children/{childIndex}/result"));
            }
        }

        Dictionary<(ExecutionNodeId Node, ActivationId Activation),
            List<(int PartitionIndex, string Registration)>> partitionStarts = [];
        for (var partitionIndex = 0; partitionIndex < continuation.Partitions.Length; partitionIndex++)
        {
            var partition = continuation.Partitions[partitionIndex];
            if (partition is null
                || plan.Definition.Nodes.FirstOrDefault(candidate => candidate.Id == partition.Node)
                    is not ForEachPartitionProcessNode node
                || node.Limits is null)
            {
                continue;
            }

            foreach (var work in partition.Work)
            {
                if (work is null || string.IsNullOrWhiteSpace(work.ChildRegistrationId))
                {
                    continue;
                }

                var child = continuation.Children.FirstOrDefault(candidate => candidate is not null
                    && !string.IsNullOrWhiteSpace(candidate.RegistrationId)
                    && candidate.RegistrationId == work.ChildRegistrationId);
                if (child?.RequestEmission is not { } requestEmission
                    || !outbox.TryGetValue(requestEmission, out var record)
                    || record.Envelope is not RequestEnvelope
                    {
                        Context.Origin: ProcessInteractionOrigin origin
                    }
                    || origin.Definition != plan.DefinitionReference
                    || origin.Continuation != continuation.Continuation
                    || origin.Node != partition.Node
                    || origin.Token != child.Token)
                {
                    continue;
                }

                var key = (partition.Node, origin.Activation);
                if (!partitionStarts.TryGetValue(key, out var starts))
                {
                    starts = [];
                    partitionStarts.Add(key, starts);
                }
                starts.Add((partitionIndex, partition.RegistrationId));
            }
        }

        foreach (var (key, starts) in partitionStarts)
        {
            var node = (ForEachPartitionProcessNode)plan.GetNode(key.Node);
            if (starts.Count <= node.Limits.MaximumStartsPerActivation
                && starts.Select(static start => start.Registration).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                continue;
            }

            var partitionIndex = starts.Min(static start => start.PartitionIndex);
            diagnostics.Add(Error(
                ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                "Partition child Request evidence must come from one reachable occurrence per node and activation and stay within the authored maximum starts.",
                $"/continuation/partitions/{partitionIndex}/work"));
        }
    }

    static bool IsRetainedClosedAttemptChildRequest(
        ProcessDurableCheckpoint checkpoint,
        RequestEnvelope request,
        ProcessInteractionOrigin origin,
        string childRegistration,
        IReadOnlyDictionary<EmissionId, DurableOperationState> durableOperations)
    {
        if (origin.Continuation.ProcessInstanceId != checkpoint.ContinuationIdentity.ProcessInstanceId
            || origin.Continuation == checkpoint.ContinuationIdentity
            || request.ChildTarget is not { } childTarget
            || childTarget.Continuation.ProcessInstanceId == origin.Continuation.ProcessInstanceId
            || request.ResponseTarget is not ProcessTokenInteractionTarget responseTarget
            || responseTarget.Continuation != origin.Continuation
            || responseTarget.Token != origin.Token
            || !durableOperations.TryGetValue(request.Context.EmissionId, out var operation)
            || operation.Request != request)
        {
            return false;
        }

        var registrations = checkpoint.Activations.Sum(activation => activation.Evidence.Trace.Count(trace =>
            trace.Kind == ProcessTraceEventKind.ChildRegistered
            && trace.Continuation == origin.Continuation
            && trace.Node == origin.Node
            && trace.Token == childTarget.OwnerToken
            && string.Equals(trace.Detail, childRegistration, StringComparison.Ordinal)));
        if (registrations != 1)
            return false;

        var attempt = checkpoint.Control.Attempts.SingleOrDefault(candidate =>
            candidate.AttemptId == origin.Continuation.ProcessAttemptId);
        return attempt is
        {
            Disposition: ProcessControlAttemptDisposition.Abandoned,
            Phase: ProcessControlExecutionPhase.Stopped,
            Closure: not null
        };
    }

    static bool TraceMatchesEnvelope(
        CompiledProcessPlan plan,
        ProcessTraceEvent trace,
        InteractionEnvelope envelope)
    {
        if (envelope.Context.Origin is not ProcessInteractionOrigin origin
            || origin.Definition != plan.DefinitionReference
            || origin.Definition != trace.Definition
            || origin.Node != trace.Node
            || origin.Continuation != trace.Continuation
            || origin.Activation != trace.Activation
            || origin.Token != trace.Token
            || origin.Entity is not null
            || origin.Transition is not null
            || origin.TransitionNode is not null
            || origin.Outcome is not null
            || !plan.Definition.Nodes.Any(node => node.Id == trace.Node))
        {
            return false;
        }

        var node = plan.GetNode(trace.Node);
        var kind = (node, envelope) switch
        {
            (ProcessNode requestNode, RequestEnvelope request) when
                TryGetRequestContract(requestNode, out var requestContract)
                && request.Contract == requestContract
                && request.ResponseTarget is ProcessTokenInteractionTarget target
                && target.Continuation == trace.Continuation
                && target.Token == trace.Token
                && target.WaitRegistrationId is not null => "request",
            (EmitEventProcessNode eventNode, DomainEventEnvelope domainEvent) when
                domainEvent.Contract == eventNode.Contract => "event",
            (SendSignalProcessNode signalNode, SignalEnvelope signal) when
                signal.Contract == signalNode.Contract => "signal",
            (ReplyProcessNode replyNode, ReplyEnvelope reply) when
                reply.Contract == replyNode.Contract => "reply",
            _ => null
        };
        return kind is not null
            && string.Equals(trace.Detail, kind, StringComparison.Ordinal)
            && trace.EmissionFingerprint
                == InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope);
    }

    static bool TryGetRequestContract(
        ProcessNode node,
        out RequestContractReference contract) =>
        ProcessRequestSemantics.TryGetContract(node, out contract);

    static bool HostOperationOriginMatches(
        CompiledProcessPlan plan,
        ProcessOperationReceipt receipt,
        ProcessDefinitionLinkKind operationKind,
        InteractionEnvelope envelope)
    {
        if (envelope.Context.Origin is not ProcessInteractionOrigin origin
            || origin.Definition != plan.DefinitionReference
            || origin.Node != receipt.Key.Node
            || origin.Continuation != receipt.Key.Continuation
            || origin.Activation != receipt.Key.Activation
            || origin.Token != receipt.Key.Token)
        {
            return false;
        }

        return operationKind switch
        {
            ProcessDefinitionLinkKind.RelationQuery =>
                origin.Entity is null
                && origin.Transition is null
                && origin.TransitionNode is null
                && origin.Outcome is null,
            ProcessDefinitionLinkKind.Transition =>
                origin.Entity is not null
                && origin.Transition == receipt.OperationDefinition
                && origin.TransitionNode is not null
                && origin.Outcome is not null,
            _ => false
        };
    }

    static void AddProducerClaim(Dictionary<EmissionId, int> claims, EmissionId emission)
    {
        claims.TryGetValue(emission, out var count);
        claims[emission] = checked(count + 1);
    }

    static void ValidateCleanAttempt(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        if (checkpoint.Control.Attempts.Length > 1
            && plan.Definition.RecoveryPolicy != ProcessRecoveryPolicy.RestartAttempt)
        {
            diagnostics.Add(Error(
                ProcessCheckpointDiagnosticCodes.RestartAttemptIncompatible,
                "Process control retains replacement attempts but the compiled definition does not permit restart recovery.",
                "/control/attempts"));
        }

        var input = checkpoint.Start.Request.Input ?? PortableValue.Missing(plan.Definition.Input);
        for (var attemptIndex = 0; attemptIndex < checkpoint.Control.Attempts.Length; attemptIndex++)
        {
            var attempt = checkpoint.Control.Attempts[attemptIndex];
            var firstReceipt = checkpoint.Activations.FirstOrDefault(receipt =>
                receipt.Continuation.ProcessAttemptId == attempt.AttemptId
                && receipt.Sequence == 1);
            if (firstReceipt is null)
            {
                continue;
            }

            var clean = ProcessReferenceInterpreter.Create(
                plan,
                new(checkpoint.ContinuationIdentity.ProcessInstanceId, attempt.AttemptId),
                input);
            if (firstReceipt.BeforeContinuation
                != ProcessStorageContentFingerprints.Continuation(clean))
            {
                var receiptIndex = checkpoint.Activations.IndexOf(firstReceipt);
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.RestartAttemptIncompatible,
                    "An attempt's first activation must consume the exact clean continuation for its pinned definition and input.",
                    $"/activations/{receiptIndex}/beforeContinuation"));
            }
        }

        if (checkpoint.Continuation.CompletedActivationCount == 0)
        {
            var expected = ProcessReferenceInterpreter.Create(
                plan,
                checkpoint.ContinuationIdentity,
                input);
            if (ProcessStorageContentFingerprints.Continuation(expected)
                != ProcessStorageContentFingerprints.Continuation(checkpoint.Continuation))
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.RestartAttemptIncompatible,
                    "A zero-activation Process attempt must be the exact clean continuation for its pinned definition and input.",
                    "/continuation"));
            }
        }
    }

    static void ValidateOperationReceipts(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var nodes = plan.Definition.Nodes.ToDictionary(static node => node.Id);
        var emissions = checkpoint.Emissions.ToDictionary(static emission => emission.EmissionId);
        var receipts = checkpoint.Operations.ToDictionary(static receipt => receipt.Key);
        for (var activationIndex = 0; activationIndex < checkpoint.Activations.Length; activationIndex++)
        {
            var activation = checkpoint.Activations[activationIndex];
            for (var traceIndex = 0; traceIndex < activation.Evidence.Trace.Length; traceIndex++)
            {
                var trace = activation.Evidence.Trace[traceIndex];
                if (trace.Kind != ProcessTraceEventKind.OperationCompleted)
                {
                    continue;
                }

                var exactReceipt = trace.OperationOccurrence is { } occurrence
                    && receipts.ContainsKey(new(
                        trace.Continuation,
                        trace.Activation,
                        trace.Token,
                        trace.Node,
                        occurrence));
                if (!exactReceipt)
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                        "Committed OperationCompleted trace has no exact attempt-scoped host-operation receipt.",
                        $"/activations/{activationIndex}/evidence/trace/{traceIndex}/operationOccurrence"));
                }
            }
        }

        for (var index = 0; index < checkpoint.Operations.Length; index++)
        {
            var receipt = checkpoint.Operations[index];
            var location = $"/operations/{index}";
            if (!nodes.TryGetValue(receipt.Key.Node, out var node)
                || !TryGetOperationDefinition(node, out var expectedDefinition, out var expectedKind)
                || receipt.OperationDefinition != expectedDefinition
                || !plan.ValidationContext.TryResolve(expectedDefinition, out var link)
                || link.Kind != expectedKind)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                    "Cached host-operation identity does not match an exact compiled Transition or Relation/Query node.",
                    $"{location}/operationDefinition"));
                continue;
            }

            var matchingActivation = checkpoint.Activations.SingleOrDefault(activation =>
                    activation.Continuation == receipt.Key.Continuation
                    && activation.Activation.Id == receipt.Key.Activation
                    && activation.Evidence.Trace.Any(trace =>
                        trace.Kind == ProcessTraceEventKind.OperationCompleted
                        && trace.Continuation == receipt.Key.Continuation
                        && trace.Activation == receipt.Key.Activation
                        && trace.Token == receipt.Key.Token
                        && trace.Node == receipt.Key.Node
                        && trace.OperationOccurrence == receipt.Key.Occurrence));
            if (matchingActivation is null
                || receipt.RecordedAtUtc != matchingActivation.CommittedAtUtc)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                    "Cached host operation has no exact attempt-scoped committed activation trace.",
                    $"{location}/key"));
            }

            var result = receipt.Result;
            if (!result.IsValidOutcome())
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                    "Cached host-operation result is not one closed success or failure outcome.",
                    $"{location}/result"));
                continue;
            }

            if (result.IsSuccessful)
            {
                var value = result.Value!;
                var valueValidation = PortableExecutionValidator.Validate(
                    value,
                    plan.ValidationContext.ShapeGraph);
                if (value.Contract != link.Result || !valueValidation.IsValid)
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                        "Cached host-operation result violates the exact compiled result contract.",
                        $"{location}/result/value"));
                }
            }

            for (var emissionIndex = 0; emissionIndex < result.Emissions.Length; emissionIndex++)
            {
                var emission = result.Emissions[emissionIndex];
                var contracts = plan.ValidationContext.InteractionContracts;
                var valid = contracts is not null
                    && InteractionEnvelopeValidator.Validate(
                        emission,
                        contracts,
                        plan.ValidationContext.ShapeGraph).IsValid;
                var retained = emissions.TryGetValue(emission.Context.EmissionId, out var outbox)
                    && ProcessStorageContentFingerprints.Envelope(outbox.Envelope)
                        == ProcessStorageContentFingerprints.Envelope(emission)
                    && outbox.EnqueuedAtUtc == receipt.RecordedAtUtc;
                var exactOrigin = HostOperationOriginMatches(
                    plan,
                    receipt,
                    expectedKind,
                    emission);
                if (valid && retained && exactOrigin)
                {
                    continue;
                }

                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                    "Cached host-operation emission is invalid, absent from the exact durable outbox, or lacks exact operation-occurrence provenance.",
                    $"{location}/result/emissions/{emissionIndex}"));
            }
        }
    }

    static bool TryGetOperationDefinition(
        ProcessNode node,
        out ExecutionDefinitionReference definition,
        out ProcessDefinitionLinkKind kind)
    {
        switch (node)
        {
            case InvokeTransitionProcessNode transition:
                definition = transition.Transition;
                kind = ProcessDefinitionLinkKind.Transition;
                return true;
            case EvaluateRelationProcessNode relation:
                definition = relation.Relation;
                kind = ProcessDefinitionLinkKind.RelationQuery;
                return true;
            default:
                definition = null!;
                kind = ProcessDefinitionLinkKind.Unspecified;
                return false;
        }
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(stage: "processCheckpointRecovery"));
}

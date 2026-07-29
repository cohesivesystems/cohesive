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
        ValidateActivationEvidence(checkpoint, diagnostics);
        ValidateOperationReceipts(plan, checkpoint, diagnostics);
        ValidateInboxEvidence(plan, checkpoint, diagnostics);
        ValidateEmissionLedger(plan, checkpoint, diagnostics);
        ValidateCleanAttempt(plan, checkpoint, diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateActivationEvidence(
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        for (var activationIndex = 0; activationIndex < checkpoint.Activations.Length; activationIndex++)
        {
            var receipt = checkpoint.Activations[activationIndex];
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
                            && (trace.WaitRegistrationId is not { } waitRegistration
                                || !string.IsNullOrWhiteSpace(waitRegistration.Value))
                        : trace.InputDisposition is null && trace.WaitRegistrationId is null))
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
            if (safePointActivations.Contains(key))
            {
                continue;
            }

            diagnostics.Add(Error(
                ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible,
                "Committed activation receipt has no exact lifecycle-control safe-point evidence.",
                $"/activations/{activationIndex}"));
        }
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
                    && activation.Activation.Inputs.Any(input =>
                        input.Envelope.Context.EmissionId == emission
                        && ProcessStorageContentFingerprints.Input(input)
                            == ProcessStorageContentFingerprints.Input(entry.Input));
                if (!exactInput)
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                        "Committed InputAdmitted trace has no exact activation input and durable inbox entry.",
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

            var witnessed = checkpoint.Activations.Any(activation =>
                activation.Continuation == continuation
                && activation.Activation.ObservedAtUtc == receipt.ObservedAtUtc
                && activation.Activation.Inputs.Any(input =>
                    ProcessStorageContentFingerprints.Input(input)
                        == ProcessStorageContentFingerprints.Input(entry.Input))
                && activation.Evidence.Trace.Any(trace =>
                    trace.Kind == ProcessTraceEventKind.InputAdmitted
                    && trace.Continuation == continuation
                    && trace.Emission == entry.EmissionId
                    && trace.InputDisposition == receipt.Disposition
                    && trace.WaitRegistrationId == receipt.WaitRegistrationId));
            if (!witnessed)
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible,
                    "Inbox disposition has no exact committed input-admission trace in its deciding attempt.",
                    $"/inbox/{inboxIndex}/receipt"));
            }
        }
    }

    static void ValidateEmissionLedger(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var emissions = checkpoint.Emissions.ToDictionary(static emission => emission.EmissionId);
        var durableOperations = checkpoint.DurableOperations.ToDictionary(static operation => operation.OperationId);
        HashSet<EmissionId> traceOrigins = [];
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

                if (trace.Emission is not { } emission
                    || !emissions.TryGetValue(emission, out var outbox)
                    || outbox.EnqueuedAtUtc != activation.CommittedAtUtc
                    || !TraceMatchesEnvelope(plan, trace, outbox.Envelope)
                    || !traceOrigins.Add(emission))
                {
                    diagnostics.Add(Error(
                        ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                        "Committed InteractionEmitted trace has no unique exact-origin durable outbox record.",
                        $"/activations/{activationIndex}/evidence/trace/{traceIndex}/emission"));
                }
            }
        }

        var operationOrigins = checkpoint.Operations
            .SelectMany(static operation =>
                operation.Result.IsValidOutcome()
                    ? operation.Result.Emissions
                    : [])
            .Select(static emission => emission.Context.EmissionId)
            .ToHashSet();
        for (var emissionIndex = 0; emissionIndex < checkpoint.Emissions.Length; emissionIndex++)
        {
            var emission = checkpoint.Emissions[emissionIndex];
            var contracts = plan.ValidationContext.InteractionContracts;
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

            if (!traceOrigins.Contains(emission.EmissionId)
                && !operationOrigins.Contains(emission.EmissionId))
            {
                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.EmissionLedgerIncompatible,
                    "Durable outbox record has no committed Process or host-operation origin evidence.",
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
            || origin.Outcome is not null
            || !plan.Definition.Nodes.Any(node => node.Id == trace.Node))
        {
            return false;
        }

        var node = plan.GetNode(trace.Node);
        var kind = (node, envelope) switch
        {
            (RequestProcessNode requestNode, RequestEnvelope request) when
                request.Contract == requestNode.Contract
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
                if (valid && retained)
                {
                    continue;
                }

                diagnostics.Add(Error(
                    ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible,
                    "Cached host-operation emission is invalid or absent from the exact durable outbox.",
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

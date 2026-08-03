using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Single Storage-owned composition boundary for legal durable Process checkpoint successors.</summary>
static class ProcessDurableCheckpointReducer
{
    internal static bool TryApplyActivation(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        ProcessActivation activation,
        ProcessActivationDecision decision,
        ProcessControlState control,
        ImmutableArray<ProcessOperationReplayObservation> operationObservations,
        IProcessDurableRequestBindingResolver bindingResolver,
        DateTimeOffset committedAtUtc,
        out ProcessDurableCheckpoint? replacement,
        out ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(bindingResolver);

        List<DocumentValidationDiagnostic> failures = [];
        var inbox = ApplyInbox(checkpoint, activation, decision, failures);
        RequireTerminalInboxClosure(checkpoint, decision, inbox, failures);
        var emissions = ApplyEmissions(checkpoint, decision, committedAtUtc, failures, out var newRequests);
        var operations = ApplyOperations(checkpoint, decision, operationObservations, committedAtUtc);
        var durableOperations = ApplyDurableRequests(
            plan,
            checkpoint,
            newRequests,
            bindingResolver,
            activation.ObservedAtUtc,
            failures);
        if (failures.Count > 0)
        {
            failures.Sort(DocumentValidationDiagnosticComparer.Ordinal);
            replacement = null;
            diagnostics = [.. failures];
            return false;
        }

        var before = ProcessStorageContentFingerprints.Continuation(checkpoint.Continuation);
        var after = ProcessStorageContentFingerprints.Continuation(decision.State);
        var receipt = new ProcessActivationCommitReceipt(
            decision.State.CompletedActivationCount,
            decision.State.Continuation,
            before,
            after,
            activation,
            decision.Disposition,
            decision.Evidence,
            committedAtUtc);
        replacement = new(
            checkpoint.SchemaVersion,
            checkpoint.Start,
            decision.State,
            control,
            [.. checkpoint.Activations, receipt],
            operations,
            inbox,
            emissions,
            durableOperations,
            checkpoint.CreatedAtUtc,
            committedAtUtc);
        diagnostics = [];
        return true;
    }

    internal static ProcessDurableCheckpoint ApplyControl(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        ProcessControlDecision decision,
        DateTimeOffset committedAtUtc)
    {
        if (!TryApplyControl(
                plan,
                checkpoint,
                decision,
                committedAtUtc,
                out var replacement,
                out var diagnostics))
        {
            throw new InvalidOperationException(diagnostics[0].Message);
        }
        return replacement
            ?? throw new InvalidOperationException("A successful control reduction returned no checkpoint.");
    }

    internal static bool TryApplyControl(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        ProcessControlDecision decision,
        DateTimeOffset committedAtUtc,
        out ProcessDurableCheckpoint? replacement,
        out ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(decision);

        var continuation = checkpoint.Continuation;
        var inbox = checkpoint.Inbox;
        if (decision.Intent is ProcessAttemptRestartIntent restart)
        {
            var abandoned = decision.State.Attempts.Single(attempt =>
                attempt.AttemptId == checkpoint.ContinuationIdentity.ProcessAttemptId);
            var closure = abandoned.Closure
                ?? throw new InvalidOperationException("A validated RestartAttempt decision requires abandonment closure evidence.");
            inbox = ClosePendingInboxForRestart(
                inbox,
                checkpoint.ContinuationIdentity,
                closure.OccurredAtUtc);
            continuation = ProcessReferenceInterpreter.RestartAttempt(
                plan,
                checkpoint.Continuation,
                restart.ReplacementAttemptId);
        }

        if (decision.Intent is ProcessSignalAdmissionIntent signal)
        {
            var target = signal.Admission.Signal.Target as ProcessTokenInteractionTarget
                ?? throw new InvalidOperationException("A validated Process Signal requires a Process-token target.");
            var input = new ProcessActivationInput(target, signal.Admission.Signal);
            var existingIndex = FindInbox(inbox, input.Envelope.Context.EmissionId);
            if (existingIndex < 0)
            {
                inbox = [.. inbox, new(input, signal.Admission.AdmittedAtUtc)];
            }
            else if (ProcessStorageContentFingerprints.Input(inbox[existingIndex].Input)
                     != ProcessStorageContentFingerprints.Input(input))
            {
                replacement = null;
                diagnostics = [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict,
                    "A Process Signal admission conflicts with retained inbox content under the same emission identity.",
                    "/decision/signal/context/emissionId")];
                return false;
            }
        }

        replacement = new(
            checkpoint.SchemaVersion,
            checkpoint.Start,
            continuation,
            decision.State,
            checkpoint.Activations,
            checkpoint.Operations,
            inbox,
            checkpoint.Emissions,
            checkpoint.DurableOperations,
            checkpoint.CreatedAtUtc,
            committedAtUtc);
        diagnostics = [];
        return true;
    }

    static ImmutableArray<ProcessDurableInboxEntry> ClosePendingInboxForRestart(
        ImmutableArray<ProcessDurableInboxEntry> inbox,
        ProcessContinuationIdentity closingContinuation,
        DateTimeOffset observedAtUtc)
    {
        if (inbox.All(static entry => entry.Receipt is not null
            && entry.Receipt.Disposition != ProcessInputAdmissionDisposition.Buffered))
        {
            return inbox;
        }

        var closed = inbox.ToBuilder();
        for (var index = 0; index < closed.Count; index++)
        {
            var entry = closed[index];
            if (entry.Receipt is not null
                && entry.Receipt.Disposition != ProcessInputAdmissionDisposition.Buffered)
            {
                continue;
            }

            closed[index] = new(
                entry.Input,
                entry.AdmittedAtUtc,
                new(
                    entry.Input,
                    ProcessInputAdmissionDisposition.Stale,
                    ProcessInputAdmissionReason.Stale,
                    observedAtUtc),
                closingContinuation);
        }
        return closed.MoveToImmutable();
    }

    internal static ProcessDurableCheckpoint ApplyAffinity(
        ProcessDurableCheckpoint checkpoint,
        ProcessControlDecision decision,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(decision);
        return new(
            checkpoint.SchemaVersion,
            checkpoint.Start,
            checkpoint.Continuation,
            decision.State,
            checkpoint.Activations,
            checkpoint.Operations,
            checkpoint.Inbox,
            checkpoint.Emissions,
            checkpoint.DurableOperations,
            checkpoint.CreatedAtUtc,
            committedAtUtc);
    }

    internal static bool TryApplyDurableOperation(
        ProcessDurableCheckpoint checkpoint,
        DurableOperationState operation,
        DateTimeOffset committedAtUtc,
        ProcessActivationInput? admittedReply,
        out ProcessDurableCheckpoint? replacement,
        out ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(operation);
        var operations = checkpoint.DurableOperations.ToBuilder();
        var index = FindOperation(checkpoint.DurableOperations, operation.OperationId);
        if (index < 0)
        {
            replacement = null;
            diagnostics = [Error(
                ProcessDurableRuntimeDiagnosticCodes.OperationNotFound,
                $"Durable operation '{operation.OperationId.Value}' is not retained by the Process checkpoint.",
                "/operation/operationId")];
            return false;
        }
        operations[index] = operation;

        var inbox = checkpoint.Inbox;
        if (admittedReply is not null)
        {
            var inputIndex = FindInbox(inbox, admittedReply.Envelope.Context.EmissionId);
            if (inputIndex < 0)
            {
                inbox = [.. inbox, new(admittedReply, committedAtUtc)];
            }
            else if (ProcessStorageContentFingerprints.Input(inbox[inputIndex].Input)
                     != ProcessStorageContentFingerprints.Input(admittedReply))
            {
                replacement = null;
                diagnostics = [Error(
                    ProcessDurableRuntimeDiagnosticCodes.OperationReplyIdentityConflict,
                    "A durable operation Reply conflicts with retained inbox content under the same emission identity.",
                    "/admittedReply/envelope/context/emissionId")];
                return false;
            }
        }

        replacement = new(
            checkpoint.SchemaVersion,
            checkpoint.Start,
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.Activations,
            checkpoint.Operations,
            inbox,
            checkpoint.Emissions,
            operations.MoveToImmutable(),
            checkpoint.CreatedAtUtc,
            committedAtUtc);
        diagnostics = [];
        return true;
    }

    static ImmutableArray<ProcessDurableInboxEntry> ApplyInbox(
        ProcessDurableCheckpoint checkpoint,
        ProcessActivation activation,
        ProcessActivationDecision decision,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var entries = checkpoint.Inbox.ToBuilder();
        // A replay admission may report Reason.Duplicate while the continuation deliberately retains the
        // original semantic receipt. The durable inbox projects that canonical state, not the latest presentation.
        var retainedReceipts = decision.State.InputReceipts.ToDictionary(static receipt => receipt.Emission);
        foreach (var input in activation.Inputs)
        {
            var index = FindInbox(checkpoint.Inbox, input.Envelope.Context.EmissionId);
            if (index >= 0
                && ProcessStorageContentFingerprints.Input(checkpoint.Inbox[index].Input)
                    == ProcessStorageContentFingerprints.Input(input))
            {
                continue;
            }

            diagnostics.Add(Error(
                ProcessDurableRuntimeDiagnosticCodes.ActivationInputNotAdmitted,
                "A finite durable activation may consume only exact input already admitted to its durable inbox.",
                "/activation/inputs"));
        }

        foreach (var receipt in decision.InputAdmissions)
        {
            var index = FindInbox(checkpoint.Inbox, receipt.Emission);
            if (index < 0)
            {
                diagnostics.Add(Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationInputNotAdmitted,
                    "An input disposition has no exact retained durable inbox entry.",
                    "/decision/inputAdmissions"));
                continue;
            }

            var prior = checkpoint.Inbox[index];
            if (ProcessStorageContentFingerprints.Input(prior.Input)
                != ProcessStorageContentFingerprints.Input(receipt.Input))
            {
                diagnostics.Add(Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationInputNotAdmitted,
                    "An input disposition describes content different from the retained durable inbox entry.",
                    "/decision/inputAdmissions"));
                continue;
            }

            if (!retainedReceipts.TryGetValue(receipt.Emission, out var retainedReceipt))
            {
                continue;
            }

            entries[index] = new(
                prior.Input,
                prior.AdmittedAtUtc,
                retainedReceipt,
                decision.State.Continuation);
        }
        return entries.ToImmutable();
    }

    static void RequireTerminalInboxClosure(
        ProcessDurableCheckpoint checkpoint,
        ProcessActivationDecision decision,
        ImmutableArray<ProcessDurableInboxEntry> replacement,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (decision.State.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
        {
            return;
        }

        for (var index = 0; index < checkpoint.Inbox.Length; index++)
        {
            if (checkpoint.Inbox[index].Receipt is null && replacement[index].Receipt is null)
            {
                diagnostics.Add(Error(
                    ProcessDurableRuntimeDiagnosticCodes.TerminalInputUndispositioned,
                    "A terminal activation must disposition every input already pending before its durable cut.",
                    $"/checkpoint/inbox/{index}"));
            }
        }
    }

    static ImmutableArray<ProcessEmissionRecord> ApplyEmissions(
        ProcessDurableCheckpoint checkpoint,
        ProcessActivationDecision decision,
        DateTimeOffset committedAtUtc,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        out ImmutableArray<RequestEnvelope> newRequests)
    {
        var entries = checkpoint.Emissions.ToBuilder();
        var requests = ImmutableArray.CreateBuilder<RequestEnvelope>();
        HashSet<EmissionId> emittedThisCut = [];
        for (var emissionIndex = 0; emissionIndex < decision.Emissions.Length; emissionIndex++)
        {
            var emission = decision.Emissions[emissionIndex];
            if (!emittedThisCut.Add(emission.Context.EmissionId))
            {
                diagnostics.Add(Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict,
                    "A logical emission identity has more than one producer in the same durable activation cut.",
                    $"/decision/emissions/{emissionIndex}/context/emissionId"));
                continue;
            }

            var index = FindEmission(checkpoint.Emissions, emission.Context.EmissionId);
            if (index >= 0)
            {
                if (ProcessStorageContentFingerprints.Envelope(checkpoint.Emissions[index].Envelope)
                    != ProcessStorageContentFingerprints.Envelope(emission))
                {
                    diagnostics.Add(Error(
                        ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict,
                        "A logical emission identity was reused for different canonical envelope content.",
                        $"/decision/emissions/{emissionIndex}"));
                }
                continue;
            }

            entries.Add(new(emission, committedAtUtc));
            if (emission is RequestEnvelope request)
            {
                requests.Add(request);
            }
        }
        newRequests = requests.ToImmutable();
        return entries.ToImmutable();
    }

    static ImmutableArray<ProcessOperationReceipt> ApplyOperations(
        ProcessDurableCheckpoint checkpoint,
        ProcessActivationDecision decision,
        ImmutableArray<ProcessOperationReplayObservation> observations,
        DateTimeOffset committedAtUtc)
    {
        var traceKeys = decision.Evidence.Trace
            .Where(static trace => trace.Kind == ProcessTraceEventKind.OperationCompleted)
            .Select(trace => new ProcessOperationOccurrence(
                trace.Continuation,
                trace.Activation,
                trace.Token,
                trace.Node,
                trace.OperationOccurrence!.Value))
            .ToHashSet();
        var receipts = checkpoint.Operations.ToBuilder();
        foreach (var observation in observations)
        {
            if (!traceKeys.Contains(observation.Key))
            {
                continue;
            }
            receipts.Add(new(
                observation.Key,
                observation.OperationDefinition,
                observation.Result,
                committedAtUtc));
        }
        return receipts.ToImmutable();
    }

    static ImmutableArray<DurableOperationState> ApplyDurableRequests(
        CompiledProcessPlan plan,
        ProcessDurableCheckpoint checkpoint,
        ImmutableArray<RequestEnvelope> requests,
        IProcessDurableRequestBindingResolver bindingResolver,
        DateTimeOffset createdAtUtc,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (requests.IsEmpty)
        {
            return checkpoint.DurableOperations;
        }

        var contracts = plan.ValidationContext.InteractionContracts
            ?? throw new InvalidOperationException("A durable Request requires the compiled interaction catalog.");
        var executor = new DurableOperationReferenceExecutor(contracts);
        var operations = checkpoint.DurableOperations.ToBuilder();
        foreach (var request in requests)
        {
            if (!bindingResolver.TryResolve(request, out var binding) || binding is null)
            {
                diagnostics.Add(Error(
                    ProcessDurableRuntimeDiagnosticCodes.RequestBindingUnavailable,
                    $"No durable execution binding is available for Request '{request.Contract.Definition.DefinitionId.Value}'.",
                    "/decision/emissions"));
                continue;
            }

            var validation = executor.TryCreate(request, binding, createdAtUtc, out var operation);
            if (!validation.IsValid || operation is null)
            {
                foreach (var diagnostic in validation.Diagnostics)
                {
                    diagnostics.Add(diagnostic);
                }
                continue;
            }
            operations.Add(operation);
        }
        return operations.ToImmutable();
    }

    static int FindInbox(ImmutableArray<ProcessDurableInboxEntry> entries, EmissionId emission)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].EmissionId == emission)
            {
                return index;
            }
        }
        return -1;
    }

    static int FindEmission(ImmutableArray<ProcessEmissionRecord> entries, EmissionId emission)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].EmissionId == emission)
            {
                return index;
            }
        }
        return -1;
    }

    static int FindOperation(ImmutableArray<DurableOperationState> entries, EmissionId operation)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].OperationId == operation)
            {
                return index;
            }
        }
        return -1;
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(stage: "processDurableRuntime"));
}

using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>Projects authoritative Process activation evidence into the shared normalized trace contract.</summary>
public static class ProcessExecutionTraceProjector
{
    const string Stage = "processTraceProjection";

    /// <summary>Projects one finite Process decision without copying invocation, input, or result payload values.</summary>
    /// <param name="decision">Pure replacement-state Process decision.</param>
    /// <returns>A normalized trace, or structured diagnostics when evidence affinity is invalid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    public static ExecutionTraceProjectionResult Project(ProcessActivationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var sourceReference = FirstSourceReference(decision.Evidence);
        if (decision.Evidence.Definition != decision.State.Definition)
        {
            diagnostics.Add(Error(
                ExecutionTraceDiagnosticCodes.DefinitionMismatch,
                "Process trace evidence and replacement state must name the same exact definition.",
                "/definition",
                decision.State.Definition.DefinitionId.Value,
                sourceReference));
        }

        Dictionary<EmissionId, InteractionEnvelope> envelopes = [];
        AddEnvelopes(decision, envelopes, diagnostics, sourceReference);
        return ProjectEvidence(
            decision.Evidence,
            decision.Disposition,
            durableCommitSequence: null,
            expectedDefinition: decision.State.Definition,
            expectedContinuation: decision.State.Continuation,
            envelopes,
            diagnostics,
            sourceReference);
    }

    /// <summary>Projects one committed Process activation against explicit durable-receipt affinity evidence.</summary>
    /// <param name="evidence">Definition-bound semantic activation trace retained by the durable receipt.</param>
    /// <param name="disposition">Finite semantic activation disposition retained by the durable receipt.</param>
    /// <param name="durableCommitSequence">Positive durable activation commit sequence.</param>
    /// <param name="expectedDefinition">Definition pinned by the durable checkpoint.</param>
    /// <param name="expectedContinuation">Process instance and attempt pinned by the durable receipt.</param>
    /// <param name="envelopes">
    /// Canonical checkpoint inbox and outbox envelopes used only to attach correlation identities without
    /// copying their payload values.
    /// </param>
    /// <returns>A normalized trace, or structured diagnostics when evidence affinity is invalid.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="evidence"/>, <paramref name="expectedDefinition"/>,
    /// <paramref name="expectedContinuation"/>, or <paramref name="envelopes"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="durableCommitSequence"/> is not positive.</exception>
    public static ExecutionTraceProjectionResult ProjectCommitted(
        ProcessExecutionEvidence evidence,
        ProcessActivationDisposition disposition,
        long durableCommitSequence,
        ExecutionDefinitionReference expectedDefinition,
        ProcessContinuationIdentity expectedContinuation,
        IEnumerable<InteractionEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(expectedDefinition);
        ArgumentNullException.ThrowIfNull(expectedContinuation);
        ArgumentNullException.ThrowIfNull(envelopes);
        if (durableCommitSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durableCommitSequence),
                durableCommitSequence,
                "A durable commit sequence must be positive.");
        }
        if (!Enum.IsDefined(disposition) || disposition == ProcessActivationDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Activation disposition must be explicit.");

        List<DocumentValidationDiagnostic> diagnostics = [];
        var sourceReference = FirstSourceReference(evidence);
        Dictionary<EmissionId, InteractionEnvelope> indexedEnvelopes = [];
        foreach (var envelope in envelopes)
            AddEnvelope(envelope, indexedEnvelopes, diagnostics, sourceReference);

        return ProjectEvidence(
            evidence,
            disposition,
            durableCommitSequence,
            expectedDefinition,
            expectedContinuation,
            indexedEnvelopes,
            diagnostics,
            sourceReference);
    }

    static ExecutionTraceProjectionResult ProjectEvidence(
        ProcessExecutionEvidence evidence,
        ProcessActivationDisposition disposition,
        long? durableCommitSequence,
        ExecutionDefinitionReference expectedDefinition,
        ProcessContinuationIdentity expectedContinuation,
        IReadOnlyDictionary<EmissionId, InteractionEnvelope> envelopes,
        List<DocumentValidationDiagnostic> diagnostics,
        string sourceReference)
    {
        if (evidence.Definition != expectedDefinition)
        {
            diagnostics.Add(Error(
                ExecutionTraceDiagnosticCodes.DefinitionMismatch,
                "Process trace and durable affinity must name the same exact definition.",
                "/definition",
                expectedDefinition.DefinitionId.Value,
                sourceReference));
        }

        ValidateTrace(evidence, expectedContinuation, envelopes, diagnostics, sourceReference);
        if (diagnostics.Count != 0)
            return ExecutionTraceProjectionResult.Failure(diagnostics);

        var source = evidence.Trace;
        var events = ImmutableArray.CreateBuilder<NormalizedExecutionTraceEvent>(source.Length);
        foreach (var item in source)
        {
            envelopes.TryGetValue(item.Emission ?? default, out var envelope);
            events.Add(new(
                sequence: item.Sequence,
                kind: ConventionName(item.Kind),
                node: item.Node,
                token: item.Token,
                branchOrClause: item.BranchOrClause,
                emission: item.Emission,
                correlation: envelope?.Context.CorrelationId,
                causation: envelope?.Context.CausationId,
                idempotencyKey: envelope?.Context.IdempotencyKey,
                emissionFingerprint: item.EmissionFingerprint,
                operationOccurrence: item.OperationOccurrence,
                inputDisposition: item.InputDisposition is { } inputDisposition
                    ? ConventionName(inputDisposition)
                    : null,
                inputReason: item.InputReason is { } inputReason
                    ? ConventionName(inputReason)
                    : null,
                waitRegistrationId: item.WaitRegistrationId,
                processOccurrence: item.ProcessOccurrence,
                requestOutcome: item.RequestOutcome,
                detail: item.Detail,
                sourceReferences: item.SourceReferences));
        }

        return ExecutionTraceProjectionResult.Success(new(
            schemaVersion: NormalizedExecutionTrace.CurrentSchemaVersion,
            kind: ProcessDefinitionDocuments.Kind,
            definition: expectedDefinition,
            continuation: expectedContinuation,
            activation: evidence.Activation,
            disposition: ConventionName(disposition),
            safePointNode: evidence.SafePointNode,
            durableCommitSequence: durableCommitSequence,
            events: events.MoveToImmutable()));
    }

    static void AddEnvelopes(
        ProcessActivationDecision decision,
        IDictionary<EmissionId, InteractionEnvelope> envelopes,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string sourceReference)
    {
        foreach (var envelope in decision.Emissions)
            AddEnvelope(envelope, envelopes, diagnostics, sourceReference);
        foreach (var admission in decision.InputAdmissions)
        {
            if (admission is null || admission.Input?.Envelope is null)
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                    "Process input-admission evidence contains no canonical envelope.",
                    "/inputAdmissions",
                    decision.State.Continuation.ProcessInstanceId.Value,
                    sourceReference));
                continue;
            }

            AddEnvelope(admission.Input.Envelope, envelopes, diagnostics, sourceReference);
        }
    }

    static void AddEnvelope(
        InteractionEnvelope? envelope,
        IDictionary<EmissionId, InteractionEnvelope> envelopes,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string sourceReference)
    {
        if (envelope is null)
        {
            diagnostics.Add(Error(
                ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                "Process emission evidence contains no canonical envelope.",
                "/emissions",
                "process",
                sourceReference));
            return;
        }

        var emission = envelope.Context.EmissionId;
        if (!envelopes.TryGetValue(emission, out var existing))
        {
            envelopes.Add(emission, envelope);
            return;
        }

        if (InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(existing)
            != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope))
        {
            diagnostics.Add(Error(
                ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                $"Logical emission '{emission.Value}' has conflicting canonical envelope evidence.",
                "/emissions",
                emission.Value,
                sourceReference));
        }
    }

    static void ValidateTrace(
        ProcessExecutionEvidence evidence,
        ProcessContinuationIdentity expectedContinuation,
        IReadOnlyDictionary<EmissionId, InteractionEnvelope> envelopes,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string sourceReference)
    {
        var trace = evidence.Trace;
        for (var index = 0; index < trace.Length; index++)
        {
            var item = trace[index];
            if (item is null
                || item.Sequence != index
                || !Enum.IsDefined(item.Kind)
                || item.Definition != evidence.Definition
                || item.Continuation != expectedContinuation
                || item.Activation != evidence.Activation
                || string.IsNullOrWhiteSpace(item.Token.Value)
                || string.IsNullOrWhiteSpace(item.Node.Value)
                || item.SourceReferences.IsDefault
                || item.SourceReferences.Any(string.IsNullOrWhiteSpace))
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EventInvalid,
                    $"Process trace event {index} contradicts its enclosing activation lineage.",
                    $"/trace/{index}",
                    expectedContinuation.ProcessInstanceId.Value,
                    sourceReference));
                continue;
            }

            var requiredOccurrenceKind = RequiredOccurrenceKind(item.Kind);
            if ((requiredOccurrenceKind is null) != (item.ProcessOccurrence is null)
                || requiredOccurrenceKind is { } requiredKind
                && item.ProcessOccurrence?.Kind != requiredKind
                || item.RequestOutcome is { } requestOutcome
                && (string.IsNullOrWhiteSpace(requestOutcome.Value) || item.Emission is null)
                || item.Kind == ProcessTraceEventKind.ChildResolved && item.RequestOutcome is null)
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EventInvalid,
                    $"Process trace event {index} has incomplete or contradictory occurrence evidence.",
                    $"/trace/{index}/processOccurrence",
                    item.Node.Value,
                    sourceReference));
                continue;
            }

            var envelopeRequired = item.Kind is ProcessTraceEventKind.InteractionEmitted
                or ProcessTraceEventKind.InputAdmitted;
            InteractionEnvelope? envelope = null;
            var hasEnvelope = item.Emission is { } emission
                && envelopes.TryGetValue(emission, out envelope);
            if (item.RequestOutcome is { } exactRequestOutcome
                && (!hasEnvelope
                    || envelope is not ReplyEnvelope reply
                    || reply.Outcome.Id != exactRequestOutcome))
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                    $"Process trace event {index} has contradictory terminal Request outcome evidence.",
                    $"/trace/{index}/requestOutcome",
                    item.Node.Value,
                    sourceReference));
                continue;
            }
            if (item.Kind == ProcessTraceEventKind.ChildResolved
                && item.ProcessOccurrence is
                {
                    Disclosure: ExecutionTraceEvidenceDisclosure.Disclosed,
                    Definition: { } childDefinition,
                    Continuation: { } childContinuation
                }
                && (!hasEnvelope
                    || envelope is not ReplyEnvelope
                    {
                        Context.Origin: ProcessInteractionOrigin childOrigin
                    }
                    || childOrigin.Definition != childDefinition
                    || childOrigin.Continuation != childContinuation))
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                    $"Process trace event {index} has contradictory child Reply lineage evidence.",
                    $"/trace/{index}/processOccurrence",
                    item.Node.Value,
                    sourceReference));
                continue;
            }
            if (envelopeRequired
                && !hasEnvelope)
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                    $"Process trace event {index} has no matching canonical envelope evidence.",
                    $"/trace/{index}/emission",
                    item.Node.Value,
                    sourceReference));
                continue;
            }

            if (item.Kind == ProcessTraceEventKind.InteractionEmitted
                && (item.EmissionFingerprint is not { } fingerprint
                    || fingerprint != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope!)))
            {
                diagnostics.Add(Error(
                    ExecutionTraceDiagnosticCodes.EmissionEvidenceMismatch,
                    $"Process trace event {index} has stale or missing envelope fingerprint evidence.",
                    $"/trace/{index}/emissionFingerprint",
                    item.Node.Value,
                    sourceReference));
            }
        }
    }

    static ProcessTraceOccurrenceKind? RequiredOccurrenceKind(ProcessTraceEventKind kind) => kind switch
    {
        ProcessTraceEventKind.ChildRegistered
            or ProcessTraceEventKind.ChildResolved
            or ProcessTraceEventKind.ChildCancellationRequested
            or ProcessTraceEventKind.ChildDetached
            or ProcessTraceEventKind.ChildCancelledBeforeStart
            or ProcessTraceEventKind.ChildCancellationSettled
            or ProcessTraceEventKind.CancellationFinalizerStarted => ProcessTraceOccurrenceKind.Child,
        ProcessTraceEventKind.PartitionBatchChanged => ProcessTraceOccurrenceKind.Partition,
        ProcessTraceEventKind.RecurrenceAdvanced => ProcessTraceOccurrenceKind.Recurrence,
        _ => null
    };

    static string FirstSourceReference(ProcessExecutionEvidence evidence) =>
        evidence.Trace.SelectMany(static item => item?.SourceReferences ?? [])
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item))
        ?? evidence.Definition.DefinitionId.Value;

    static string ConventionName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    static DocumentValidationDiagnostic Error(
        string code,
        string message,
        string location,
        string subject,
        string sourceReference) => new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: Stage,
                subject: subject,
                sourceReferences: [sourceReference]));
}

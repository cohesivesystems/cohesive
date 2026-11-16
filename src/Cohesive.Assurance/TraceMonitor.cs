using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Immutable bounded reference monitor for one ordered mapping of an exact observation scope.</summary>
/// <remarks>Each accepted update re-evaluates retained history through the batch interpreter. This is an
/// incremental ingestion contract, not an optimized streaming evaluator. No observations or obligations are evicted.
/// Rejected updates leave this snapshot unchanged and must not be acknowledged as consumed.</remarks>
public sealed class TraceMonitor
{
    readonly Lazy<ClaimReference> reference;

    TraceMonitor(TraceEvaluation evaluation, ClaimReference? predecessor = null, ClaimReference? correctionEvidence = null)
    {
        Evaluation = evaluation; Predecessor = predecessor; CorrectionEvidence = correctionEvidence;
        reference = new(CreateReference, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Evaluation of this accepted prefix only; later rejected deliveries are not covered.</summary>
    public TraceEvaluation Evaluation { get; }
    /// <summary>Exact prior checkpoint, or null at the start of a monitoring session.</summary>
    public ClaimReference? Predecessor { get; }
    /// <summary>Evidence for a replacement epoch and its complete single mapping, or null for an append.</summary>
    public ClaimReference? CorrectionEvidence { get; }

    /// <summary>Starts monitoring an exact claim, target, contract and epoch.</summary>
    /// <param name="claim">Derived claim; fixed throughout this session.</param>
    /// <param name="initial">Initial accepted prefix with exactly one completely enumerated mapping.</param>
    /// <param name="limits">Bounds for retained records and every batch evaluation; defaults to reference limits.</param>
    /// <param name="cancellationToken">Cancellation leaves no accepted monitor.</param>
    /// <returns>Immutable monitor containing a completed evaluation, which may have an inconclusive verdict.</returns>
    /// <exception cref="ArgumentNullException">Claim or initial batch is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An evaluation bound is nonpositive.</exception>
    /// <exception cref="TraceMonitorUpdateException">Mapping profile is unsupported or evaluation cannot complete.</exception>
    /// <exception cref="OperationCanceledException">Evaluation is cancelled.</exception>
    public static TraceMonitor Start(ClaimDocument claim, TraceObservationBatch initial,
        TraceEvaluationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.Alternatives.IsDefault || initial.Alternatives.Length != 1
            || initial.Enumeration != EnumerationCompleteness.Complete)
            throw Reject(TraceEvaluationCodes.MonitorProfile, "/alternatives", "The reference monitor requires one complete mapping; use batch evaluation for joint alternatives.");
        return new(Evaluate(claim, initial, limits, cancellationToken));
    }

    /// <summary>Atomically appends ordered occurrences and replaces advancing per-source completeness frontiers.</summary>
    /// <param name="update">New deliveries and frontier updates; its reference identifies the resulting cumulative alternative.
    /// Identical occurrence redelivery is ignored. Reuse of the current alternative reference may only reproduce the current state.</param>
    /// <param name="cancellationToken">Cancellation rejects the entire update.</param>
    /// <returns>A new accepted snapshot, or this instance for an identical replay of the current reference.</returns>
    /// <exception cref="ArgumentNullException">Update is null.</exception>
    /// <exception cref="TraceMonitorUpdateException">Input is invalid, late, contradicts prior completeness, exceeds bounds,
    /// reuses a reference for changed content, or cannot be evaluated. Retry without acknowledging the rejected delivery,
    /// or explicitly correct the epoch when prior completeness was false.</exception>
    /// <exception cref="OperationCanceledException">Evaluation is cancelled; no part of the update is accepted.</exception>
    public TraceMonitor Advance(TraceObservationAlternative update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        var prior = Evaluation.Batch.Alternatives[0];
        if (update.Reference is null || update.Observations.IsDefault || update.Completeness.IsDefault)
            throw Reject(TraceEvaluationCodes.InvalidObservation, "/update", "Update reference and initialized record arrays are required.");
        if ((long)update.Observations.Length + update.Completeness.Length > Evaluation.Limits.MaxObservations)
            throw Capacity();
        Dictionary<(string Partition, string Occurrence), SemanticObservation> seen = [];
        foreach (var observation in prior.Observations) seen.TryAdd(TraceObservationValidation.Key(observation), observation);
        var observations = prior.Observations.ToBuilder();
        var frontiers = prior.Completeness.ToDictionary(TraceObservationValidation.Key);
        var changed = false;
        foreach (var observation in update.Observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TraceObservationValidation.Observe(observation, seen, out var duplicate) is { } invalid)
                throw new TraceMonitorUpdateException(invalid);
            if (duplicate) continue;
            if (frontiers.TryGetValue((observation.Selector, observation.Partition), out var frontier)
                && (frontier.Closure == ObservationClosure.Closed || observation.Step <= frontier.ThroughStep
                    || observation.Ticks <= frontier.ThroughTicks))
                throw Reject(TraceEvaluationCodes.MonitorLateObservation, observation.Occurrence,
                    "New occurrence contradicts previously certified inclusive completeness; correct the epoch explicitly.");
            if ((long)observations.Count + frontiers.Count >= Evaluation.Limits.MaxObservations) throw Capacity();
            observations.Add(observation);
            changed = true;
        }
        Dictionary<(ClaimReference Selector, string Partition), ObservationCompleteness> updatedFrontiers = [];
        foreach (var frontier in update.Completeness)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TraceObservationValidation.Register(frontier, updatedFrontiers) is { } invalid)
                throw new TraceMonitorUpdateException(invalid);
            var key = TraceObservationValidation.Key(frontier);
            if (frontiers.TryGetValue(key, out var previous))
            {
                if (frontier.ThroughStep < previous.ThroughStep || frontier.ThroughTicks < previous.ThroughTicks
                    || previous.Closure == ObservationClosure.Closed &&
                    (frontier.Closure != ObservationClosure.Closed || frontier.ThroughStep != previous.ThroughStep
                        || frontier.ThroughTicks != previous.ThroughTicks))
                    throw Reject(TraceEvaluationCodes.MonitorFrontierRegression, "/completeness", "Completeness cannot regress or revise a closed boundary within an epoch.");
                changed |= frontier != previous;
            }
            else
            {
                if ((long)observations.Count + frontiers.Count >= Evaluation.Limits.MaxObservations) throw Capacity();
                changed = true;
            }
            frontiers[key] = frontier;
        }
        if (update.Reference == prior.Reference && changed)
            throw Reject(TraceEvaluationCodes.InvalidObservation, "/reference", "Changed cumulative content requires a new exact alternative reference.");
        cancellationToken.ThrowIfCancellationRequested();
        if (update.Reference == prior.Reference) return this; // Every supplied record was validated; nothing changed.
        var alternative = new TraceObservationAlternative(update.Reference, observations.ToImmutable(),
            [.. frontiers.Values.OrderBy(x => x.Selector, ClaimReference.CanonicalComparer).ThenBy(x => x.Partition, StringComparer.Ordinal)]);
        var batch = Evaluation.Batch with { Alternatives = [alternative] };
        var evaluated = Evaluate(Evaluation.Claim, batch, Evaluation.Limits, cancellationToken);
        return new(evaluated, Reference());
    }

    /// <summary>Replaces the retained history in a new epoch, explicitly superseding the current snapshot.</summary>
    /// <param name="replacement">Complete replacement prefix and frontiers; missing old records are deliberately removed.</param>
    /// <param name="newEpoch">New exact epoch, different from the current one.</param>
    /// <param name="evidence">Exact evidence supporting the correction and complete replacement mapping.</param>
    /// <param name="cancellationToken">Cancellation rejects the replacement.</param>
    /// <returns>A new checkpoint linked to this one; the original evidence remains immutable and attributable.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="TraceMonitorUpdateException">Epoch or alternative identity is reused, or replacement evaluation cannot complete.</exception>
    /// <exception cref="OperationCanceledException">Evaluation is cancelled.</exception>
    /// <remarks>Claim, target, scope and observation contract remain pinned. Release or mapping changes require a new session;
    /// this method does not prove reconstruction fidelity or automatically invalidate externally retained assessments.</remarks>
    public TraceMonitor Correct(TraceObservationAlternative replacement, ClaimReference newEpoch,
        ClaimReference evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement); ArgumentNullException.ThrowIfNull(newEpoch);
        ArgumentNullException.ThrowIfNull(evidence);
        if (newEpoch == Evaluation.Batch.Epoch || replacement.Reference == Evaluation.Batch.Alternatives[0].Reference)
            throw Reject(TraceEvaluationCodes.InvalidObservation, "/epoch", "Corrections require new exact epoch and alternative references.");
        var batch = Evaluation.Batch with { Epoch = newEpoch, Alternatives = [replacement], EnumerationEvidence = evidence };
        return new(Evaluate(Evaluation.Claim, batch, Evaluation.Limits, cancellationToken), Reference(), evidence);
    }

    static TraceEvaluation Evaluate(ClaimDocument claim, TraceObservationBatch batch, TraceEvaluationLimits? limits,
        CancellationToken cancellation)
    {
        var result = TraceClaimEvaluator.Evaluate(claim, batch, limits, cancellation);
        if (result.Status != AssuranceAttemptStatus.Completed)
            throw new TraceMonitorUpdateException(result.Diagnostics.FirstOrDefault()
                ?? new(TraceEvaluationCodes.InvalidObservation, "/batch", "Monitor update could not be evaluated completely."));
        return result;
    }
    static TraceMonitorUpdateException Capacity() => Reject(TraceEvaluationCodes.BudgetExceeded, "/records",
        "Retained history or incoming update exceeds the explicit record limit; no records were accepted or evicted. Apply backpressure or start an explicitly reconfigured session.");
    static TraceMonitorUpdateException Reject(string code, string location, string message) => new(new(code, location, message));

    sealed record Checkpoint(string Schema, TraceEvaluation Evaluation, ClaimReference? Predecessor, ClaimReference? CorrectionEvidence);
    Checkpoint Payload() => new("cohesive-assurance-reference-monitor/v1", Evaluation, Predecessor, CorrectionEvidence);

    /// <summary>Serializes the accepted prefix, exact evaluation limits and correction lineage.</summary>
    /// <returns>Canonical checkpoint; the host must atomically persist it with its source delivery position.</returns>
    /// <exception cref="JsonException">Retained values cannot be encoded.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(Payload(), AssuranceJson.Options));

    /// <summary>Identifies this exact checkpoint, including its evaluation and predecessor.</summary>
    /// <returns>Lazily computed, thread-safe cached content fingerprint for checkpoint storage and correction lineage.</returns>
    /// <exception cref="JsonException">Retained values cannot be encoded.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public ClaimReference Reference() => reference.Value;

    ClaimReference CreateReference() => new(new("assurance-monitor://checkpoint"), "1",
        Convert.ToHexString(StrictDocumentJson.ComputeCanonicalSha256(Payload(), AssuranceJson.Options)));

    /// <summary>Restores a checkpoint and independently re-evaluates its retained trace.</summary>
    /// <param name="json">Canonical checkpoint from durable storage.</param>
    /// <returns>Monitor pinned to the restored claim, target, contract, limits and epoch; inspect these before binding a live source.</returns>
    /// <exception cref="TraceMonitorUpdateException">Checkpoint parsing, verification, profile or correction validation fails; Diagnostic retains a stable code.</exception>
    /// <remarks>A predecessor reference is provenance, not proof that the predecessor was retained or that an adapter's
    /// completeness declarations are true. The host must retain the referenced checkpoints separately.</remarks>
    public static TraceMonitor Restore(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, AssuranceJson.Options, "reference monitor checkpoint",
                out Checkpoint? checkpoint, out var error) || checkpoint is null || checkpoint.Evaluation is null)
            throw Reject(TraceEvaluationCodes.MonitorCheckpoint, error.Location, $"{error.Failure}: {error.Message}");
        var evaluation = checkpoint.Evaluation; // The TraceEvaluation converter independently verifies persisted results.
        if (checkpoint.Schema != "cohesive-assurance-reference-monitor/v1")
            throw Reject(TraceEvaluationCodes.MonitorCheckpoint, "/schema", "Unsupported monitor checkpoint schema.");
        if (evaluation.Status != AssuranceAttemptStatus.Completed || evaluation.Batch.Alternatives.Length != 1
            || evaluation.Batch.Enumeration != EnumerationCompleteness.Complete)
            throw Reject(TraceEvaluationCodes.MonitorProfile, "/evaluation", "Checkpoint does not satisfy the reference monitor mapping profile.");
        if (checkpoint.CorrectionEvidence is not null && (checkpoint.Predecessor is null
            || checkpoint.CorrectionEvidence != evaluation.Batch.EnumerationEvidence))
            throw Reject(TraceEvaluationCodes.MonitorCorrection, "/correctionEvidence", "Correction requires a predecessor and matching enumeration evidence.");
        return new(evaluation, checkpoint.Predecessor, checkpoint.CorrectionEvidence);
    }
}

/// <summary>Structured rejection of an entire monitor update; the accepted immutable prefix is unchanged.</summary>
public sealed class TraceMonitorUpdateException : InvalidOperationException
{
    /// <summary>Creates a structured rejection, including rejections from qualified observation adapters.</summary>
    /// <param name="diagnostic">Stable diagnostic identifying the failed ingestion boundary.</param>
    /// <exception cref="ArgumentNullException">Diagnostic is null.</exception>
    public TraceMonitorUpdateException(TraceEvaluationDiagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message) => Diagnostic = diagnostic;
    /// <summary>Stable diagnostic identifying invalid input, a continuity breach, unsupported interpretation or exhausted bounds.</summary>
    public TraceEvaluationDiagnostic Diagnostic { get; }
}

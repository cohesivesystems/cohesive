using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Cohesive.Assurance;
using OpenTelemetry.Trace;

namespace Cohesive.Adapters.OpenTelemetry.Assurance;

/// <summary>Explicit opt-in semantic observation events and atomic ingestion into the reference monitor.</summary>
/// <remarks>Ordinary spans do not establish semantic order, commit outcome or capture completeness.
/// This transport profile rejects completeness certificates; they require a separately qualified producer protocol.
/// Payloads may contain application values and must be explicitly selected by the host for export.</remarks>
public static class AssuranceActivityAdapter
{
    /// <summary>Optional activity source; excluded from generic core-instrumentation registration.</summary>
    public const string ActivitySourceName = "Cohesive.Assurance.Observations";
    /// <summary>Semantic event carrying one versioned observation envelope.</summary>
    public const string EventName = "cohesive.assurance.observations";
    /// <summary>String tag containing the canonical observation document.</summary>
    public const string PayloadTag = "cohesive.assurance.observations.json";
    /// <summary>Default maximum UTF-16 payload characters across a captured activity.</summary>
    public const int DefaultMaxPayloadCharacters = 1_048_576;
    /// <summary>Process-lifetime source; the adapter owns its lifetime. Hosts must not dispose it.</summary>
    public static ActivitySource Source { get; } = new(ActivitySourceName);

    /// <summary>Opts the native OpenTelemetry pipeline into semantic observation activities.</summary>
    /// <param name="builder">Native trace provider builder.</param>
    /// <returns>The same builder with the optional source registered.</returns>
    /// <exception cref="ArgumentNullException">Builder is null.</exception>
    public static TracerProviderBuilder AddCohesiveAssuranceInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(ActivitySourceName);
    }

    /// <summary>Creates an explicitly selected event without changing application execution or publishing completeness.</summary>
    /// <param name="observations">One completely enumerated mapping of the supplied rows, with no completeness frontiers.
    /// Complete mapping enumeration does not assert complete capture of production behavior.</param>
    /// <param name="maxPayloadCharacters">Positive UTF-16 character bound for this event. Apply independently bounds the
    /// whole activity; several valid events can exceed that total. Encoding checks length after serialization.</param>
    /// <returns>An activity event; add it to an activity from Source. Export and sampling remain host-controlled.</returns>
    /// <exception cref="ArgumentNullException">Observations are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Payload bound is nonpositive.</exception>
    /// <exception cref="TraceMonitorUpdateException">Envelope profile is invalid or payload exceeds its bound.</exception>
    /// <exception cref="JsonException">A portable observation cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public static ActivityEvent CreateEvent(TraceObservationBatch observations,
        int maxPayloadCharacters = DefaultMaxPayloadCharacters)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadCharacters);
        ValidateEnvelope(observations);
        var json = observations.Serialize();
        if (json.Length > maxPayloadCharacters) throw Capacity(TraceEvaluationCodes.TelemetryPayloadLimit);
        return new(EventName, tags: new ActivityTagsCollection { { PayloadTag, json } });
    }

    /// <summary>Consumes the semantic events of one captured activity as an atomic monitor update.</summary>
    /// <remarks>Envelope, binding and payload-limit rejection is permanent for unchanged input/configuration.
    /// Hosts must durably quarantine such deliveries and record a capture gap before acknowledging transport;
    /// rejection never advances the monitor. Cancellation or transient host persistence failures may be retried.</remarks>
    /// <param name="monitor">Accepted reference-monitor prefix.</param>
    /// <param name="activity">Captured activity from the explicit observation source. Span timing, status and Recorded flag
    /// are not semantic order, commit outcome or evidence of completeness.</param>
    /// <param name="cumulativeReference">Exact host-owned reference identifying the resulting cumulative alternative.</param>
    /// <param name="maxPayloadCharacters">Positive maximum UTF-16 payload characters across all semantic events.</param>
    /// <param name="cancellationToken">Cancellation rejects the entire activity.</param>
    /// <returns>The accepted monitor and every producer envelope reference in delivery order, including empty envelopes and duplicates.
    /// The host must persist its checkpoint and delivery position before acknowledging captured input.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Payload bound is nonpositive.</exception>
    /// <exception cref="TraceMonitorUpdateException">An envelope is malformed, exceeds bounds, carries completeness,
    /// mismatches the pinned binding, or the monitor rejects the resulting update.</exception>
    /// <exception cref="OperationCanceledException">Processing is cancelled.</exception>
    public static AssuranceActivityCapture Apply(TraceMonitor monitor, Activity activity, ClaimReference cumulativeReference,
        int maxPayloadCharacters = DefaultMaxPayloadCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor); ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(cumulativeReference);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadCharacters);
        cancellationToken.ThrowIfCancellationRequested();
        if (activity.Source.Name != ActivitySourceName)
            throw Reject(TraceEvaluationCodes.TelemetryBinding, "Activity source is not the explicit Assurance observation source.");
        var expected = monitor.Evaluation.Batch;
        var references = ImmutableArray.CreateBuilder<ClaimReference>();
        var observations = ImmutableArray.CreateBuilder<SemanticObservation>();
        long characters = 0;
        foreach (var item in activity.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Name != EventName) continue;
            string? payload = null;
            foreach (var tag in item.Tags)
            {
                if (tag.Key != PayloadTag) continue;
                if (payload is not null || tag.Value is not string value)
                    throw Reject(TraceEvaluationCodes.TelemetryEnvelope, "Semantic event requires exactly one string payload tag.");
                payload = value;
            }
            if (payload is null) throw Reject(TraceEvaluationCodes.TelemetryEnvelope, "Semantic event has no canonical payload.");
            characters += payload.Length;
            if (characters > maxPayloadCharacters) throw Capacity(TraceEvaluationCodes.TelemetryPayloadLimit);
            TraceObservationBatch batch;
            try { batch = TraceObservationBatch.Deserialize(payload); }
            catch (JsonException error) { throw Reject(TraceEvaluationCodes.TelemetryEnvelope, error.Message); }
            ValidateEnvelope(batch);
            if (batch.Target != expected.Target || batch.Epoch != expected.Epoch
                || batch.EnumerationEvidence != expected.EnumerationEvidence
                || !expected.Contract.SameContent(batch.Contract))
                throw Reject(TraceEvaluationCodes.TelemetryBinding, "Telemetry does not match the monitor's exact contract, target, epoch and mapping evidence.");
            var rows = batch.Alternatives[0].Observations;
            if ((long)observations.Count + rows.Length > monitor.Evaluation.Limits.MaxObservations) throw Capacity(TraceEvaluationCodes.BudgetExceeded);
            references.Add(batch.Alternatives[0].Reference);
            observations.AddRange(rows);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = observations.Count == 0 ? monitor
            : monitor.Advance(new(cumulativeReference, observations.ToImmutable(), []), cancellationToken);
        return new(accepted, references.ToImmutable());
    }

    static void ValidateEnvelope(TraceObservationBatch batch)
    {
        if (batch.Contract is null || batch.Target is null || batch.Epoch is null || batch.EnumerationEvidence is null
            || batch.Enumeration != EnumerationCompleteness.Complete || batch.Alternatives.IsDefault
            || batch.Alternatives.Length != 1 || batch.Alternatives[0] is not { } alternative
            || alternative.Reference is null || alternative.Observations.IsDefault || alternative.Completeness.IsDefault
            || !alternative.Completeness.IsEmpty)
            throw Reject(TraceEvaluationCodes.TelemetryEnvelope, "Telemetry requires one complete mapping with initialized rows and no completeness certificates.");
    }
    static TraceMonitorUpdateException Capacity(string code) => Reject(code,
        "Semantic telemetry exceeds the explicit payload or observation bound; no observations were accepted.");
    static TraceMonitorUpdateException Reject(string code, string message) => TraceMonitorUpdateException.FromDiagnostic(new(code, "/telemetry", message));
}

/// <summary>Accepted activity ingestion and producer references for host-owned delivery provenance.</summary>
/// <param name="Monitor">Accepted immutable monitor; unchanged when no new semantic rows were supplied.</param>
/// <param name="EnvelopeReferences">Exact producer references in event order, including duplicates and empty envelopes.
/// References are retained assertions, not verification of their fingerprints. Persist them with delivery position and checkpoint.</param>
public sealed record AssuranceActivityCapture(TraceMonitor Monitor, ImmutableArray<ClaimReference> EnvelopeReferences);

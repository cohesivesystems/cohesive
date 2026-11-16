using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Adapters.OpenTelemetry.Assurance;
using Cohesive.Assurance;
using Cohesive.Model.Serialization;

namespace Cohesive.Examples.Assurance;

// Reference host, deliberately internal: single local writer, bounded journal, process-crash recovery.
// The source must provide stable, contiguous delivery positions and redelivery until acknowledgment.
// Atomic same-directory rename is required. Power-loss/directory-fsync guarantees are NOT established.
internal sealed class FileCaptureHost : IDisposable
{
    internal enum PersistencePoint { BeforePublish, AfterPublish }
    internal sealed record Receipt(long Position, string RawActivity, ClaimReference CumulativeReference,
        string Checkpoint, ImmutableArray<ClaimReference> EnvelopeReferences, TraceEvaluationDiagnostic? Rejection);
    sealed record Journal(string Schema, string Stream, string InitialCheckpoint, ImmutableArray<Receipt> Receipts);
    sealed record RawEvent(string Name, KeyValuePair<string, object?>[] Tags);
    sealed record RawActivity(string Source, string TraceId, string SpanId, RawEvent[] Events);

    const string Schema = "cohesive-assurance-file-capture-example/v1";
    static readonly System.Text.Json.JsonSerializerOptions Json = StrictDocumentJson.CreateOptions();
    readonly string path;
    readonly FileStream ownership;
    readonly int maxDeliveries;
    readonly int maxJournalBytes;
    readonly object gate = new();
    readonly Action<PersistencePoint>? fault;
    Journal journal;
    bool unavailable;

    internal TraceMonitor Monitor { get; private set; }
    internal long Position => journal.Receipts.Length;
    internal bool HasCaptureGaps => journal.Receipts.Any(x => x.Rejection is not null);

    internal FileCaptureHost(string directory, string stream, TraceMonitor initial,
        int maxDeliveries = 256, int maxJournalBytes = 16 * 1024 * 1024,
        Action<PersistencePoint>? fault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeliveries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxJournalBytes);
        // This lossy-capture example cannot inherit certificates that purport to cover a later gap.
        if (!initial.Evaluation.Batch.Alternatives[0].Completeness.IsEmpty)
            throw new ArgumentException("Capture host requires an uncertified initial scope.", nameof(initial));
        this.maxDeliveries = maxDeliveries;
        this.maxJournalBytes = maxJournalBytes;
        this.fault = fault;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "capture.json");
        ownership = new FileStream(Path.Combine(directory, "capture.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var initialCheckpoint = initial.Serialize();
            journal = new(Schema, stream, initialCheckpoint, []);
            Monitor = initial;
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > maxJournalBytes) throw new InvalidDataException("Journal exceeds configured bound.");
            var text = File.ReadAllText(path);
            if (!StrictDocumentJson.TryReadCanonicalObject(text, Json, "capture journal", out Journal? restored, out _)
                || restored is null || restored.Schema != Schema || restored.Stream != stream
                || restored.InitialCheckpoint != initialCheckpoint || restored.Receipts.IsDefault
                || restored.Receipts.Length > maxDeliveries)
                throw new InvalidDataException("Journal schema, source, initial checkpoint or capacity is incompatible.");
            // Every entry retains its exact monitor artifact; reject corrupt checkpoints rather than skipping history.
            for (var i = 0; i < restored.Receipts.Length; i++)
            {
                var receipt = restored.Receipts[i];
                if (receipt is null || receipt.Position != i + 1 || receipt.RawActivity is null
                    || receipt.CumulativeReference is null || receipt.EnvelopeReferences.IsDefault)
                    throw new InvalidDataException("Journal receipt is incomplete or out of order.");
                if (receipt.Rejection is not null)
                {
                    if (!IsPermanent(receipt.Rejection.Code) || receipt.Checkpoint != Monitor.Serialize()
                        || !receipt.EnvelopeReferences.IsEmpty)
                        throw new InvalidDataException("Quarantine receipt changed the accepted monitor.");
                }
                else
                {
                    var next = TraceMonitor.Restore(receipt.Checkpoint);
                    if (next.Evaluation.Batch.Alternatives[0].Reference != receipt.CumulativeReference
                        || next.Evaluation.Claim.Fingerprint != initial.Evaluation.Claim.Fingerprint
                        || next.Evaluation.Batch.Target != initial.Evaluation.Batch.Target
                        || next.Evaluation.Batch.Epoch != initial.Evaluation.Batch.Epoch
                        || next.Evaluation.Limits != initial.Evaluation.Limits
                        || next.Evaluation.Batch.EnumerationEvidence != initial.Evaluation.Batch.EnumerationEvidence
                        || (receipt.Checkpoint != Monitor.Serialize() && next.Predecessor != Monitor.Reference())
                        || next.CorrectionEvidence is not null
                        || !next.Evaluation.Batch.Contract.SameContent(initial.Evaluation.Batch.Contract)
                        || !next.Evaluation.Batch.Alternatives[0].Completeness.IsEmpty)
                        throw new InvalidDataException("Receipt checkpoint binding is invalid.");
                    Monitor = next;
                }
            }
            journal = restored;
        }
        catch { ownership.Dispose(); throw; }
    }

    // Return only after the receipt is committed and acknowledgment succeeds. If acknowledgment fails,
    // redelivery finds the durable receipt and retries acknowledgment without re-evaluating the observations.
    internal Receipt Process(long position, Activity activity, ClaimReference cumulativeReference,
        Action<long> acknowledge, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(unavailable, this);
            ArgumentNullException.ThrowIfNull(acknowledge);
            ArgumentNullException.ThrowIfNull(activity);
            ArgumentNullException.ThrowIfNull(cumulativeReference);
            cancellationToken.ThrowIfCancellationRequested();
            var raw = StrictDocumentJson.GetCanonicalText(new RawActivity(activity.Source.Name,
                activity.TraceId.ToString(), activity.SpanId.ToString(),
                [.. activity.Events.Select(x => new RawEvent(x.Name, [.. x.Tags]))]), Json);
            if (position <= 0 || position > Position + 1) throw new InvalidOperationException("Delivery positions must be contiguous and start at one.");
            if (position <= Position)
            {
                var prior = journal.Receipts[(int)position - 1];
                if (prior.RawActivity != raw || prior.CumulativeReference != cumulativeReference)
                    throw new InvalidOperationException("Redelivery changed content or its cumulative reference.");
                acknowledge(position);
                return prior;
            }
            if (Position >= maxDeliveries) throw new InvalidOperationException("Journal delivery capacity reached; apply backpressure.");
            TraceMonitor next = Monitor;
            ImmutableArray<ClaimReference> references = [];
            TraceEvaluationDiagnostic? rejection = null;
            try
            {
                var capture = AssuranceActivityAdapter.Apply(Monitor, activity, cumulativeReference,
                    cancellationToken: cancellationToken);
                next = capture.Monitor;
                references = capture.EnvelopeReferences;
            }
            catch (TraceObservationException error) when (IsPermanent(error.Diagnostic.Code))
            {
                // Retain the complete input and diagnostic in the SAME commit as the source cursor.
                // A rejected activity never modifies the semantic monitor or establishes completeness.
                rejection = error.Diagnostic;
            }
            var receipt = new Receipt(position, raw, cumulativeReference, next.Serialize(), references, rejection);
            var updated = journal with { Receipts = journal.Receipts.Add(receipt) };
            var bytes = StrictDocumentJson.GetCanonicalBytes(updated, Json);
            if (bytes.Length > maxJournalBytes) throw new InvalidOperationException("Journal byte capacity reached; apply backpressure.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A leftover pending file is never a commit. Reopening uses capture.json only.
                using (var pending = new FileStream(path + ".pending", FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
                {
                    pending.Write(bytes);
                    pending.Flush(flushToDisk: true);
                }
                fault?.Invoke(PersistencePoint.BeforePublish);
                File.Move(path + ".pending", path, overwrite: true);
                fault?.Invoke(PersistencePoint.AfterPublish);
                journal = updated;
                Monitor = next;
            }
            catch
            {
                // Publication outcome may be ambiguous. Require reopen before processing or acknowledging anything else.
                unavailable = true;
                throw;
            }
            acknowledge(position);
            return receipt;
        }
    }

    static bool IsPermanent(string code) => code is TraceEvaluationCodes.TelemetryEnvelope
        or TraceEvaluationCodes.TelemetryBinding or TraceEvaluationCodes.TelemetrySource
        or TraceEvaluationCodes.TelemetryPayloadLimit;

    public void Dispose()
    {
        lock (gate) { unavailable = true; ownership.Dispose(); }
    }
}

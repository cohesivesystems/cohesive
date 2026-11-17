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
    sealed record Commit(string Schema, string Stream, ClaimReference InitialCheckpoint, Receipt Receipt);
    sealed record RawEvent(string Name, KeyValuePair<string, object?>[] Tags);
    sealed record RawActivity(string Source, string TraceId, string SpanId, RawEvent[] Events);

    const string Schema = "cohesive-assurance-file-capture-example/v2";
    static readonly System.Text.Json.JsonSerializerOptions Json = StrictDocumentJson.CreateOptions();
    readonly string directory;
    readonly string stream;
    readonly ClaimReference initialReference;
    readonly FileStream ownership;
    readonly int maxDeliveries;
    readonly int maxJournalBytes;
    readonly object gate = new();
    readonly Action<PersistencePoint>? fault;
    readonly List<Receipt> receipts = [];
    bool unavailable;

    internal TraceMonitor Monitor { get; private set; }
    internal long Position => receipts.Count;
    // Total bytes in immutable committed files; pending and lock files are not retained journal content.
    internal long CommittedBytes { get; private set; }
    internal bool HasCaptureGaps => receipts.Any(x => x.Rejection is not null);

    internal FileCaptureHost(string directory, string stream, TraceMonitor initial,
        int maxDeliveries = 256, int maxJournalBytes = 16 * 1024 * 1024,
        Action<PersistencePoint>? fault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeliveries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxJournalBytes);
        // This lossy-capture example cannot inherit certificates that purport to cover a later gap.
        if (!initial.Evaluation.Batch.Alternatives[0].Completeness.IsEmpty)
            throw new ArgumentException("Capture host requires an uncertified initial scope.", nameof(initial));
        this.maxDeliveries = maxDeliveries;
        this.maxJournalBytes = maxJournalBytes;
        this.fault = fault;
        Directory.CreateDirectory(directory);
        this.directory = directory;
        this.stream = stream;
        initialReference = initial.Reference();
        ownership = new FileStream(Path.Combine(directory, "capture.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            Monitor = initial;
            // Never reinterpret a v1 snapshot as an empty v2 journal or overwrite it.
            if (File.Exists(Path.Combine(directory, "capture.json")))
                throw new InvalidDataException("Legacy v1 journal requires explicit migration or a new directory.");
            List<string> files = [];
            foreach (var file in Directory.EnumerateFiles(directory, "capture-*.json"))
            {
                if (files.Count == maxDeliveries) throw new InvalidDataException("Journal delivery bound exceeded.");
                files.Add(file);
            }
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var expectedPosition = receipts.Count + 1L;
                if (Path.GetFileName(file) != CommitName(expectedPosition))
                    throw new InvalidDataException("Committed receipt sequence has a gap or an invalid filename.");
                var length = new FileInfo(file).Length;
                if (length > maxJournalBytes - CommittedBytes)
                    throw new InvalidDataException("Journal exceeds configured byte bound.");
                var text = File.ReadAllText(file);
                if (!StrictDocumentJson.TryReadCanonicalObject(text, Json, "capture commit", out Commit? commit, out _)
                    || commit is null || commit.Schema != Schema || commit.Stream != stream
                    || commit.InitialCheckpoint != initialReference)
                    throw new InvalidDataException("Commit schema, source or initial checkpoint is incompatible.");
                var receipt = commit.Receipt;
                if (receipt is null || receipt.Position != expectedPosition || receipt.RawActivity is null
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
                receipts.Add(receipt);
                CommittedBytes += length;
            }
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
                var prior = receipts[(int)position - 1];
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
            var bytes = StrictDocumentJson.GetCanonicalBytes(new Commit(Schema, stream, initialReference, receipt), Json);
            if (bytes.Length > maxJournalBytes - CommittedBytes) throw new InvalidOperationException("Journal byte capacity reached; apply backpressure.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The pending file is never a commit. Numbered files are immutable publication units.
                var pendingPath = Path.Combine(directory, "capture.pending");
                using (var pending = new FileStream(pendingPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
                {
                    pending.Write(bytes);
                    pending.Flush(flushToDisk: true);
                }
                fault?.Invoke(PersistencePoint.BeforePublish);
                File.Move(pendingPath, Path.Combine(directory, CommitName(position)), overwrite: false);
                fault?.Invoke(PersistencePoint.AfterPublish);
                receipts.Add(receipt);
                CommittedBytes += bytes.Length;
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

    internal static string CommitName(long position) => $"capture-{position.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)}.json";

    static bool IsPermanent(string code) => code is TraceEvaluationCodes.TelemetryEnvelope
        or TraceEvaluationCodes.TelemetryBinding or TraceEvaluationCodes.TelemetrySource
        or TraceEvaluationCodes.TelemetryPayloadLimit;

    public void Dispose()
    {
        lock (gate) { unavailable = true; ownership.Dispose(); }
    }
}

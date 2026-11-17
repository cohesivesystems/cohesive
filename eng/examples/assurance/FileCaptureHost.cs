using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Adapters.OpenTelemetry.Assurance;
using Cohesive.Assurance;
using Cohesive.Model.Serialization;

namespace Cohesive.Examples.Assurance;

// Single local writer, bounded receipt history, process-crash recovery under atomic local rename.
// Power-loss/directory-fsync guarantees are NOT established. Compaction never supplies completeness.
internal sealed class FileCaptureHost : IDisposable
{
    internal enum PersistencePoint
    {
        BeforePublish, AfterPublish, BeforeCheckpointPublish, AfterCheckpointPublish,
        BeforeCompactionDelete, AfterCompactionDelete
    }
    internal sealed record Receipt(long Position, string RawActivity, ClaimReference CumulativeReference,
        ClaimReference Checkpoint, ImmutableArray<ClaimReference> EnvelopeReferences, TraceEvaluationDiagnostic? Rejection);
    sealed record Commit(string Schema, string Stream, ClaimReference InitialCheckpoint, Receipt Receipt, string? Delta);
    sealed record Checkpoint(string Schema, string Stream, ClaimReference InitialCheckpoint,
        string Monitor, ImmutableArray<Receipt> Receipts);
    sealed record RawEvent(string Name, KeyValuePair<string, object?>[] Tags);
    sealed record RawActivity(string Source, string TraceId, string SpanId, RawEvent[] Events);

    const string Schema = "cohesive-assurance-file-capture-example/v3";
    internal const string CheckpointFile = "capture-checkpoint.json";
    static readonly System.Text.Json.JsonSerializerOptions Json = StrictDocumentJson.CreateOptions();
    readonly string directory;
    readonly string stream;
    readonly ClaimReference initialReference;
    readonly FileStream ownership;
    readonly int maxDeliveries;
    readonly int maxJournalBytes;
    readonly int checkpointInterval;
    readonly object gate = new();
    readonly Action<PersistencePoint>? fault;
    readonly List<Receipt> receipts = [];
    bool unavailable;
    long checkpointBytes;

    internal TraceMonitor Monitor { get; private set; }
    internal long Position => receipts.Count;
    // Actual retained committed bytes; pending and lock files are excluded.
    internal long CommittedBytes { get; private set; }
    // Monotone per-open-instance logical published bytes, including replacement checkpoints.
    internal long PublishedBytes { get; private set; }
    internal bool HasCaptureGaps => receipts.Any(x => x.Rejection is not null);

    internal FileCaptureHost(string directory, string stream, TraceMonitor initial,
        int maxDeliveries = 256, int maxJournalBytes = 16 * 1024 * 1024,
        Action<PersistencePoint>? fault = null, int checkpointInterval = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeliveries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxJournalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointInterval);
        if (!initial.Evaluation.Batch.Alternatives[0].Completeness.IsEmpty)
            throw new ArgumentException("Capture host requires an uncertified initial scope.", nameof(initial));
        this.maxDeliveries = maxDeliveries;
        this.maxJournalBytes = maxJournalBytes;
        this.checkpointInterval = checkpointInterval;
        this.fault = fault;
        this.directory = directory;
        this.stream = stream;
        initialReference = initial.Reference();
        Directory.CreateDirectory(directory);
        ownership = new FileStream(Path.Combine(directory, "capture.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            Monitor = initial;
            if (File.Exists(Path.Combine(directory, "capture.json")))
                throw new InvalidDataException("Legacy journal requires explicit migration or a new directory.");
            var files = CommitFiles();
            var checkpointPath = Path.Combine(directory, CheckpointFile);
            checkpointBytes = File.Exists(checkpointPath) ? new FileInfo(checkpointPath).Length : 0;
            CommittedBytes = checkpointBytes;
            if (CommittedBytes > maxJournalBytes) throw new InvalidDataException("Journal exceeds configured byte bound.");
            foreach (var file in files)
            {
                var length = new FileInfo(file).Length;
                if (length > maxJournalBytes - CommittedBytes) throw new InvalidDataException("Journal exceeds configured byte bound.");
                CommittedBytes += length;
            }
            if (File.Exists(checkpointPath))
            {
                var checkpoint = Read<Checkpoint>(checkpointPath);
                ValidateBinding(checkpoint.Schema, checkpoint.Stream, checkpoint.InitialCheckpoint);
                if (checkpoint.Receipts.IsDefaultOrEmpty || checkpoint.Receipts.Length > maxDeliveries)
                    throw new InvalidDataException("Checkpoint receipt history is invalid.");
                var expected = initialReference;
                var expectedAlternative = initial.Evaluation.Batch.Alternatives[0].Reference;
                foreach (var receipt in checkpoint.Receipts)
                {
                    ValidateReceipt(receipt, receipts.Count + 1L);
                    if (receipt.Rejection is not null && receipt.Checkpoint != expected)
                        throw new InvalidDataException("Quarantine receipt changed the accepted checkpoint.");
                    if (receipt.Rejection is null) expectedAlternative = receipt.CumulativeReference;
                    expected = receipt.Checkpoint;
                    receipts.Add(receipt);
                }
                Monitor = TraceMonitor.Restore(checkpoint.Monitor);
                if (Monitor.Reference() != expected
                    || Monitor.Evaluation.Batch.Alternatives[0].Reference != expectedAlternative
                    || Monitor.Evaluation.Claim.Fingerprint != initial.Evaluation.Claim.Fingerprint
                    || Monitor.Evaluation.Batch.Target != initial.Evaluation.Batch.Target
                    || Monitor.Evaluation.Batch.Epoch != initial.Evaluation.Batch.Epoch
                    || Monitor.Evaluation.Limits != initial.Evaluation.Limits
                    || Monitor.Evaluation.Batch.EnumerationEvidence != initial.Evaluation.Batch.EnumerationEvidence
                    || Monitor.CorrectionEvidence is not null && Monitor.Reference() != initialReference
                    || !Monitor.Evaluation.Batch.Contract.SameContent(initial.Evaluation.Batch.Contract)
                    || !Monitor.Evaluation.Batch.Alternatives[0].Completeness.IsEmpty)
                    throw new InvalidDataException("Checkpoint monitor binding is invalid.");
            }
            var coveredThrough = Position;
            foreach (var file in files)
            {
                var position = FilePosition(file);
                if (position <= coveredThrough) continue; // Published checkpoint is authoritative for this prefix.
                var commit = Read<Commit>(file);
                ValidateBinding(commit.Schema, commit.Stream, commit.InitialCheckpoint);
                ValidateReceipt(commit.Receipt, Position + 1);
                if (position != commit.Receipt.Position) throw new InvalidDataException("Commit filename and position disagree.");
                if (commit.Receipt.Rejection is not null)
                {
                    if (commit.Delta is not null) throw new InvalidDataException("Quarantine cannot carry an accepted delta.");
                }
                else
                {
                    if (commit.Delta is null) throw new InvalidDataException("Accepted commit lacks its replay delta.");
                    var delta = TraceObservationBatch.Deserialize(commit.Delta);
                    delta.ValidateCapture();
                    if (delta.Alternatives[0].Reference != commit.Receipt.CumulativeReference)
                        throw new InvalidDataException("Delta reference differs from receipt.");
                    Monitor = Monitor.AdvanceCaptured([delta], commit.Receipt.CumulativeReference).Monitor;
                }
                if (Monitor.Reference() != commit.Receipt.Checkpoint)
                    throw new InvalidDataException("Replayed delta changed the retained checkpoint identity.");
                receipts.Add(commit.Receipt);
            }
            // Only after the complete surviving tail is validated may interrupted cleanup resume.
            DeleteCovered(files, coveredThrough);
        }
        catch { ownership.Dispose(); throw; }
    }

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
            catch (TraceObservationException error) when (IsPermanent(error.Diagnostic.Code)) { rejection = error.Diagnostic; }

            // Derive the replay delta from the accepted prefix, never from a second interpretation of activity tags.
            // The monitor is append-only in this capture profile; duplicate deliveries add no rows.
            string? delta = null;
            if (rejection is null)
            {
                var batch = next.Evaluation.Batch;
                var alternative = batch.Alternatives[0];
                delta = (batch with { Alternatives = [alternative with {
                    Observations = alternative.Observations[Monitor.Evaluation.Batch.Alternatives[0].Observations.Length..] }] }).Serialize();
            }
            var receipt = new Receipt(position, raw, cumulativeReference, next.Reference(), references, rejection);
            var bytes = StrictDocumentJson.GetCanonicalBytes(new Commit(Schema, stream, initialReference, receipt, delta), Json);
            if (bytes.Length > maxJournalBytes - CommittedBytes) throw new InvalidOperationException("Journal byte capacity reached; apply backpressure.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Publish(bytes, CommitName(position), PersistencePoint.BeforePublish, PersistencePoint.AfterPublish);
                receipts.Add(receipt);
                Monitor = next;
                if (Position % checkpointInterval == 0) Compact();
            }
            catch
            {
                // Commit/checkpoint may have published. Require reopen; never acknowledge an ambiguous local state.
                unavailable = true;
                throw;
            }
            acknowledge(position);
            return receipt;
        }
    }

    void Compact()
    {
        var bytes = StrictDocumentJson.GetCanonicalBytes(new Checkpoint(Schema, stream, initialReference,
            Monitor.Serialize(), [.. receipts]), Json);
        // Preserve the peak committed-byte bound while old logs coexist with the new checkpoint.
        // Lack of headroom skips optional compaction; the already-published delivery is still safe to acknowledge.
        if (bytes.Length > maxJournalBytes - CommittedBytes + checkpointBytes) return;
        Publish(bytes, CheckpointFile, PersistencePoint.BeforeCheckpointPublish, PersistencePoint.AfterCheckpointPublish);
        checkpointBytes = bytes.Length;
        DeleteCovered(CommitFiles(), Position);
    }

    void Publish(byte[] bytes, string name, PersistencePoint before, PersistencePoint after)
    {
        var pendingPath = Path.Combine(directory, "capture.pending");
        using (var pending = new FileStream(pendingPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
        {
            pending.Write(bytes);
            pending.Flush(flushToDisk: true);
        }
        fault?.Invoke(before);
        var replacesCheckpoint = name == CheckpointFile;
        File.Move(pendingPath, Path.Combine(directory, name), overwrite: replacesCheckpoint);
        CommittedBytes += bytes.Length - (replacesCheckpoint ? checkpointBytes : 0);
        PublishedBytes += bytes.Length;
        fault?.Invoke(after);
    }

    void DeleteCovered(List<string> files, long through)
    {
        foreach (var file in files)
        {
            if (FilePosition(file) > through) continue;
            var length = new FileInfo(file).Length;
            fault?.Invoke(PersistencePoint.BeforeCompactionDelete);
            File.Delete(file);
            CommittedBytes -= length;
            fault?.Invoke(PersistencePoint.AfterCompactionDelete);
        }
    }

    List<string> CommitFiles()
    {
        List<string> files = [];
        foreach (var file in Directory.EnumerateFiles(directory, "capture-*.json"))
        {
            if (Path.GetFileName(file) == CheckpointFile) continue;
            if (files.Count == maxDeliveries) throw new InvalidDataException("Journal delivery bound exceeded.");
            FilePosition(file); // Reject malformed names even if a checkpoint would otherwise cover them.
            files.Add(file);
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    static long FilePosition(string file)
    {
        var name = Path.GetFileName(file);
        if (name.Length != "capture-0000000000.json".Length
            || !long.TryParse(name.AsSpan(8, 10), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var position) || position <= 0 || name != CommitName(position))
            throw new InvalidDataException("Committed receipt filename is invalid.");
        return position;
    }

    static T Read<T>(string path) where T : class
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(File.ReadAllText(path), Json, "capture artifact", out T? value, out _)
            || value is null) throw new InvalidDataException("Capture artifact is malformed or incompatible.");
        return value;
    }

    void ValidateBinding(string schema, string source, ClaimReference initial)
    {
        if (schema != Schema || source != stream || initial != initialReference)
            throw new InvalidDataException("Capture schema, source or initial checkpoint is incompatible.");
    }

    static void ValidateReceipt(Receipt receipt, long position)
    {
        if (receipt is null || receipt.Position != position || receipt.RawActivity is null
            || receipt.CumulativeReference is null || receipt.Checkpoint is null || receipt.EnvelopeReferences.IsDefault
            || receipt.Rejection is not null && (!IsPermanent(receipt.Rejection.Code) || !receipt.EnvelopeReferences.IsEmpty))
            throw new InvalidDataException("Capture receipt is incomplete, out of order or has an invalid quarantine disposition.");
    }

    internal static string CommitName(long position) => $"capture-{position.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)}.json";
    static bool IsPermanent(string code) => code is TraceEvaluationCodes.TelemetryEnvelope
        or TraceEvaluationCodes.TelemetryBinding or TraceEvaluationCodes.TelemetrySource or TraceEvaluationCodes.TelemetryPayloadLimit;
    public void Dispose() { lock (gate) { unavailable = true; ownership.Dispose(); } }
}

using Cohesive.Assurance;
using Cohesive.Adapters.OpenTelemetry.Assurance;
using Cohesive.Examples.Assurance;

namespace Cohesive.Adapters.OpenTelemetry.Tests;

public sealed partial class AssuranceActivityTests
{
    sealed class CaptureDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assurance-capture-" + Guid.NewGuid());
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    [Fact]
    public void CaptureHostPublishesCheckpointPositionAndProvenanceBeforeAcknowledgment()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var paid = Capture(Batch(Event("paid", 1)));
        using var shipped = Capture(Batch(Event("shipped", 2, shipped: true)));
        string committed;
        using (var host = new FileCaptureHost(directory.Path, "source", initial))
        {
            var receipt = host.Process(1, paid, R("paid-checkpoint"), position =>
            {
                Assert.True(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(1))));
                Assert.Equal(1, position);
                Assert.Equal(1, host.Position);
            });
            Assert.Equal(R("producer-envelope"), Assert.Single(receipt.EnvelopeReferences));
            Assert.Contains(paid.TraceId.ToString(), receipt.RawActivity);
            committed = host.Monitor.Serialize();
        }
        using var resumed = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(1, resumed.Position);
        Assert.Equal(committed, resumed.Monitor.Serialize());
        resumed.Process(2, shipped, R("shipped-checkpoint"), _ => { });
        Assert.Equal(ClaimVerdict.Satisfied, Assert.Single(resumed.Monitor.Evaluation.Alternatives[0].Obligations).Verdict);
        Assert.Equal(ClaimVerdict.Inconclusive, resumed.Monitor.Evaluation.Verdict);
        Assert.False(resumed.HasCaptureGaps);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void CaptureHostReopensAtTheAtomicPublicationBoundary(int boundary, int recoveredPosition)
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        var acknowledgments = 0;
        using (var failed = new FileCaptureHost(directory.Path, "source", initial,
            fault: reached => { if (reached == (FileCaptureHost.PersistencePoint)boundary) throw new IOException("Injected process boundary failure."); }))
        {
            Assert.Throws<IOException>(() => failed.Process(1, activity, R("accepted"), _ => acknowledgments++));
            Assert.Equal(0, acknowledgments);
            Assert.Throws<ObjectDisposedException>(() => failed.Process(1, activity, R("accepted"), _ => acknowledgments++));
        }
        using var resumed = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(recoveredPosition, resumed.Position);
        resumed.Process(1, activity, R("accepted"), _ => acknowledgments++);
        Assert.Equal(1, acknowledgments);
        Assert.Single(resumed.Monitor.Evaluation.Batch.Alternatives[0].Observations);
    }

    [Fact]
    public void FailedAppendPreservesThePreviouslyCommittedCursorAndCheckpoint()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var paid = Capture(Batch(Event("paid", 1)));
        using var shipped = Capture(Batch(Event("shipped", 2, shipped: true)));
        using (var first = new FileCaptureHost(directory.Path, "source", initial))
            first.Process(1, paid, R("first"), _ => { });
        using (var failed = new FileCaptureHost(directory.Path, "source", initial,
            fault: point => { if (point == FileCaptureHost.PersistencePoint.BeforePublish) throw new IOException(); }))
            Assert.Throws<IOException>(() => failed.Process(2, shipped, R("second"), _ => Assert.Fail("Must not ack.")));
        using var resumed = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(1, resumed.Position);
        Assert.Equal(R("first"), resumed.Monitor.Evaluation.Batch.Alternatives[0].Reference);
        resumed.Process(2, shipped, R("second"), _ => { });
        Assert.Equal(2, resumed.Position);
    }

    [Fact]
    public void MonitorAndJournalByteExhaustionApplyBackpressureInsteadOfQuarantine()
    {
        using var directory = new CaptureDirectory();
        using var tinyDirectory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch(), new(MaxObservations: 1));
        using var first = Capture(Batch(Event("paid", 1)));
        using var second = Capture(Batch(Event("shipped", 2, shipped: true)));
        using var host = new FileCaptureHost(directory.Path, "source", initial);
        host.Process(1, first, R("first"), _ => { });
        Assert.Equal(TraceEvaluationCodes.BudgetExceeded, Assert.Throws<TraceObservationException>(() =>
            host.Process(2, second, R("second"), _ => Assert.Fail("Must not ack."))).Diagnostic.Code);
        Assert.Equal(1, host.Position);
        Assert.False(host.HasCaptureGaps);
        using var tiny = new FileCaptureHost(tinyDirectory.Path, "source", initial, maxJournalBytes: 1);
        Assert.Throws<InvalidOperationException>(() => tiny.Process(1, first, R("first"), _ => Assert.Fail("Must not ack.")));
        Assert.Equal(0, tiny.Position);
        Assert.False(File.Exists(Path.Combine(tinyDirectory.Path, FileCaptureHost.CommitName(1))));
    }

    [Fact]
    public void RestoreRejectsDifferentSourceAndCorruptJournal()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        using (var host = new FileCaptureHost(directory.Path, "source", initial))
            host.Process(1, activity, R("first"), _ => { });
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "other-source", initial));
        File.WriteAllText(Path.Combine(directory.Path, FileCaptureHost.CommitName(1)), "broken");
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "source", initial));
    }

    [Fact]
    public void AppendsNeverRewriteCommittedFilesAndRestoredByteBoundsCoverTheWholeLog()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var paid = Capture(Batch(Event("paid", 1)));
        using var shipped = Capture(Batch(Event("shipped", 2, shipped: true)));
        long committedBytes;
        using (var host = new FileCaptureHost(directory.Path, "source", initial))
        {
            host.Process(1, paid, R("first"), _ => { });
            var first = Path.Combine(directory.Path, FileCaptureHost.CommitName(1));
            var original = File.ReadAllBytes(first);
            var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(first, sentinel);
            host.Process(2, shipped, R("second"), _ => { });
            Assert.Equal(original, File.ReadAllBytes(first));
            Assert.Equal(sentinel, File.GetLastWriteTimeUtc(first));
            committedBytes = Directory.GetFiles(directory.Path, "capture-*.json").Sum(x => new FileInfo(x).Length);
            Assert.Equal(committedBytes, host.CommittedBytes);
        }
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "source", initial,
            maxJournalBytes: checked((int)committedBytes - 1)));
        using (var full = new FileCaptureHost(directory.Path, "source", initial, maxJournalBytes: checked((int)committedBytes)))
        {
            Assert.Throws<InvalidOperationException>(() => full.Process(3, shipped, R("third"), _ => Assert.Fail("Must not ack.")));
            Assert.Equal(2, full.Position);
            Assert.Equal(committedBytes, full.CommittedBytes);
            Assert.False(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(3))));
        }
        using var reopened = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(committedBytes, reopened.CommittedBytes);
        Assert.Equal(2, reopened.Position);
    }

    [Fact]
    public void RestoreIgnoresTornPendingFilesButRejectsMissingCommittedPositions()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        using (var host = new FileCaptureHost(directory.Path, "source", initial))
            host.Process(1, activity, R("first"), _ => { });
        File.WriteAllText(Path.Combine(directory.Path, "capture.pending"), "{torn");
        using (var restored = new FileCaptureHost(directory.Path, "source", initial))
        {
            Assert.Equal(1, restored.Position);
            restored.Process(2, activity, R("second"), _ => { });
        }
        File.Delete(Path.Combine(directory.Path, FileCaptureHost.CommitName(1)));
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "source", initial));
    }

    [Fact]
    public void LegacyJournalIsNeverSilentlyResetOrOverwritten()
    {
        using var directory = new CaptureDirectory();
        Directory.CreateDirectory(directory.Path);
        var legacy = Path.Combine(directory.Path, "capture.json");
        File.WriteAllText(legacy, "legacy snapshot");
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "source", TraceMonitor.Start(Claim(), Batch())));
        Assert.Equal("legacy snapshot", File.ReadAllText(legacy));
        Assert.Empty(Directory.GetFiles(directory.Path, "capture-*.json"));
    }

    [Fact]
    public void PublicationNeverOverwritesAnExistingCommitOrAcknowledgesItsCollision()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        var destination = Path.Combine(directory.Path, FileCaptureHost.CommitName(1));
        using var host = new FileCaptureHost(directory.Path, "source", initial, fault: point =>
        {
            if (point == FileCaptureHost.PersistencePoint.BeforePublish) File.WriteAllText(destination, "conflicting file");
        });
        Assert.Throws<IOException>(() => host.Process(1, activity, R("first"), _ => Assert.Fail("Must not ack.")));
        Assert.Equal("conflicting file", File.ReadAllText(destination));
        Assert.Throws<ObjectDisposedException>(() => host.Process(1, activity, R("first"), _ => Assert.Fail("Must not ack.")));
    }

    [Fact]
    public void PeriodicCheckpointRetainsReceiptsAndRestoresOnlyItsTail()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var paid = Capture(Batch(Event("paid", 1)));
        using var shipped = Capture(Batch(Event("shipped", 2, shipped: true)));
        using var another = Capture(Batch(Event("next-paid", 3)));
        string expected;
        using (var host = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2))
        {
            host.Process(1, paid, R("first"), _ => { });
            host.Process(2, shipped, R("second"), _ => { });
            Assert.True(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CheckpointFile)));
            Assert.False(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(1))));
            Assert.False(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(2))));
            Assert.True(host.PublishedBytes > host.CommittedBytes);
            host.Process(3, another, R("third"), _ => { });
            expected = host.Monitor.Serialize();
            Assert.Equal(Directory.GetFiles(directory.Path, "capture-*.json").Sum(x => new FileInfo(x).Length), host.CommittedBytes);
        }
        using var restored = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2);
        Assert.Equal(3, restored.Position);
        Assert.Equal(expected, restored.Monitor.Serialize());
        var replay = restored.Process(1, paid, R("first"), _ => { });
        Assert.Equal(R("producer-envelope"), Assert.Single(replay.EnvelopeReferences));
        Assert.Equal(0, restored.PublishedBytes);
        Assert.Equal(expected, restored.Monitor.Serialize());
    }

    [Theory]
    [InlineData((int)FileCaptureHost.PersistencePoint.BeforeCheckpointPublish)]
    [InlineData((int)FileCaptureHost.PersistencePoint.AfterCheckpointPublish)]
    [InlineData((int)FileCaptureHost.PersistencePoint.BeforeCompactionDelete)]
    [InlineData((int)FileCaptureHost.PersistencePoint.AfterCompactionDelete)]
    public void CheckpointAndDeletionFailuresRecoverCommittedDeliveryBeforeRetryingAck(int boundary)
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var paid = Capture(Batch(Event("paid", 1)));
        using var shipped = Capture(Batch(Event("shipped", 2, shipped: true)));
        var expected = AssuranceActivityAdapter.Apply(
            AssuranceActivityAdapter.Apply(initial, paid, R("first")).Monitor, shipped, R("second")).Monitor.Serialize();
        var acks = 0;
        using (var failed = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2,
            fault: point => { if ((int)point == boundary) throw new IOException("Checkpoint boundary failure."); }))
        {
            failed.Process(1, paid, R("first"), _ => acks++);
            Assert.Throws<IOException>(() => failed.Process(2, shipped, R("second"), _ => acks++));
            Assert.Equal(1, acks);
            Assert.Throws<ObjectDisposedException>(() => failed.Process(2, shipped, R("second"), _ => acks++));
        }
        using var resumed = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2);
        Assert.Equal(2, resumed.Position);
        Assert.Equal(expected, resumed.Monitor.Serialize());
        resumed.Process(2, shipped, R("second"), _ => acks++);
        Assert.Equal(2, acks);
        Assert.Equal(0, resumed.PublishedBytes);
    }

    [Fact]
    public void CompactedQuarantineAndEmptyDeliveriesKeepGapAndExactRedeliveryChecks()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var rejected = Capture(Batch() with { Target = R("wrong") });
        using var empty = Capture(Batch());
        using var changed = Capture(Batch(Event("paid", 1)));
        using (var host = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2))
        {
            host.Process(1, rejected, R("rejected"), _ => { });
            host.Process(2, empty, R("empty"), _ => { });
        }
        using var resumed = new FileCaptureHost(directory.Path, "source", initial);
        Assert.True(resumed.HasCaptureGaps);
        Assert.Empty(resumed.Monitor.Evaluation.Batch.Alternatives[0].Observations);
        Assert.Equal(R("empty"), resumed.Monitor.Evaluation.Batch.Alternatives[0].Reference);
        Assert.NotNull(resumed.Process(1, rejected, R("rejected"), _ => { }).Rejection);
        resumed.Process(2, empty, R("empty"), _ => { });
        Assert.Throws<InvalidOperationException>(() => resumed.Process(2, changed, R("empty"), _ => Assert.Fail("Must not ack.")));
        Assert.Equal(0, resumed.PublishedBytes);
    }

    [Fact]
    public void CorruptPublishedCheckpointFailsInsteadOfFallingBackToCoveredLogs()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        using (var host = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 1,
            fault: point => { if (point == FileCaptureHost.PersistencePoint.AfterCheckpointPublish) throw new IOException(); }))
            Assert.Throws<IOException>(() => host.Process(1, activity, R("first"), _ => Assert.Fail("Must not ack.")));
        Assert.True(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(1))));
        File.WriteAllText(Path.Combine(directory.Path, FileCaptureHost.CheckpointFile), "corrupt");
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "source", initial));
        Assert.True(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(1))));
    }

    [Theory]
    [InlineData((int)FileCaptureHost.PersistencePoint.BeforeCheckpointPublish)]
    [InlineData((int)FileCaptureHost.PersistencePoint.AfterCheckpointPublish)]
    public void ReplacingAnExistingCheckpointRecoversTheLatestCommittedTail(int boundary)
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var paid = Capture(Batch(Event("paid", 1)));
        using var shipped = Capture(Batch(Event("shipped", 2, shipped: true)));
        using var paidAgain = Capture(Batch(Event("paid-again", 3)));
        using var shippedAgain = Capture(Batch(Event("shipped-again", 4, shipped: true)));
        var inject = false;
        string expected;
        using (var host = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2,
            fault: point => { if (inject && (int)point == boundary) throw new IOException(); }))
        {
            host.Process(1, paid, R("one"), _ => { });
            host.Process(2, shipped, R("two"), _ => { });
            host.Process(3, paidAgain, R("three"), _ => { });
            expected = AssuranceActivityAdapter.Apply(host.Monitor, shippedAgain, R("four")).Monitor.Serialize();
            inject = true;
            Assert.Throws<IOException>(() => host.Process(4, shippedAgain, R("four"), _ => Assert.Fail("Must not ack.")));
        }
        using var restored = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2);
        Assert.Equal(4, restored.Position);
        Assert.Equal(expected, restored.Monitor.Serialize());
        restored.Process(1, paid, R("one"), _ => { }); // Old compacted receipt still supports exact redelivery.
        restored.Process(4, shippedAgain, R("four"), _ => { });
        Assert.Equal(0, restored.PublishedBytes);
    }

    [Fact]
    public void InsufficientCheckpointHeadroomKeepsTheDurableLogAndAllowsAcknowledgment()
    {
        using var sizing = new CaptureDirectory();
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        long commitSize;
        using (var baseline = new FileCaptureHost(sizing.Path, "source", initial))
        {
            baseline.Process(1, activity, R("one"), _ => { });
            commitSize = baseline.CommittedBytes;
        }
        var acknowledgments = 0;
        using (var host = new FileCaptureHost(directory.Path, "source", initial,
            maxJournalBytes: checked((int)commitSize), checkpointInterval: 1))
        {
            host.Process(1, activity, R("one"), _ => acknowledgments++);
            Assert.Equal(1, acknowledgments);
            Assert.Equal(commitSize, host.CommittedBytes);
            Assert.False(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CheckpointFile)));
            Assert.True(File.Exists(Path.Combine(directory.Path, FileCaptureHost.CommitName(1))));
        }
        using var restored = new FileCaptureHost(directory.Path, "source", initial, maxJournalBytes: checked((int)commitSize));
        Assert.Equal(1, restored.Position);
        Assert.Single(restored.Monitor.Evaluation.Batch.Alternatives[0].Observations);
    }

    [Fact]
    public void AllQuarantinedCheckpointCanRetainAnExactCorrectedInitialMonitor()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch()).Correct(new(R("corrected"), [], []), R("corrected-epoch"), R("correction"));
        using var activity = Capture(Batch()); // Old epoch; permanently rejected against the corrected binding.
        using (var host = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 1))
            Assert.NotNull(host.Process(1, activity, R("rejected"), _ => { }).Rejection);
        using var restored = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(initial.Serialize(), restored.Monitor.Serialize());
        Assert.True(restored.HasCaptureGaps);
    }

    [Fact]
    public void ModifiedReplayDeltaCannotStandInForItsRecordedCheckpoint()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        using (var host = new FileCaptureHost(directory.Path, "source", initial))
            host.Process(1, activity, R("one"), _ => { });
        var path = Path.Combine(directory.Path, FileCaptureHost.CommitName(1));
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var delta = TraceObservationBatch.Deserialize(document["delta"]!.GetValue<string>());
        var alternative = delta.Alternatives[0];
        var changed = delta with { Alternatives = [alternative with { Observations = [alternative.Observations[0] with { Occurrence = "changed" }] }] };
        document["delta"] = changed.Serialize();
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new FileCaptureHost(directory.Path, "source", initial));
    }

    [Fact]
    public void PublishedByteAccountingIncludesReplacedCheckpointsWithoutSubtractingDeletedLogs()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        long position = 0;
        List<long> publications = [];
        using var host = new FileCaptureHost(directory.Path, "source", initial, checkpointInterval: 2,
            fault: point =>
            {
                if (point == FileCaptureHost.PersistencePoint.AfterPublish)
                    publications.Add(new FileInfo(Path.Combine(directory.Path, FileCaptureHost.CommitName(position))).Length);
                if (point == FileCaptureHost.PersistencePoint.AfterCheckpointPublish)
                    publications.Add(new FileInfo(Path.Combine(directory.Path, FileCaptureHost.CheckpointFile)).Length);
            });
        for (position = 1; position <= 4; position++) host.Process(position, activity, R("delivery-" + position), _ => { });
        Assert.Equal(6, publications.Count);
        Assert.Equal(publications.Sum(), host.PublishedBytes);
        Assert.Equal(new FileInfo(Path.Combine(directory.Path, FileCaptureHost.CheckpointFile)).Length, host.CommittedBytes);
        Assert.True(host.PublishedBytes > host.CommittedBytes);
        Assert.Single(Directory.GetFiles(directory.Path, "capture-*.json"));
    }

    [Fact]
    public void CaptureHostRedeliversAfterAckFailureAndRejectsChangedDelivery()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        using var host = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Throws<IOException>(() => host.Process(1, activity, R("accepted"), _ => throw new IOException("Ack lost.")));
        var checkpoint = host.Monitor.Serialize();
        host.Process(1, activity, R("accepted"), _ => { });
        Assert.Equal(checkpoint, host.Monitor.Serialize());
        Assert.Throws<InvalidOperationException>(() => host.Process(1, activity, R("changed"), _ => Assert.Fail("Must not ack.")));
        using var changed = Capture(Batch(Event("different", 1)));
        Assert.Throws<InvalidOperationException>(() => host.Process(1, changed, R("accepted"), _ => Assert.Fail("Must not ack.")));
    }

    [Fact]
    public void PermanentRejectionIsQuarantinedBeforeAcknowledgmentAndDoesNotBlockNextDelivery()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var wrong = Capture(Batch() with { Target = R("wrong") });
        using var valid = Capture(Batch(Event("paid", 1)));
        using (var host = new FileCaptureHost(directory.Path, "source", initial))
        {
            var receipt = host.Process(1, wrong, R("rejected"), _ => Assert.True(host.HasCaptureGaps));
            Assert.Equal(TraceEvaluationCodes.TelemetryBinding, receipt.Rejection!.Code);
            Assert.Equal(initial.Serialize(), host.Monitor.Serialize());
            Assert.NotEmpty(receipt.RawActivity);
            host.Process(2, valid, R("accepted"), _ => { });
        }
        using var resumed = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(2, resumed.Position);
        Assert.True(resumed.HasCaptureGaps);
        Assert.Single(resumed.Monitor.Evaluation.Batch.Alternatives[0].Observations);
        Assert.Empty(resumed.Monitor.Evaluation.Batch.Alternatives[0].Completeness);
    }

    [Fact]
    public void QuarantinePersistenceFailureDoesNotAcknowledgeOrSkipTheDelivery()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var wrong = Capture(Batch() with { Target = R("wrong") });
        using (var failed = new FileCaptureHost(directory.Path, "source", initial,
            fault: point => { if (point == FileCaptureHost.PersistencePoint.BeforePublish) throw new IOException(); }))
            Assert.Throws<IOException>(() => failed.Process(1, wrong, R("rejected"), _ => Assert.Fail("Must not ack.")));
        using var resumed = new FileCaptureHost(directory.Path, "source", initial);
        Assert.Equal(0, resumed.Position);
        Assert.NotNull(resumed.Process(1, wrong, R("rejected"), _ => { }).Rejection);
    }

    [Fact]
    public void CaptureHostBoundsOrderingCancellationAndOwnershipFailWithoutAcknowledgment()
    {
        using var directory = new CaptureDirectory();
        var initial = TraceMonitor.Start(Claim(), Batch());
        using var activity = Capture(Batch(Event("paid", 1)));
        using var host = new FileCaptureHost(directory.Path, "source", initial, maxDeliveries: 1);
        Assert.Throws<IOException>(() => new FileCaptureHost(directory.Path, "source", initial));
        Assert.Throws<InvalidOperationException>(() => host.Process(2, activity, R("gap"), _ => Assert.Fail("Must not ack.")));
        Assert.Throws<OperationCanceledException>(() => host.Process(1, activity, R("cancelled"), _ => Assert.Fail("Must not ack."), new(canceled: true)));
        host.Process(1, activity, R("accepted"), _ => { });
        Assert.Throws<InvalidOperationException>(() => host.Process(2, activity, R("full"), _ => Assert.Fail("Must not ack.")));
        Assert.Equal(1, host.Position);
    }
}

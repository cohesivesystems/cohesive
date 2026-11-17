using Cohesive.Assurance;
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

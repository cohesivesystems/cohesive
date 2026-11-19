using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Authoring;
using Cohesive.Model;

namespace Cohesive.Assurance.Tests;

public sealed class TraceMonitorTests
{
    public sealed record Stock(decimal Reserved);
    public sealed record Order(string Id);
    static ClaimReference R(string id) => new(new("monitor-test://" + id), "1", id);
    static readonly ClaimReference Selection = R("selected"), Witness = R("witness"), Mapping = R("mapping");
    static Claim.Declaration Declaration() => Claim.Define("orders", "1", R("subject"), Mapping, R("source"), R("config"));
    static ClaimDocument Invariant()
    {
        var input = new ExpressionAuthoring().Input<Stock>("stock");
        return Declaration().AtEveryCommittedState(Selection, InitialStateSelection.Include)
            .Require(input, ClaimInputRole.State, s => s.Reserved <= 10m).Build();
    }
    static ClaimDocument Events(bool historical = false)
    {
        var author = new ExpressionAuthoring();
        return (historical ? Declaration().RequireEarlier(Selection, Witness)
                : Declaration().RequireWithin(Selection, Witness, TimeSpan.FromTicks(10)))
            .Require(author.Input<Order>("trigger"), ClaimInputRole.Trigger,
                author.Input<Order>("witness"), ClaimInputRole.Witness, (t, w) => t.Id == w.Id).Build();
    }
    static SemanticObservation State(string id, long step, decimal reserved) => new(id, "p1", step, step, Selection,
        SemanticObservationKind.CommittedState, ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
            .Add(ClaimInputRole.State, ObservationValue.FromObject(new Stock(reserved))));
    static SemanticObservation Event(string id, long step, long ticks, bool witness = false, string partition = "p1") =>
        new(id, partition, step, ticks, witness ? Witness : Selection, SemanticObservationKind.Event,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty, ObservationValue.FromObject(new Order("o1")));
    static ObservationCompleteness Frontier(ClaimReference selector, long step, long ticks,
        ObservationClosure closure = ObservationClosure.Open, string partition = "p1") =>
        new(selector, partition, step, ticks, closure, R("frontier-" + partition + "-" + step));
    static TraceObservationBatch Batch(bool events = false, bool partitions = false) =>
        new(new(Mapping, R("atomic-scope"), events
            ? [new(Selection, ["p1"]), new(Witness, partitions ? ["p1", "p2"] : ["p1"])]
            : [new(Selection, ["p1"])]), R("target"), R("epoch"),
            [new(R("initial"), [], [])], EnumerationCompleteness.Complete, R("enumeration"));
    static TraceObservationAlternative Update(string id, SemanticObservation[]? observations = null,
        ObservationCompleteness[]? frontiers = null) => new(R(id), observations is null ? [] : [.. observations],
        frontiers is null ? [] : [.. frontiers]);
    static void MatchesBatch(TraceMonitor monitor, TraceObservationBatch expected)
    {
        var batch = TraceClaimEvaluator.Evaluate(monitor.Evaluation.Claim, expected, monitor.Evaluation.Limits);
        Assert.Equal(batch.Serialize(), monitor.Evaluation.Serialize());
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(10, 2)]
    [InlineData(3, 3)]
    public void BoundedAppendChargesRetainedMatchesInBatchOrder(int predicates, int visits)
    {
        var first = Event("a", 1, 1) with { EventPayload = ObservationValue.FromObject(new Order("other")) };
        var second = Event("b", 1, 1);
        var priorWitness = Event("release", 1, 1, witness: true);
        var claim = Events();
        var limits = new TraceEvaluationLimits(MaxPredicateEvaluations: predicates, MaxEventCandidateVisits: visits);
        var initial = Batch(events: true) with { Alternatives = [Update("prior", [first, second, priorWitness])] };
        var monitor = TraceMonitor.Restore(TraceMonitor.Start(claim, initial, limits).Serialize());
        var newWitness = Event("release2", 2, 11, witness: true) with { EventPayload = first.EventPayload };
        var batch = initial with { Alternatives = [Update("next", [first, second, priorWitness, newWitness])] };
        var expected = TraceClaimEvaluator.Evaluate(claim, batch, limits);
        var update = Update("next", [newWitness]);
        if (expected.Status == AssuranceAttemptStatus.BudgetExceeded)
            Assert.Equal(expected.Diagnostics[0], Assert.Throws<TraceObservationException>(() => monitor.Advance(update)).Diagnostic);
        else
            Assert.Equal(expected.Serialize(), monitor.Advance(update).Evaluation.Serialize());
    }

    [Fact]
    public void BoundedAppendMatchesBatchAtEachPrefixIncludingMissingPayloadAndFirstWitness()
    {
        var initial = Batch(events: true);
        var monitor = TraceMonitor.Start(Events(), initial);
        SemanticObservation[] rows = [Event("a", 1, 1), Event("b", 1, 1),
            Event("unknown", 2, 4, witness: true) with { EventPayload = null },
            Event("first", 3, 11, witness: true), Event("second", 3, 11, witness: true)];
        for (var length = 1; length <= rows.Length; length++)
        {
            var reference = "prefix-" + length;
            monitor = monitor.Advance(Update(reference, [rows[length - 1]]));
            MatchesBatch(monitor, initial with { Alternatives = [Update(reference, rows[..length])] });
            monitor = TraceMonitor.Restore(monitor.Serialize());
        }
        Assert.All(monitor.Evaluation.Alternatives[0].Obligations, o => Assert.Equal("first", o.WitnessOccurrence));
    }

    [Fact]
    public void RestoredStatePrefixRetainsDiagnosticsAndChargesOriginalPredicateBudget()
    {
        var missing = State("missing", 1, 0) with { Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty };
        var first = TraceMonitor.Start(Invariant(), Batch(), new(MaxPredicateEvaluations: 2))
            .Advance(Update("first", [missing]));
        var second = TraceMonitor.Restore(first.Serialize()).Advance(Update("second", [State("bad", 2, 11)]));
        MatchesBatch(second, second.Evaluation.Batch);
        Assert.Equal(ClaimVerdict.Violated, second.Evaluation.Verdict);
        Assert.Throws<TraceObservationException>(() => second.Advance(Update("third", [State("extra", 3, 1)])));
    }

    [Fact]
    public void CapturedBatchesShareBindingProfileBudgetAndAtomicityWithoutTransport()
    {
        var initial = Batch();
        var monitor = TraceMonitor.Start(Invariant(), initial, new(MaxObservations: 2));
        var first = initial with { Alternatives = [Update("producer", [State("first", 1, 4)])] };
        var accepted = monitor.AdvanceCaptured([first], R("accepted"));
        Assert.Equal(R("producer"), Assert.Single(accepted.EnvelopeReferences));
        Assert.Single(accepted.Monitor.Evaluation.Batch.Alternatives[0].Observations);
        Assert.Equal(TraceEvaluationCodes.TelemetryBinding, Assert.Throws<TraceObservationException>(() =>
            monitor.AdvanceCaptured([first, initial with { Target = R("different") }], R("bad"))).Diagnostic.Code);
        var certified = initial with { Alternatives = [Update("certified", frontiers: [Frontier(Selection, 1, 1)])] };
        Assert.Equal(TraceEvaluationCodes.TelemetryEnvelope, Assert.Throws<TraceObservationException>(() =>
            monitor.AdvanceCaptured([first, certified], R("bad"))).Diagnostic.Code);
        Assert.Equal(TraceEvaluationCodes.BudgetExceeded, Assert.Throws<TraceObservationException>(() =>
            monitor.AdvanceCaptured([first, first, first], R("too-many"))).Diagnostic.Code);
        Assert.Equal(TraceEvaluationCodes.BudgetExceeded, Assert.Throws<TraceObservationException>(() =>
            monitor.AdvanceCaptured([initial, initial, initial], R("too-many-empty"))).Diagnostic.Code);
        // Empty envelopes use their own per-call counter, not the retained row budget.
        var emptyThenRows = monitor.AdvanceCaptured([initial, first], R("empty-then-rows"));
        Assert.Single(emptyThenRows.Monitor.Evaluation.Batch.Alternatives[0].Observations);
        var next = initial with { Alternatives = [Update("next-producer", [State("second", 2, 5)])] };
        Assert.Equal(2, emptyThenRows.Monitor.AdvanceCaptured([initial, next], R("second-capture"))
            .Monitor.Evaluation.Batch.Alternatives[0].Observations.Length);
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Observations);
    }

    [Fact]
    public void EmptyCaptureHonorsReferenceAndLazyDecoderFailureDoesNotAdvance()
    {
        var initial = Batch();
        var monitor = TraceMonitor.Start(Invariant(), initial);
        var empty = monitor.AdvanceCaptured([], R("empty"));
        Assert.Equal(R("empty"), empty.Monitor.Evaluation.Batch.Alternatives[0].Reference);
        Assert.Equal(R("empty"), TraceMonitor.Restore(empty.Monitor.Serialize()).Evaluation.Batch.Alternatives[0].Reference);
        Assert.Same(empty.Monitor, empty.Monitor.AdvanceCaptured([], R("empty")).Monitor);
        Assert.Throws<FormatException>(() => monitor.AdvanceCaptured(BrokenDecoder(), R("failed")));
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Observations);
        IEnumerable<TraceObservationBatch> BrokenDecoder()
        {
            yield return initial with { Alternatives = [Update("producer", [State("first", 1, 4)])] };
            throw new FormatException("Decoder failure after a valid envelope.");
        }
    }

    [Fact]
    public void InvariantPrefixesAndRestoredContinuationMatchIndependentBatches()
    {
        var initial = Batch();
        var monitor = TraceMonitor.Start(Invariant(), initial);
        var first = Update("first", [State("reserved", 1, 7)]);
        monitor = monitor.Advance(first);
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict);
        MatchesBatch(monitor, initial with { Alternatives = [first] });
        var checkpoint = monitor.Serialize();
        var restored = TraceMonitor.Restore(checkpoint);
        Assert.Equal(monitor.Reference(), restored.Reference());
        var second = Update("second", [State("oversell", 2, 12)]);
        var continuous = monitor.Advance(second);
        var resumed = restored.Advance(second);
        Assert.Equal(continuous.Serialize(), resumed.Serialize());
        Assert.Equal(ClaimVerdict.Violated, resumed.Evaluation.Verdict);
        MatchesBatch(resumed, initial with { Alternatives = [second with { Observations = [.. first.Observations, .. second.Observations] }] });
        Assert.Equal(checkpoint, monitor.Serialize());
    }

    [Fact]
    public void CommitInputsPreservePreStateAndPostStateRoles()
    {
        var author = new ExpressionAuthoring();
        var before = author.Input<Stock>("before"); var after = author.Input<Stock>("after");
        var claim = Declaration().ForEachCommitted(Selection)
            .Require(before, ClaimInputRole.Before, after, ClaimInputRole.After, (b, a) => a.Reserved >= b.Reserved).Build();
        var commit = new SemanticObservation("commit", "p1", 1, 1, Selection, SemanticObservationKind.Commit,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
                .Add(ClaimInputRole.Before, ObservationValue.FromObject(new Stock(5)))
                .Add(ClaimInputRole.After, ObservationValue.FromObject(new Stock(3))));
        var update = Update("committed", [commit]);
        var initial = Batch();
        var result = TraceMonitor.Start(claim, initial).Advance(update);
        MatchesBatch(result, initial with { Alternatives = [update] });
        Assert.Equal(ClaimVerdict.Violated, result.Evaluation.Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalAndResponseWitnessesAgreeWithBatch(bool history)
    {
        var initial = Batch(events: true);
        var earlier = Event("first", 1, 1, witness: history);
        var later = Event("second", 2, 2, witness: !history);
        var monitor = TraceMonitor.Start(Events(history), initial).Advance(Update("first", [earlier]));
        monitor = TraceMonitor.Restore(monitor.Serialize()).Advance(Update("second", [later]));
        var closure = new[] { Frontier(Selection, 2, 2, ObservationClosure.Closed), Frontier(Witness, 2, 2, ObservationClosure.Closed) };
        monitor = monitor.Advance(Update("closed", frontiers: closure));
        MatchesBatch(monitor, initial with { Alternatives = [Update("closed", [earlier, later], closure)] });
        Assert.Equal(ClaimVerdict.Satisfied, monitor.Evaluation.Verdict);
    }

    [Fact]
    public void DeadlineAbsenceNeedsEveryWitnessPartitionAndInclusiveWatermark()
    {
        var initial = Batch(events: true, partitions: true);
        var monitor = TraceMonitor.Start(Events(), initial).Advance(Update("trigger", [Event("order", 1, 1)]));
        monitor = monitor.Advance(Update("p1", frontiers: [Frontier(Witness, 11, 11)]));
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict);
        monitor = monitor.Advance(Update("p2-before", frontiers: [Frontier(Witness, 10, 10, partition: "p2")]));
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict);
        var restored = TraceMonitor.Restore(monitor.Serialize());
        monitor = restored.Advance(Update("p2-deadline", frontiers: [Frontier(Witness, 11, 11, partition: "p2")]));
        Assert.Equal(ClaimVerdict.Violated, monitor.Evaluation.Verdict);
        MatchesBatch(monitor, initial with { Alternatives = [Update("p2-deadline", [Event("order", 1, 1)],
            [Frontier(Witness, 11, 11), Frontier(Witness, 11, 11, partition: "p2")])] });
    }

    [Fact]
    public void InclusiveDeadlineWitnessMayArriveWithItsCompletenessCertificate()
    {
        var monitor = TraceMonitor.Start(Events(), Batch(events: true))
            .Advance(Update("trigger", [Event("order", 1, 1)]));
        monitor = monitor.Advance(Update("deadline", [Event("paid", 2, 11, witness: true)],
            [Frontier(Selection, 2, 11, ObservationClosure.Closed), Frontier(Witness, 2, 11, ObservationClosure.Closed)]));
        Assert.Equal(ClaimVerdict.Satisfied, monitor.Evaluation.Verdict);
    }

    [Fact]
    public void RedeliveryAfterClosureDoesNotConsumeCapacityOrBecomeLateData()
    {
        var state = State("stock", 1, 7);
        var update = Update("closed", [state], [Frontier(Selection, 1, 1, ObservationClosure.Closed)]);
        var monitor = TraceMonitor.Start(Invariant(), Batch(), new(MaxObservations: 2)).Advance(update);
        Assert.Same(monitor, monitor.Advance(update));
        var duplicate = State("stock", 1, 7); // New dictionary instance, same content.
        var result = monitor.Advance(Update("redelivered", [duplicate]));
        Assert.Single(result.Evaluation.Batch.Alternatives[0].Observations);
        Assert.Equal(ClaimVerdict.Satisfied, result.Evaluation.Verdict);
        var conflict = Assert.Throws<TraceObservationException>(() => monitor.Advance(Update("conflict", [State("stock", 1, 12)])));
        Assert.Equal(TraceEvaluationCodes.InvalidObservation, conflict.Diagnostic.Code);
    }

    [Fact]
    public void LateEvidenceRequiresExplicitCorrectionWithImmutableLineage()
    {
        var monitor = TraceMonitor.Start(Events(), Batch(events: true))
            .Advance(Update("trigger", [Event("order", 1, 1)]))
            .Advance(Update("absent", frontiers: [Frontier(Witness, 11, 11)]));
        var old = monitor.Serialize();
        var late = Event("paid", 2, 5, witness: true);
        var error = Assert.Throws<TraceObservationException>(() => monitor.Advance(Update("late", [late])));
        Assert.Equal(TraceEvaluationCodes.MonitorLateObservation, error.Diagnostic.Code);
        Assert.Equal(old, monitor.Serialize());
        var corrected = monitor.Correct(Update("replacement", [Event("order", 1, 1), late],
            [Frontier(Selection, 11, 11, ObservationClosure.Closed), Frontier(Witness, 11, 11, ObservationClosure.Closed)]),
            newEpoch: R("corrected-epoch"), evidence: R("correction-evidence"));
        Assert.Equal(ClaimVerdict.Violated, monitor.Evaluation.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, corrected.Evaluation.Verdict);
        Assert.Equal(monitor.Reference(), corrected.Predecessor);
        Assert.Equal(R("correction-evidence"), corrected.CorrectionEvidence);
        Assert.Equal(corrected.Serialize(), TraceMonitor.Restore(corrected.Serialize()).Serialize());
    }

    [Fact]
    public void RegressionsCapacityAndInvalidOrderRejectWholeUpdate()
    {
        var monitor = TraceMonitor.Start(Invariant(), Batch(), new(MaxObservations: 3))
            .Advance(Update("first", [State("one", 1, 1)], [Frontier(Selection, 1, 1)]));
        var old = monitor.Serialize();
        Assert.Equal(TraceEvaluationCodes.MonitorFrontierRegression, Assert.Throws<TraceObservationException>(() =>
            monitor.Advance(Update("regress", frontiers: [Frontier(Selection, 0, 0)]))).Diagnostic.Code);
        Assert.Equal(TraceEvaluationCodes.BudgetExceeded, Assert.Throws<TraceObservationException>(() =>
            monitor.Advance(Update("overflow", [State("two", 2, 2), State("three", 3, 3)]))).Diagnostic.Code);
        Assert.Equal(old, monitor.Serialize());
        var accepted = monitor.Advance(Update("retry", [State("two", 2, 2)]));
        Assert.Equal(2, accepted.Evaluation.Batch.Alternatives[0].Observations.Length);
        var unordered = TraceMonitor.Start(Invariant(), Batch()).Advance(Update("later", [State("later", 3, 3)]));
        Assert.Throws<TraceObservationException>(() => unordered.Advance(Update("earlier", [State("earlier", 2, 2)])));
    }

    [Fact]
    public void ClosedBoundaryCannotReopenOrAcceptNewObservations()
    {
        var monitor = TraceMonitor.Start(Invariant(), Batch())
            .Advance(Update("closed", frontiers: [Frontier(Selection, 1, 1, ObservationClosure.Closed)]));
        Assert.Equal(TraceEvaluationCodes.MonitorFrontierRegression, Assert.Throws<TraceObservationException>(() =>
            monitor.Advance(Update("reopen", frontiers: [Frontier(Selection, 2, 2)]))).Diagnostic.Code);
        Assert.Equal(TraceEvaluationCodes.MonitorLateObservation, Assert.Throws<TraceObservationException>(() =>
            monitor.Advance(Update("new", [State("new", 2, 2)]))).Diagnostic.Code);
    }

    [Fact]
    public void BatchAndMonitorShareMalformedRecordDiagnostics()
    {
        var monitor = TraceMonitor.Start(Invariant(), Batch());
        var valid = State("one", 1, 1);
        SemanticObservation[] malformed = [null!, valid with { Partition = " " }, valid with { Occurrence = "" },
            valid with { Values = null! }, valid with { Selector = null! }, valid with { Step = -1 },
            valid with { Ticks = -1 }, valid with { Kind = (SemanticObservationKind)99 },
            valid with { EventPayload = ObservationValue.FromBool(true) },
            valid with { Kind = SemanticObservationKind.Event }];
        foreach (var observation in malformed)
        {
            var update = Update("bad", [observation]);
            var batch = TraceClaimEvaluator.Evaluate(Invariant(), Batch() with { Alternatives = [update] });
            var error = Assert.Throws<TraceObservationException>(() => monitor.Advance(update));
            Assert.Equal(batch.Diagnostics[0], error.Diagnostic);
        }
        var frontier = Frontier(Selection, 1, 1);
        ObservationCompleteness[][] invalidFrontiers = [[null!], [frontier with { Partition = " " }],
            [frontier with { Evidence = null! }], [frontier with { ThroughStep = -1 }],
            [frontier with { Closure = (ObservationClosure)99 }], [frontier, frontier]];
        foreach (var frontiers in invalidFrontiers)
        {
            var update = Update("bad", frontiers: frontiers);
            var batch = TraceClaimEvaluator.Evaluate(Invariant(), Batch() with { Alternatives = [update] });
            var error = Assert.Throws<TraceObservationException>(() => monitor.Advance(update));
            Assert.Equal(batch.Diagnostics[0], error.Diagnostic);
        }
    }

    [Fact]
    public void RestorePreservesStableCodesForWireProfileAndCorrectionFailures()
    {
        var monitor = TraceMonitor.Start(Invariant(), Batch());
        Assert.Equal(TraceEvaluationCodes.MonitorCheckpoint,
            Assert.Throws<TraceObservationException>(() => TraceMonitor.Restore("not JSON")).Diagnostic.Code);
        var node = JsonNode.Parse(monitor.Serialize())!.AsObject();
        var partial = TraceClaimEvaluator.Evaluate(Invariant(), Batch() with { Enumeration = EnumerationCompleteness.Partial });
        node["evaluation"] = JsonNode.Parse(partial.Serialize());
        Assert.Equal(TraceEvaluationCodes.MonitorProfile,
            Assert.Throws<TraceObservationException>(() => TraceMonitor.Restore(node.ToJsonString())).Diagnostic.Code);
        var corrected = monitor.Correct(Update("replacement"), R("epoch-2"), R("evidence"));
        var correction = JsonNode.Parse(corrected.Serialize())!.AsObject();
        correction["predecessor"] = null;
        Assert.Equal(TraceEvaluationCodes.MonitorCorrection,
            Assert.Throws<TraceObservationException>(() => TraceMonitor.Restore(correction.ToJsonString())).Diagnostic.Code);
    }

    [Fact]
    public async Task ConcurrentReferenceReadsShareOneImmutableFingerprint()
    {
        var monitor = TraceMonitor.Start(Invariant(), Batch()).Advance(Update("one", [State("one", 1, 1)]));
        var references = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(monitor.Reference)));
        Assert.All(references, reference => Assert.Same(references[0], reference));
        Assert.Equal(references[0], TraceMonitor.Restore(monitor.Serialize()).Reference());
    }

    [Fact]
    public void ReferenceReuseCancellationAndUnsupportedMappingsDoNotCreateAcceptedState()
    {
        var monitor = TraceMonitor.Start(Invariant(), Batch());
        Assert.Throws<TraceObservationException>(() => monitor.Advance(Update("initial", [State("new", 1, 1)])));
        Assert.Throws<OperationCanceledException>(() => monitor.Advance(Update("cancel", [State("new", 1, 1)]), new(canceled: true)));
        Assert.Throws<TraceObservationException>(() => TraceMonitor.Start(Invariant(), Batch() with { Enumeration = EnumerationCompleteness.Partial }));
        Assert.Throws<TraceObservationException>(() => TraceMonitor.Start(Invariant(), Batch() with { Alternatives = [Update("a"), Update("b")] }));
        Assert.Throws<TraceObservationException>(() => monitor.Correct(Update("new"), R("epoch"), R("evidence")));
        var invalid = JsonNode.Parse(monitor.Serialize())!.AsObject();
        invalid["evaluation"]!["verdict"] = "Satisfied";
        Assert.Throws<TraceObservationException>(() => TraceMonitor.Restore(invalid.ToJsonString()));
        Assert.Throws<TraceObservationException>(() => TraceMonitor.Restore(monitor.Serialize().Replace("reference-monitor/v1", "reference-monitor/v9")));
    }
}

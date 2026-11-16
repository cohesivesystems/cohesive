using System.Collections.Immutable;
using System.Diagnostics;
using Cohesive.Adapters.OpenTelemetry.Assurance;
using Cohesive.Assurance;
using Cohesive.Authoring;
using Cohesive.Model;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Cohesive.Adapters.OpenTelemetry.Tests;

[Collection("OpenTelemetry listeners")]
public sealed class AssuranceActivityTests
{
    public sealed record Order(string Id);
    public sealed record Stock(decimal Reserved);
    static ClaimReference R(string value) => new(new("otel-assurance-test://" + value), "1", value);
    static readonly ClaimReference Trigger = R("paid"), Witness = R("shipped"), Mapping = R("mapping");
    static ClaimDocument Claim()
    {
        var author = new ExpressionAuthoring();
        return Cohesive.Assurance.Claim.Define("paid-ships", "1", R("orders"), Mapping, R("source"), R("config"))
            .RequireWithin(Trigger, Witness, TimeSpan.FromTicks(10))
            .Require(author.Input<Order>("paid"), ClaimInputRole.Trigger, author.Input<Order>("shipped"), ClaimInputRole.Witness,
                (paid, shipped) => paid.Id == shipped.Id).Build();
    }
    static TraceObservationBatch Batch(params SemanticObservation[] observations) => new(
        new(Mapping, R("atomic-clock-scope"), [new(Trigger, ["p"]), new(Witness, ["p"])]), R("deployment"), R("epoch"),
        [new(R("producer-envelope"), [.. observations], [])], EnumerationCompleteness.Complete, R("mapping-evidence"));
    static SemanticObservation Event(string id, long step, bool shipped = false) => new(id, "p", step, step,
        shipped ? Witness : Trigger, SemanticObservationKind.Event, ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty,
        ObservationValue.FromObject(new Order("o1")));
    static Activity Capture(TraceObservationBatch batch, Action<Activity>? edit = null)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AssuranceActivityAdapter.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var activity = AssuranceActivityAdapter.Source.StartActivity("capture")!;
        activity.AddEvent(AssuranceActivityAdapter.CreateEvent(batch));
        edit?.Invoke(activity);
        activity.Stop();
        return activity;
    }

    [Fact]
    public void NativeSdkExportFeedsMonitorAndRetainedEvidenceWithoutClosingScope()
    {
        using var exporter = new RecordingExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().SetSampler(new AlwaysOnSampler())
            .AddCohesiveAssuranceInstrumentation().AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using (var activity = AssuranceActivityAdapter.Source.StartActivity("payment"))
            activity!.AddEvent(AssuranceActivityAdapter.CreateEvent(Batch(Event("paid", 1))));
        using (var activity = AssuranceActivityAdapter.Source.StartActivity("shipment"))
            activity!.AddEvent(AssuranceActivityAdapter.CreateEvent(Batch(Event("shipped", 2, shipped: true))));
        Assert.True(provider.ForceFlush(10_000));
        Assert.Equal(2, exporter.Activities.Count);
        var monitor = TraceMonitor.Start(Claim(), Batch());
        monitor = AssuranceActivityAdapter.Apply(monitor, exporter.Activities[0], R("captured-payment")).Monitor;
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict);
        monitor = TraceMonitor.Restore(monitor.Serialize());
        monitor = AssuranceActivityAdapter.Apply(monitor, exporter.Activities[1], R("captured-shipment")).Monitor;
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict); // Open production scope.
        Assert.Equal(ClaimVerdict.Satisfied, Assert.Single(monitor.Evaluation.Alternatives[0].Obligations).Verdict);
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Completeness);
        Assert.Equal(monitor.Serialize(), TraceMonitor.Restore(monitor.Serialize()).Serialize());
        Assert.Same(monitor, AssuranceActivityAdapter.Apply(monitor, exporter.Activities[1], R("captured-shipment")).Monitor);
    }

    [Theory]
    [InlineData(true, ExportResult.Success, 1)]
    [InlineData(false, ExportResult.Success, 0)]
    [InlineData(true, ExportResult.Failure, 0)]
    public void SamplingAndExportLossNeverProduceCompleteness(bool sampled, ExportResult export, int received)
    {
        using var exporter = new RecordingExporter(export);
        using var provider = Sdk.CreateTracerProviderBuilder().SetSampler(sampled ? new AlwaysOnSampler() : new AlwaysOffSampler())
            .AddCohesiveAssuranceInstrumentation().AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using (var activity = AssuranceActivityAdapter.Source.StartActivity("payment"))
            activity?.AddEvent(AssuranceActivityAdapter.CreateEvent(Batch(Event("paid", 1))));
        Assert.True(provider.ForceFlush(10_000));
        Assert.Equal(received, exporter.Activities.Count);
        var monitor = TraceMonitor.Start(Claim(), Batch());
        foreach (var activity in exporter.Activities) monitor = AssuranceActivityAdapter.Apply(monitor, activity, R("received")).Monitor;
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict);
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Completeness);
    }

    [Fact]
    public void CompletenessCertificatesAreRejectedAtEncodingAndIngestion()
    {
        var batch = Batch();
        var forged = batch with { Alternatives = [batch.Alternatives[0] with { Completeness = [
            new(Witness, "p", 100, 100, ObservationClosure.Closed, R("claimed-completeness"))] }] };
        Assert.Equal(TraceEvaluationCodes.TelemetryEnvelope,
            Assert.Throws<TraceObservationException>(() => AssuranceActivityAdapter.CreateEvent(forged)).Diagnostic.Code);
        using var activity = Capture(batch, activity => activity.AddEvent(new(AssuranceActivityAdapter.EventName,
            tags: new ActivityTagsCollection { { AssuranceActivityAdapter.PayloadTag, forged.Serialize() } })));
        var monitor = TraceMonitor.Start(Claim(), Batch());
        Assert.Equal(TraceEvaluationCodes.TelemetryEnvelope,
            Assert.Throws<TraceObservationException>(() => AssuranceActivityAdapter.Apply(monitor, activity, R("forged")).Monitor).Diagnostic.Code);
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Observations);
    }

    [Fact]
    public void ExactBindingAndFullContractMismatchRejectWholeActivity()
    {
        var initial = Batch();
        TraceObservationBatch[] mismatches = [initial with { Target = R("other-target") },
            initial with { Epoch = R("other-epoch") }, initial with { EnumerationEvidence = R("other-mapping") },
            initial with { Contract = initial.Contract with { Scope = R("different-order") } },
            initial with { Contract = initial.Contract with { Requirements = [new(Trigger, ["other-partition"]), new(Witness, ["p"])] } }];
        var monitor = TraceMonitor.Start(Claim(), initial);
        foreach (var mismatch in mismatches)
        {
            using var activity = Capture(Batch(Event("paid", 1)), activity => activity.AddEvent(AssuranceActivityAdapter.CreateEvent(mismatch)));
            var error = Assert.Throws<TraceMonitorUpdateException>(() => AssuranceActivityAdapter.Apply(monitor, activity, R("rejected")).Monitor);
            Assert.Equal(TraceEvaluationCodes.TelemetryBinding, error.Diagnostic.Code);
            Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Observations);
        }
    }

    [Fact]
    public void ArrivalTimeAndSpanStatusNeverBecomeSemanticOrderOrCommitOutcome()
    {
        var input = new ExpressionAuthoring().Input<Stock>("before");
        var claim = Cohesive.Assurance.Claim.Define("reserve", "1", R("stock"), Mapping, R("source"), R("config"))
            .ForEachCommitted(Trigger).Require(input, ClaimInputRole.Before, s => s.Reserved <= 10m).Build();
        var attempt = new SemanticObservation("attempt-1", "p", 1, 1, Trigger, SemanticObservationKind.UnknownOutcome,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.Before, ObservationValue.FromObject(new Stock(12m))));
        var monitor = TraceMonitor.Start(claim, Batch());
        using var activity = Capture(Batch(attempt), span => span.SetStatus(ActivityStatusCode.Ok));
        monitor = AssuranceActivityAdapter.Apply(monitor, activity, R("unknown")).Monitor;
        Assert.Equal(ClaimVerdict.Inconclusive, monitor.Evaluation.Verdict);
        Assert.Equal(SemanticObservationKind.UnknownOutcome, monitor.Evaluation.Batch.Alternatives[0].Observations[0].Kind);
        using var earlier = Capture(Batch(attempt with { Occurrence = "earlier", Step = 0, Ticks = 0 }));
        Assert.Throws<TraceMonitorUpdateException>(() => AssuranceActivityAdapter.Apply(monitor, earlier, R("bad-order")).Monitor);
    }

    [Fact]
    public void ObservedCommittedViolationSurvivesCaptureAndCheckpointRestore()
    {
        var input = new ExpressionAuthoring().Input<Stock>("before");
        var claim = Cohesive.Assurance.Claim.Define("reserve", "1", R("stock"), Mapping, R("source"), R("config"))
            .ForEachCommitted(Trigger).Require(input, ClaimInputRole.Before, s => s.Reserved <= 10m).Build();
        var commit = new SemanticObservation("commit-1", "p", 1, 1, Trigger, SemanticObservationKind.Commit,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.Before, ObservationValue.FromObject(new Stock(12m))));
        using var activity = Capture(Batch(commit));
        var monitor = AssuranceActivityAdapter.Apply(TraceMonitor.Start(claim, Batch()), activity, R("violation")).Monitor;
        Assert.Equal(ClaimVerdict.Violated, monitor.Evaluation.Verdict);
        var restored = TraceMonitor.Restore(monitor.Serialize());
        Assert.Equal(ClaimVerdict.Violated, restored.Evaluation.Verdict);
        Assert.Equal("commit-1", Assert.Single(restored.Evaluation.Batch.Alternatives[0].Observations).Occurrence);
    }

    [Fact]
    public void ObservationWireRoundTripsAndRejectsUnsupportedVersions()
    {
        var batch = Batch(Event("paid", 1));
        var json = batch.Serialize();
        Assert.Equal(json, TraceObservationBatch.Deserialize(json).Serialize());
        Assert.Throws<System.Text.Json.JsonException>(() => TraceObservationBatch.Deserialize(
            json.Replace("cohesive-assurance-observations/v1", "cohesive-assurance-observations/v99")));
    }

    [Fact]
    public void MalformedAndOversizedPayloadsRejectWithoutAcceptingAnyRows()
    {
        var monitor = TraceMonitor.Start(Claim(), Batch());
        using var malformed = Capture(Batch(Event("paid", 1)), span => span.AddEvent(new(AssuranceActivityAdapter.EventName,
            tags: new ActivityTagsCollection { { AssuranceActivityAdapter.PayloadTag, "not JSON" } })));
        Assert.Equal(TraceEvaluationCodes.TelemetryEnvelope,
            Assert.Throws<AssuranceTelemetryException>(() => AssuranceActivityAdapter.Apply(monitor, malformed, R("bad")).Monitor).Diagnostic.Code);
        using var valid = Capture(Batch(Event("paid", 1)));
        Assert.Equal(TraceEvaluationCodes.TelemetryPayloadLimit,
            Assert.Throws<AssuranceTelemetryException>(() => AssuranceActivityAdapter.Apply(monitor, valid, R("big"), maxPayloadCharacters: 10).Monitor).Diagnostic.Code);
        Assert.Throws<OperationCanceledException>(() => AssuranceActivityAdapter.Apply(monitor, valid, R("cancelled"), cancellationToken: new(canceled: true)).Monitor);
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Observations);
    }

    [Fact]
    public void CaptureReturnsEveryProducerReferenceIncludingEmptyEnvelopesAndRedelivery()
    {
        var initial = Batch();
        var first = Batch(Event("paid", 1));
        var empty = initial with { Alternatives = [initial.Alternatives[0] with { Reference = R("empty-envelope") }] };
        using var activity = Capture(first, span => span.AddEvent(AssuranceActivityAdapter.CreateEvent(empty)));
        var result = AssuranceActivityAdapter.Apply(TraceMonitor.Start(Claim(), initial), activity, R("accepted"));
        Assert.Equal(new[] { first.Alternatives[0].Reference, empty.Alternatives[0].Reference }, result.EnvelopeReferences);
        var replay = AssuranceActivityAdapter.Apply(result.Monitor, activity, R("accepted"));
        Assert.Same(result.Monitor, replay.Monitor);
        Assert.Equal(result.EnvelopeReferences.ToArray(), replay.EnvelopeReferences.ToArray());
        using var emptyActivity = Capture(empty);
        var emptyResult = AssuranceActivityAdapter.Apply(result.Monitor, emptyActivity, R("unused"));
        Assert.Equal(R("unused"), emptyResult.Monitor.Evaluation.Batch.Alternatives[0].Reference);
        Assert.Equal(R("unused"), TraceMonitor.Restore(emptyResult.Monitor.Serialize()).Evaluation.Batch.Alternatives[0].Reference);
        Assert.Equal(empty.Alternatives[0].Reference, Assert.Single(emptyResult.EnvelopeReferences));
    }

    [Fact]
    public void IndividuallyValidEventsCanExceedTheActivityBoundWithoutPartialAcceptance()
    {
        var batch = Batch(Event("paid", 1));
        var limit = batch.Serialize().Length;
        using var activity = Capture(batch, span => span.AddEvent(AssuranceActivityAdapter.CreateEvent(batch, limit)));
        var monitor = TraceMonitor.Start(Claim(), Batch());
        var error = Assert.Throws<AssuranceTelemetryException>(() =>
            AssuranceActivityAdapter.Apply(monitor, activity, R("oversized"), maxPayloadCharacters: limit));
        Assert.Equal(TraceEvaluationCodes.TelemetryPayloadLimit, error.Diagnostic.Code);
        Assert.Empty(monitor.Evaluation.Batch.Alternatives[0].Observations);
    }

    [Fact]
    public void ContractContentComparisonUsesOrderedValuesRatherThanArrayIdentity()
    {
        var contract = Batch().Contract;
        var restored = TraceObservationBatch.Deserialize(Batch().Serialize()).Contract;
        Assert.True(contract.SameContent(restored));
        Assert.False(contract.SameContent(null));
        Assert.False(contract.SameContent(restored with { Reference = R("changed") }));
        Assert.False(contract.SameContent(restored with { Scope = R("changed") }));
        Assert.False(contract.SameContent(restored with { Requirements = [.. restored.Requirements.Reverse()] }));
        Assert.False(contract.SameContent(restored with { Requirements = [new(Trigger, ["different"]), new(Witness, ["p"])] }));
        Assert.False(contract.SameContent(restored with { Requirements = [new(R("different"), ["p"]), new(Witness, ["p"])] }));
        var empty = contract with { Requirements = [] };
        Assert.False(empty.SameContent(empty with { Requirements = default }));
        Assert.True((empty with { Requirements = default }).SameContent(empty with { Requirements = default }));
    }

    [Fact]
    public void HostCanCatchProductionTransportAndMonitorRejectionsThroughOneContract()
    {
        var initial = Batch();
        var monitor = TraceMonitor.Start(Claim(), initial);
        var partial = initial with { Enumeration = EnumerationCompleteness.Partial };
        using var oversized = Capture(Batch(Event("paid", 1)));
        using var mismatched = Capture(initial with { Target = R("other-target") });
        Action[] boundaries = [
            () => AssuranceActivityAdapter.CreateEvent(partial),
            () => AssuranceActivityAdapter.Apply(monitor, oversized, R("large"), maxPayloadCharacters: 1),
            () => AssuranceActivityAdapter.Apply(monitor, mismatched, R("wrong-binding"))
        ];
        string[] expected = [TraceEvaluationCodes.TelemetryEnvelope, TraceEvaluationCodes.TelemetryPayloadLimit, TraceEvaluationCodes.TelemetryBinding];
        for (var i = 0; i < boundaries.Length; i++)
        {
            var error = Assert.ThrowsAny<TraceObservationException>(boundaries[i]);
            Assert.Equal(expected[i], error.Diagnostic.Code);
        }
    }

    sealed class RecordingExporter(ExportResult result = ExportResult.Success) : BaseExporter<Activity>
    {
        internal List<Activity> Activities { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            if (result == ExportResult.Success) foreach (var activity in batch) Activities.Add(activity);
            return result;
        }
    }
}

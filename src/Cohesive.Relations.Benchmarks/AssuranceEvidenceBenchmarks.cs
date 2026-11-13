using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Assurance;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm evidence capture and persisted reassessment costs; excludes initial trace evaluation.</summary>
[MemoryDiagnoser]
public class AssuranceEvidenceBenchmarks
{
    [Params(100, 2000)] public int Count { get; set; }
    TraceEvaluation evaluation = null!;
    EvidenceSubject subject = null!;
    string assessment = null!;
    static ClaimReference R(string name) => new(new("bench://" + name), "1", name);

    [GlobalSetup]
    public void Setup()
    {
        var selection = R("states");
        var contract = R("contract");
        var id = new ValueBindingId("value");
        var claim = Claim.Define("positive", "1", R("subject"), contract, R("source"), R("config"))
            .AtEveryCommittedState(selection, InitialStateSelection.Include)
            .Require(Expr.Ge(Expr.BoundValue(id), Expr.Const(0L)),
                [new(id, ClaimInputRole.State, new(new ScalarTypeRef(ScalarTypeKind.Int64)))]).Build();
        var rows = Enumerable.Range(0, Count).Select(i => new SemanticObservation("s" + i, "p", i, i,
            selection, SemanticObservationKind.CommittedState,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.State, ObservationValue.FromInt64(i)))).ToImmutableArray();
        var batch = new TraceObservationBatch(new(contract, R("scope"), [new(selection, ["p"])]), R("target"), R("epoch"),
            [new(R("alternative"), rows, [new(selection, "p", Count, Count, ObservationClosure.Closed, R("closure"))])],
            EnumerationCompleteness.Complete, R("enumeration"));
        evaluation = TraceClaimEvaluator.Evaluate(claim, batch);
        subject = new(EvidenceTargetKind.Implementation, batch.Target, R("configuration"), R("scope"), ProofObligation.Realization);
        var report = Capture();
        assessment = ClaimAssessment.Evaluate(claim, subject, R("policy"),
            [new(report.Kind, report.Method, report.Configuration)], [report], DateTimeOffset.UnixEpoch).Serialize();
    }

    [Benchmark] public ClaimEvidence Capture() => ClaimEvidence.FromTrace(evaluation, subject, R("mapping"), dependenciesComplete: true);
    [Benchmark] public ClaimAssessment RestoreAssessment() => ClaimAssessment.Reassess(assessment);
}

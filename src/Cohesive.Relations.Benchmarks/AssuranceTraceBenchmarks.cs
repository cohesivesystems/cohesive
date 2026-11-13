using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Assurance;
using Cohesive.Model;
namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
public class AssuranceTraceBenchmarks
{
    [Params(100, 2000)] public int Count { get; set; }
    ClaimDocument claim = null!;
    TraceObservationBatch batch = null!;
    readonly TraceEvaluationLimits limits = new(MaxEventCandidateVisits: 10_000_000);
    static ClaimReference R(string name) => new(new("bench://" + name), "1", name);
    [GlobalSetup]
    public void Setup()
    {
        var t = R("trigger"); var w = R("witness"); var contract = R("contract");
        var valueContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int64));
        claim = Claim.Define("match", "1", t, contract, R("source"), R("config"))
            .RequireWithin(t, w, TimeSpan.FromTicks(1))
            .Require(Expr.Eq(Expr.BoundValue(new("t")), Expr.BoundValue(new("w"))),
                [new(new("t"), ClaimInputRole.Trigger, valueContract), new(new("w"), ClaimInputRole.Witness, valueContract)]).Build();
        var observations = Enumerable.Range(0, Count).SelectMany(i => new[] {
            new SemanticObservation("t"+i, "p", i*2, i*20, t, SemanticObservationKind.Event, ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty, ObservationValue.FromInt64(i)),
            new SemanticObservation("w"+i, "p", i*2+1, i*20+1, w, SemanticObservationKind.Event, ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty, ObservationValue.FromInt64(i))
        }).ToImmutableArray();
        batch = new(new(contract,R("scope"),[new(t,["p"]),new(w,["p"])]),R("target"),R("epoch"),
            [new(R("alt"),observations,[new(t,"p",Count*2,Count*20,ObservationClosure.Closed,R("closure")),new(w,"p",Count*2,Count*20,ObservationClosure.Closed,R("closure"))])],EnumerationCompleteness.Complete,R("enum"));
        if (Indexed().Verdict != ClaimVerdict.Satisfied) throw new Exception("Invalid benchmark");
    }
    [Benchmark] public TraceEvaluation Indexed() => TraceClaimEvaluator.Evaluate(claim,batch,limits);
}

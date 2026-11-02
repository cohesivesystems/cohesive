using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Model;
using Cohesive.Model.Expressions;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class MachineAdmissionBenchmarks
{
    [Params(1, 128, 1024)] public int Edges { get; set; }
    MachineDefinition machine = null!;
    ObservationValue facts;
    ObservationValue input;
    string action = null!;
    public sealed record Facts(int Count);
    public sealed record Input(bool Allowed);

    [GlobalSetup]
    public void Setup()
    {
        var source = Machine.Define("benchmark", "1");
        var ready = source.State("ready").Initial();
        for (var index = 0; index < Edges; index++)
            source.Edge("action-" + index).From(ready).To(ready)
                .When((Facts f, Input i) => f.Count > 0 && i.Allowed);
        machine = source.Build();
        action = "action-" + (Edges - 1);
        facts = ObservationValue.FromObject(new Facts(1));
        input = ObservationValue.FromObject(new Input(true));
        if (Before().Kind != After().Kind) throw new InvalidOperationException("Fixture differs.");
    }

    [Benchmark] public MachineDecision After() => machine.Evaluate("ready", action, facts, input);

    // Exact pre-change hot-path strategy retained solely as a measurement baseline.
    [Benchmark(Baseline = true)]
    public MachineDecision Before()
    {
        if (!machine.States.Contains("ready")) throw new InvalidOperationException();
        if (!machine.Edges.Any(e => e.Action == action)) return new(MachineDecisionKind.UnknownAction);
        var candidates = machine.Edges.Where(e => e.From == "ready" && e.Action == action).ToArray();
        if (candidates.Length == 0) return new(MachineDecisionKind.Illegal);
        MachineEdge? selected = null;
        foreach (var edge in candidates)
        {
            var guard = edge.Guard!;
            if (!guard.FactsContract.IsSatisfiedByConstant(facts) || !guard.InputContract.IsSatisfiedByConstant(input))
                throw new InvalidOperationException();
            PortableExpressionValue Resolve(ValueBindingId binding) => binding == guard.FactsBinding
                ? PortableExpressionValue.FromObservation(facts) : PortableExpressionValue.FromObservation(input);
            var evaluator = new PortableExpressionReferenceEvaluator(new(ExprSemanticsCatalog.Default.Capabilities), "machines.reference/v1");
            var result = evaluator.Evaluate(guard.Predicate, new()
            {
                ResolveBinding = Resolve,
                ResolveField = (binding, path) => Resolve(binding ?? guard.FactsBinding).Project(path),
                ResolveParameter = _ => throw new InvalidOperationException()
            });
            if (!result.RequireObservation("guard").GetBoolean()) continue;
            if (selected is not null) return new(MachineDecisionKind.Ambiguous);
            selected = edge;
        }
        return selected is null ? new(MachineDecisionKind.Disabled) : new(MachineDecisionKind.Accepted, selected);
    }
}

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cohesive.Machines;
using Cohesive.Model;
// Required by the historical revision; harmless with the current discoverable extension.
using Cohesive.Relations.Authoring;

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
    }

    [Benchmark] public MachineDecision GuardedAdmission() => machine.Evaluate("ready", action, facts, input);
}

[MemoryDiagnoser]
[ShortRunJob]
public class UnguardedMachineAdmissionBenchmarks
{
    [Params(1, 128, 1024)] public int Edges { get; set; }
    MachineDefinition machine = null!;
    string action = null!;

    [GlobalSetup]
    public void Setup()
    {
        var source = Machine.Define("benchmark", "1");
        var ready = source.State("ready").Initial();
        for (var index = 0; index < Edges; index++)
            source.Edge("action-" + index).From(ready).To(ready);
        machine = source.Build();
        action = "action-" + (Edges - 1);
    }

    [Benchmark] public MachineEdge? Decide() => machine.Decide("ready", action);
    [Benchmark] public System.Collections.Immutable.ImmutableArray<string> Enabled() => machine.Enabled("ready");
}

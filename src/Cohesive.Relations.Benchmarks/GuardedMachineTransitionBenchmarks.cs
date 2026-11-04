using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class GuardedMachineTransitionBenchmarks
{
    [Params(1, 8, 32)] public int Siblings { get; set; }
    [Params(0, 128)] public int UnrelatedEdges { get; set; }
    public sealed record Facts(string Status, int Choice);
    public sealed record Command(bool Allowed);
    MachineDefinition machine = null!;
    CompiledTransitionPlan plan = null!;
    PortableValue facts = null!;
    PortableValue input = null!;

    [GlobalSetup]
    public void Setup()
    {
        var source = Machine.Define("benchmark.guarded-transition", "1");
        var ready = source.State("ready").Initial();
        var done = source.State("done").Final();
        MachineMovement selected = null!;
        for (var index = 0; index < Siblings; index++)
        {
            // Explicit expression source avoids closures in canonical guard lowering.
            var f = Expression.Parameter(typeof(Facts), "facts");
            var i = Expression.Parameter(typeof(Command), "input");
            var predicate = Expression.Lambda<Func<Facts, Command, bool>>(
                Expression.AndAlso(Expression.Equal(Expression.Property(f, nameof(Facts.Choice)), Expression.Constant(index)),
                    Expression.Property(i, nameof(Command.Allowed))), f, i);
            selected = source.Edge("edge-" + index).On("choose").From(ready).To(done).When(predicate);
        }
        for (var index = 0; index < UnrelatedEdges; index++)
            source.Edge("unrelated-" + index).From(ready).To(done);
        machine = source.Build();
        var entity = ObjectEntityDefinition.For<Facts>(new("benchmark.facts"));
        var transition = TransitionAuthoring.Create<Facts, Command, string>(entity.Shape,
            new("benchmark.choose"), new("1"), t => t
                .Move(selected, rejection: (f, i) => "rejected")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "accepted"));
        var compilation = TransitionStaticCompiler.Compile(transition.Document, machineLinks: Link());
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join(";", compilation.Validation.Diagnostics.Select(d => d.Message)));
        plan = compilation.Plan!;
        facts = PortableValue.Concrete(transition.Definition.Observation, ObservationValue.FromObject(new Facts("ready", Siblings - 1)));
        input = PortableValue.Concrete(transition.Definition.Input, ObservationValue.FromObject(new Command(true)));
        if (Admit().Kind != TransitionDecisionKind.Applied) throw new InvalidOperationException("Fixture must admit one edge.");
    }

    // Includes binding construction and all linked evidence, excludes machine source lowering.
    [Benchmark] public TransitionMachineLinkCatalog Link() =>
        new MachineDiscriminatorBinding<Facts, string>(machine, f => f.Status)
            .Map("ready", "ready").Map("done", "done").Build();

    // Full compiled Transition reference admission, including patch/decision materialization.
    [Benchmark] public TransitionDecision Admit() =>
        TransitionReferenceInterpreter.DecideFullState(plan, new("benchmark.operation"), input, facts);
}

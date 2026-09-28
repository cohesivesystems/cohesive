using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Cohesive.Api;
using Cohesive.Api.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Prepared service binding and in-memory invocation costs; excludes host startup and network export.</summary>
[MemoryDiagnoser]
public class ServiceInvocationBenchmarks
{
    ExecutionDefinitionDocument document = null!;
    ServiceTransitionBinding binding = null!;
    ServiceRuntime runtime = null!;
    PortableValue firstInput = null!;
    PortableValue secondInput = null!;
    bool useSecondInput;
    EntityConcurrencyToken token;
    readonly Authority authority = new();
    ActivityListener? listener;

    [Params(false, true)]
    public bool Instrumented { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var entity = Note.Instance.Definition;
        var authored = TransitionAuthoring.Create<Note, Note.Input, bool>(entity.Shape,
            new(new("bench/revise"), new("v1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("bench/services"), DocumentOrigin.Generated)),
            transition => transition.Set(new("set"), note => note.Text, (_, value) => value.Text)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true));
        var compiled = authored.Compile();
        if (!compiled.IsSuccessful) throw new InvalidOperationException("Benchmark transition did not compile.");
        var plan = compiled.Plan!;
        document = ServiceDefinitionDocuments.Create(new("bench/service"), new("v1"),
            new([new ServiceTransitionOperation("revise", entity.StateShape.QualifiedId, plan.DefinitionReference)]),
            plan.Document.Metadata.Provenance);
        var repository = new InMemoryEntityOutboxRepository(entity, _ => "bench");
        var initial = await repository.Upsert(OperationContext.Create(), new(entity.CreateState("note",
            new { Id = "note", Text = "initial" }).Snapshot));
        token = initial.ConcurrencyToken;
        firstInput = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Note.Input("first")));
        secondInput = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Note.Input("second")));
        binding = new("revise", plan, entity, _ => repository);
        runtime = ConstructPreparedRuntime();
        if (Instrumented)
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ExecutionTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(listener);
        }
        await Invoke(runtime);
    }

    [Benchmark]
    public ServiceRuntime ConstructPreparedRuntime() => new(document, [binding], authority);

    [Benchmark]
    public Task<ServiceInvocationResult> ConstructAndInvoke() => Invoke(ConstructPreparedRuntime());

    [Benchmark]
    public Task<ServiceInvocationResult> WarmInvoke() => Invoke(runtime);

    async Task<ServiceInvocationResult> Invoke(ServiceRuntime target)
    {
        var previousToken = token;
        var input = useSecondInput ? secondInput : firstInput;
        useSecondInput = !useSecondInput;
        var result = await target.InvokeAsync(OperationContext.Create(), "revise", "note", token,
            new("benchmark/invocation"), input);
        if (result.Kind != ApiResultKind.Success || result.ConcurrencyToken is null || result.ConcurrencyToken == previousToken)
            throw new InvalidOperationException("Benchmark invocation failed.");
        token = result.ConcurrencyToken.Value;
        return result;
    }

    [GlobalCleanup]
    public void Cleanup() => listener?.Dispose();

    sealed class Authority : IServiceInvocationAuthorization
    {
        public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation) =>
            ValueTask.FromResult<ScopeRef?>(new("bench", "tenant"));
        public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceOperation operation, EntitySnapshot snapshot) =>
            ValueTask.FromResult(true);
    }

    sealed class Note : Entity<Note>
    {
        public sealed record Input(string Text);
        public Note() { Id = WriteOnceField<string>(nameof(Id)); Text = MutableField<string>(nameof(Text)); }
        public Field<string> Id { get; }
        public Field<string> Text { get; }
    }
}

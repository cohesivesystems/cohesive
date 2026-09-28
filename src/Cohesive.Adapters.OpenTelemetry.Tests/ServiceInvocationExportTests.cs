using System.Diagnostics;
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
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Cohesive.Adapters.OpenTelemetry.Tests;

// Native diagnostic listeners are process-wide; serialize tests that select their scopes.
[Collection("OpenTelemetry listeners")]
public sealed class ServiceInvocationExportTests
{
    [Theory]
    [InlineData(ExportResult.Success)]
    [InlineData(ExportResult.Failure)]
    public async Task ExportOutcomeDoesNotChangeCommitOrCanonicalEvidence(ExportResult exportResult)
    {
        var entity = Note.Instance.Definition;
        var compiled = TransitionAuthoring.Create<Note, Note.Input, bool>(entity.Shape,
            new(new("export/revise"), new("v1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("export/test"), DocumentOrigin.Generated)),
            transition => transition.Set(new("set"), note => note.Text, (_, input) => input.Text)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true)).Compile();
        Assert.True(compiled.IsSuccessful);
        var plan = compiled.Plan!;
        var document = ServiceDefinitionDocuments.Create(new("export/service"), new("v1"),
            new([new ServiceTransitionOperation("revise", entity.StateShape.QualifiedId, plan.DefinitionReference)]),
            plan.Document.Metadata.Provenance);
        var repository = new InMemoryEntityOutboxRepository(entity, _ => "private-tenant");
        var initial = await repository.Upsert(OperationContext.Create(), new(entity.CreateState("private-note",
            new { Id = "private-note", Text = "before" }).Snapshot));
        var runtime = new ServiceRuntime(document,
            [new ServiceTransitionBinding("revise", plan, entity, _ => repository)], new Authority());
        var exporter = new RecordingExporter(exportResult);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddCohesiveExecutionInstrumentation()
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using var parent = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = await runtime.InvokeAsync(OperationContext.Create(), "revise", "private-note",
            initial.ConcurrencyToken, new("export/invocation"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Note.Input("private-value"))));
        Assert.Equal(ApiResultKind.Success, result.Kind);
        Assert.NotEqual(initial.ConcurrencyToken, result.ConcurrencyToken);
        var retained = await repository.TryGet(OperationContext.Create(), "private-note",
            EntityReadOptions.Full.WithPartitionKey("private-tenant"));
        Assert.NotNull(retained);
        Assert.Equal("private-value", retained.Entity.Observation.GetField("Text").GetString());
        Assert.Equal(initial.Entity.Version + 1, retained.Entity.Version);
        Assert.Equal(result.ConcurrencyToken, retained.ConcurrencyToken);
        Assert.True(provider.ForceFlush(10_000));
        var span = Assert.Single(exporter.Activities);
        Assert.Equal(parent.TraceId, span.TraceId);
        Assert.Equal(parent.SpanId, span.ParentSpanId);
        Assert.Equal(ExecutionTraceFingerprinter.ComputeSemantic(result.Trace).Value,
            span.GetTagItem(ExecutionTelemetry.TraceFingerprintTagName));
        var tags = string.Join(";", span.TagObjects.Select(pair => pair.Value));
        Assert.DoesNotContain("private-tenant", tags);
        Assert.DoesNotContain("private-note", tags);
        Assert.DoesNotContain("private-value", tags);
    }

    sealed class RecordingExporter(ExportResult result) : BaseExporter<Activity>
    {
        public List<Activity> Activities { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Activities.Add(activity);
            return result;
        }
    }

    sealed class Authority : IServiceInvocationAuthorization
    {
        public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation) =>
            ValueTask.FromResult<ScopeRef?>(new("private-tenant", "tenant"));
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

using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Storage;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Authoring;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;

namespace Cohesive.Tests.Api;

public sealed class DurableServiceProcessProviderTests
{
    [Fact]
    public async Task Empty_deployment_fails_before_worker_start()
    {
        await using var services = Services().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<DurableTaskServiceProcessProvider>().Complete());
    }

    [Fact]
    public async Task Native_registration_has_one_shared_provider_and_rejects_a_second_registration()
    {
        var registrations = Services();
        Assert.Throws<InvalidOperationException>(() => registrations.AddCohesiveDurableTaskServices(_ => { }, _ => { }));
        await using var services = registrations.BuildServiceProvider();
        Assert.Same(services.GetRequiredService<DurableTaskServiceProcessProvider>(), services.GetRequiredService<IDurableServiceProcessProvider>());
        Assert.Same(services.GetRequiredService<IDurableServiceProcessProvider>(), services.GetRequiredService<Cohesive.Processes.Execution.IAsyncProcessReferenceHost>());
    }

    [Fact]
    public async Task Admission_is_offline_and_freezes_before_execution()
    {
        await using var services = Services().BuildServiceProvider();
        var provider = services.GetRequiredService<DurableTaskServiceProcessProvider>();
        var (prepared, declaration) = Process();
        _ = provider.Bind(prepared, declaration, "tests", new Authorization());
        Assert.Same(provider.Complete(), provider.Complete());
        Assert.Throws<InvalidOperationException>(() => provider.Bind(prepared, declaration, "tests", new Authorization()));
    }

    [Fact]
    public async Task Duplicate_process_is_rejected_without_invalidating_first_admission()
    {
        await using var services = Services().BuildServiceProvider();
        var provider = services.GetRequiredService<DurableTaskServiceProcessProvider>();
        var (prepared, declaration) = Process();
        provider.Bind(prepared, declaration, "tests", new Authorization());
        var failure = Assert.Throws<ServiceBindingValidationException>(() => provider.Bind(prepared, declaration, "tests", new Authorization()));
        Assert.Contains("services.binding.duplicateProcess", failure.Message);
        Assert.NotNull(provider.Complete());
    }

    [Fact]
    public async Task Shared_query_routes_by_exact_originating_process_and_rejects_ambiguous_legacy_envelopes()
    {
        await using var services = Services().BuildServiceProvider();
        var provider = services.GetRequiredService<DurableTaskServiceProcessProvider>();
        var first = QueryProcess("first");
        var second = QueryProcess("second");
        provider.Bind(first.Prepared, first.Declaration, "tests", new Authorization());
        provider.Bind(second.Prepared, second.Declaration, "tests", new Authorization());
        provider.Complete();
        foreach (var (prepared, expected) in new[] { (first.Prepared, "first"), (second.Prepared, "second") })
        {
            var evaluation = Evaluation(prepared) with { Process = prepared.Plan.DefinitionReference };
            var result = await provider.EvaluateRelationAsync(OperationContext.Create(), evaluation);
            Assert.Equal(expected, result.Value!.Value!.Value.GetRequiredString());
            var executed = await new EphemeralProcessExecutor(prepared.Plan).ExecuteAsync(OperationContext.Create(),
                evaluation.Continuation, evaluation.Input, evaluation.Context, provider, TimeSpan.FromSeconds(5));
            Assert.Equal(expected, Assert.IsType<ObservationValue>(executed.Decision.State.Terminal.Detail!.Value!.Value).GetRequiredString());
        }
        var legacy = Evaluation(first.Prepared);
        Assert.Throws<InvalidOperationException>(() => provider.EvaluateRelationAsync(OperationContext.Create(), legacy));
        var undeployed = Process().Item1.Plan.DefinitionReference;
        Assert.Throws<InvalidOperationException>(() => provider.EvaluateRelationAsync(OperationContext.Create(), legacy with { Process = undeployed }));
    }

    [Fact]
    public async Task Legacy_query_envelope_requires_one_deployed_owner()
    {
        await using var services = Services().BuildServiceProvider();
        var provider = services.GetRequiredService<DurableTaskServiceProcessProvider>();
        var first = QueryProcess("first");
        provider.Bind(first.Prepared, first.Declaration, "tests", new Authorization());
        provider.Complete();
        var result = await provider.EvaluateRelationAsync(OperationContext.Create(), Evaluation(first.Prepared));
        Assert.Equal("first", result.Value!.Value!.Value.GetRequiredString());
        var unknown = Evaluation(first.Prepared) with { Definition = Process().Item1.Plan.DefinitionReference };
        Assert.Throws<InvalidOperationException>(() => provider.EvaluateRelationAsync(OperationContext.Create(), unknown));
    }

    static (PreparedServiceProcess Prepared, ExecutionDefinitionDocument Declaration) QueryProcess(string name)
    {
        var provenance = new ExecutionProvenance(new("tests"), new("tests/durable-service"), DocumentOrigin.Generated);
        var query = HostedQuery<string, string>.Create(new("shared-query"), new("1"), new("test/shared", "1"), "text", provenance);
        var output = new ValueBindingId("query-result");
        var document = ProcessDefinitionDocuments.Create(new(name), new("1"),
            new(query.InputContract, query.ResultContract, new("read"), [
                new EvaluateRelationProcessNode(new("read"), query.Reference, Expr.BoundValue(ProcessBindingIds.Input),
                    new(new(new("next"), new("return")), new(output, query.ResultContract))),
                new ReturnProcessNode(new("return"), Expr.BoundValue(output))], ProcessRecoveryPolicy.ContinueAttempt), provenance);
        var process = ProcessAuthoring.Project<string, string>(document);
        var declaration = Service.Define(new(name + "-service"), new("1"), provenance)
            .Operation("start").Run(process).ReturnAfterAdmission(ProcessExecutionLifetime.Durable)
            .Operation("result").ReadResultOf(process).Build();
        var prepared = Service.Define(new(name + "-bindings"), new("1"), provenance).Operation("start")
            .Run(process, host => host.Query(query, (_, _, _) => ValueTask.FromResult(name))).Prepare();
        return (prepared, declaration);
    }

    static ProcessRelationEvaluation Evaluation(PreparedServiceProcess prepared) => new(
        prepared.Plan.Definition.Nodes.OfType<EvaluateRelationProcessNode>().Single().Relation,
        PortableValue.Concrete(prepared.Plan.Definition.Input, ObservationValue.FromString("input")),
        new(new("test-instance"), new("test-attempt")), new("test-activation"), new("test-token"), new("read"), 0,
        DateTimeOffset.UtcNow, new(new("tests", "tests"), new("test-instance"),
            new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), prepared.Plan.Document.Metadata.Provenance));

    static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // No server listens here. Preparation must not issue native calls.
        const string scheduler = "Endpoint=http://127.0.0.1:1;TaskHub=default;Authentication=None";
        services.AddCohesiveDurableTaskServices(client => client.UseDurableTaskScheduler(scheduler), worker => worker.UseDurableTaskScheduler(scheduler));
        return services;
    }

    static (PreparedServiceProcess, ExecutionDefinitionDocument) Process()
    {
        var provenance = new ExecutionProvenance(new("tests"), new("tests/durable-service"), DocumentOrigin.Generated);
        var text = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var document = ProcessDefinitionDocuments.Create(new("echo"), new("1"),
            new(text, text, new("return"), [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))],
                ProcessRecoveryPolicy.ContinueAttempt), provenance);
        var process = ProcessAuthoring.Project<string, string>(document);
        var service = Service.Define(new("echo-service"), new("1"), provenance)
            .Operation("start").Run(document).ReturnAfterAdmission(ProcessExecutionLifetime.Durable)
            .Operation("result").ReadResultOf(process);
        return (service.PrepareProcess(process, _ => throw new InvalidOperationException("Echo has no storage.")), service.Build());
    }

    sealed class Authorization : IServiceInvocationAuthorization
    {
        public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation) => ValueTask.FromResult<ScopeRef?>(new("tests", "tests", PartitionKey: "tests"));
        public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceOperation operation, EntitySnapshot snapshot) => ValueTask.FromResult(true);
    }
}

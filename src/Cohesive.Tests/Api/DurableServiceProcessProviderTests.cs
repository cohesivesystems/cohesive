using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Storage;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
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

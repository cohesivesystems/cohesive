using Cohesive.Adapters.DurableTask;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskProcessExecutionRepositoryRegistrationTests
{
    [Fact]
    public void Registration_DeclaresOneAuthorityForEachConfiguredCapability()
    {
        ServiceCollection services = [];

        services.AddCohesiveDurableTaskProcessExecutionRepository(static _ =>
                throw new InvalidOperationException("Registration-only test."))
            .DecorateExecutionRepository<DecoratedExecutionRepository>(static (_, repository) => new(repository))
            .AddExecutionExplainRepository(static _ =>
                throw new InvalidOperationException("Registration-only test."));

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(DurableTaskProcessExecutionRepository));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IProcessExecutionRepository));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IProcessExecutionValueRepository));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IProcessExecutionTraceRepository));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(DecoratedExecutionRepository));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(DurableTaskProcessExecutionExplainRepository));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IProcessExecutionExplainRepository));
    }

    [Fact]
    public void Registration_RejectsAnExistingCapabilityAuthority()
    {
        ServiceCollection services = [];
        services.AddSingleton<IProcessExecutionRepository, DecoratedExecutionRepository>();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddCohesiveDurableTaskProcessExecutionRepository(static _ =>
                throw new InvalidOperationException("Registration-only test.")));

        Assert.Contains(typeof(IProcessExecutionRepository).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("one authority", error.Message, StringComparison.Ordinal);
    }

    sealed class DecoratedExecutionRepository(IProcessExecutionRepository? inner = null) : IProcessExecutionRepository
    {
        public ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId) =>
            inner?.GetAsync(context, processId) ?? ValueTask.FromResult<ProcessExecutionRecord?>(null);

        public ValueTask<ProcessExecutionRecord?> GetAsync(
            OperationContext context,
            InteractionAuthorityScope authorityScope,
            ProcessInstanceId processInstanceId) =>
            inner?.GetAsync(context, authorityScope, processInstanceId)
            ?? ValueTask.FromResult<ProcessExecutionRecord?>(null);

        public ValueTask<ProcessExecutionQueryResult> QueryAsync(
            OperationContext context,
            ProcessExecutionQuery query) =>
            inner?.QueryAsync(context, query)
            ?? ValueTask.FromResult(new ProcessExecutionQueryResult([], null));
    }
}

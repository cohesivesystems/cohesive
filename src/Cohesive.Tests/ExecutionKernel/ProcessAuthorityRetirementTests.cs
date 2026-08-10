using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Analyzers;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessAuthorityRetirementTests
{
    [Fact]
    public void ShippedProcessAssembly_DoesNotContainDelegateOrSingleCursorAuthorities()
    {
        var assembly = typeof(ProcessDefinitionDocuments).Assembly;
        string[] retiredTypes =
        [
            "Cohesive.Processes.Model.ProcessDefinition",
            "Cohesive.Processes.Model.ProcessDefinitionBuilder",
            "Cohesive.Processes.Model.ProcessNode",
            "Cohesive.Processes.Model.TypedProcessDefinition`2",
            "Cohesive.Processes.Runtime.IProcessEngine",
            "Cohesive.Processes.Runtime.ProcessCheckpoint",
            "Cohesive.Processes.Runtime.ProcessEngine",
            "Cohesive.Processes.Runtime.ProcessExecutionContext",
            "Cohesive.Processes.Runtime.ProcessExecutionPlanner",
            "Cohesive.Processes.Runtime.ProcessNodeExecutor"
        ];

        Assert.All(retiredTypes, typeName => Assert.Null(assembly.GetType(typeName)));
    }

    [Fact]
    public void ShippedDurableTaskAssembly_DoesNotRestoreRetiredCallbackAuthority()
    {
        var assembly = typeof(DurableTaskProcessExecutionRepository).Assembly;
        string[] retiredTypes =
        [
            "Cohesive.Adapters.DurableTask.DurableTaskExecuteProcessNodeActivity",
            "Cohesive.Adapters.DurableTask.DurableTaskProcessDefinitionRegistry",
            "Cohesive.Adapters.DurableTask.DurableTaskProcessEngine",
            "Cohesive.Adapters.DurableTask.DurableTaskProcessHost",
            "Cohesive.Adapters.DurableTask.DurableTaskProcessOrchestration"
        ];

        Assert.All(retiredTypes, typeName => Assert.Null(assembly.GetType(typeName)));
        Assert.Contains(
            assembly.GetExportedTypes(),
            static type => type == typeof(DurableTaskProcessExecutionRepository));
        Assert.Contains(
            assembly.GetExportedTypes(),
            static type => type == typeof(DurableTaskSequentialProcessOrchestrator));
        Assert.Contains(
            assembly.GetExportedTypes(),
            static type => type == typeof(DurableTaskSequentialProcessPlanCatalog));
    }

    [Fact]
    public void ShippedAspNetAssembly_DoesNotExposeLegacyProcessStartEndpoints()
    {
        var assembly = typeof(ProcessExecutionApiEndpointRouteBuilderExtensions).Assembly;
        string[] retiredTypes =
        [
            "Cohesive.Adapters.AspNet.Processes.ProcessApiEndpointRouteBuilderExtensions",
            "Cohesive.Adapters.AspNet.Processes.ProcessApiEndpointOptions`4",
            "Cohesive.Adapters.AspNet.Processes.ProcessEndpointRouteBuilderExtensions",
            "Cohesive.Adapters.AspNet.Processes.ProcessEndpointOptions`3"
        ];

        Assert.All(retiredTypes, typeName => Assert.Null(assembly.GetType(typeName)));
    }

    [Fact]
    public void ShippedStorageAssembly_DoesNotContainLegacyEntityProcessAdapter()
    {
        var assembly = typeof(ProcessDurableRuntime).Assembly;

        Assert.Null(assembly.GetType("Cohesive.Storage.ProcessEntityRepositoryAdapter"));
        Assert.Null(assembly.GetType("Cohesive.Storage.ObservationProcessEntityRepositoryOptions"));
        Assert.Contains(
            assembly.GetExportedTypes(),
            static type => type == typeof(ProcessDurableRuntime));
    }

    [Fact]
    public void ShippedAnalyzerAssembly_DoesNotContainDelegateProcessGenerator()
    {
        var assembly = typeof(CodeSetSourceGenerator).Assembly;

        Assert.Null(assembly.GetType("Cohesive.Analyzers.ProcessFlowSourceGenerator"));
    }
}

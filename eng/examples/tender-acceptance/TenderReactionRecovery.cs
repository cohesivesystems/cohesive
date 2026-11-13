using Cohesive.Execution;
using Cohesive.Prelude;
using Cohesive.Storage.Processes;

namespace Cohesive.Examples.TenderAcceptance;

/// <summary>Bounded worker composition over the same Process authority used by source admission.</summary>
internal static class TenderReactionRecovery
{
    // A supervisor invokes another pass after the configured interval. Cursor state is an optimization:
    // losing it simply rescans; no source receipt, in-memory queue or target command must be reconstructed.
    internal static async Task<ProcessInstanceId?> RunPageAsync(OperationContext context, IProcessInstanceSource source,
        IReadOnlyList<MachineReactionHost> policies, ProcessInstanceId? after,
        Func<ProcessInstanceId, MachineReactionProgress, Task> observe, int pageSize = 32)
    {
        var instances = await source.ListInstancesAsync(context, after, pageSize);
        foreach (var instance in instances)
            foreach (var host in policies)
                if (await host.RecoverAsync(context, instance) is { } progress)
                    await observe(instance, progress);
        return instances.IsEmpty ? null : instances[^1];
    }
}

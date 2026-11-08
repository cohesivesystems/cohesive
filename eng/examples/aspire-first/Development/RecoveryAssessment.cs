using AspireFirst.Orders;
using Cohesive.Adapters.Mcp.Development;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.DurableTask;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Transitions.Authoring;
using System.Text.Json;

// Source-selected recovery policy, assessed independently of the example's ordinary DTS execution profile.
internal sealed class RecoveryAssessment : IProcessDependencyReceiver
{
    readonly List<ProcessDefinitionLink> links = [];
    internal static JsonElement[] Export(CancellationProcess process)
    {
        var receiver = new RecoveryAssessment();
        foreach (var dependency in process.Dependencies) dependency.Contribute(receiver);
        var result = ProcessStaticCompiler.Compile(process.Definition.Document, new(receiver.links),
            CancellationProcess.FencedRecoveryCompilation);
        var plan = result.Plan ?? throw new InvalidOperationException(string.Join("; ", result.Validation.Diagnostics.Select(d => d.Message)));
        return [.. new[] { PostgresProcessRecoveryProfile.Fenced, DurableTaskProcessTargetProfile.Executable,
            Cohesive.Processes.Execution.EphemeralProcessExecutor.CapabilityProfile }.Select(profile => ProcessRecoveryWorkspaceProjection.Export(plan, profile))];
    }
    public void Transition<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition) where TEntity : notnull
    {
        var validation = ProcessDefinitionLink.TryCreateTransition(transition.Document, out var link);
        if (!validation.IsValid) throw new InvalidOperationException("Invalid transition dependency.");
        links.Add(link!);
    }
    public void PointRead<TEntity, TInput, TResult>(EntityPointRead<TEntity, TInput, TResult> read)
        where TEntity : class where TInput : notnull where TResult : notnull => links.Add(read.Query.CreateProcessDefinitionLink());
    public void Query<TInput, TResult>(ProcessQuery<TInput, TResult> query) where TInput : notnull where TResult : notnull
        => links.Add(query.Contract.CreateProcessDefinitionLink());
}

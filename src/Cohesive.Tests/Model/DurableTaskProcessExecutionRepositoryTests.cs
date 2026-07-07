using DurableTask.Core;
using DurableTask.Core.Query;
using DurableTask.Core.Serializing;

namespace Cohesive.Tests.Model;

public sealed class DurableTaskProcessExecutionRepositoryTests
{
    [Fact]
    public async Task QueryAsync_MapsProcessQueryToDurableTaskQuery()
    {
        var converter = CreateConverter();
        var queryClient = new FakeOrchestrationServiceQueryClient([
            CreateState(
                instanceId: "shape-graph-compilation--default--001",
                orchestrationStatus: OrchestrationStatus.Running,
                customStatus: converter.Serialize(new DurableTaskProcessOrchestrationStatus(
                    ProcessName: "CompileShapeGraph",
                    Status: ProcessExecutionStatus.Waiting,
                    CurrentNode: "wait",
                    CurrentPlace: "default",
                    Wait: null)),
                input: converter.Serialize(new DurableTaskProcessRequest(
                    ProcessName: "CompileShapeGraph",
                    Parameters: new Dictionary<string, object?> { ["trigger"] = new SampleTrigger("edi-1") },
                    RunOptions: new() { ProcessId = "shape-graph-compilation--default--001" })),
                createdTime: new DateTime(2026, 05, 05, 12, 00, 00, DateTimeKind.Utc),
                updatedTime: new DateTime(2026, 05, 05, 12, 01, 00, DateTimeKind.Utc))
        ], continuationToken: "ct-out");
        var repository = new DurableTaskProcessExecutionRepository(queryClient, taskHubName: "arihub", dataConverter: converter);

        var result = await repository.QueryAsync(OperationContext.Create(), new()
        {
            ProcessIdPrefix = "shape-graph-compilation--default--",
            Statuses =
            [
                ProcessExecutionStatus.Pending,
                ProcessExecutionStatus.Waiting
            ],
            Limit = 25,
            ContinuationToken = "ct-in"
        });

        Assert.NotNull(queryClient.LastQuery);
        Assert.Equal("shape-graph-compilation--default--", queryClient.LastQuery.InstanceIdPrefix);
        Assert.Equal(25, queryClient.LastQuery.PageSize);
        Assert.Equal("ct-in", queryClient.LastQuery.ContinuationToken);
        Assert.NotNull(queryClient.LastQuery.TaskHubNames);
        Assert.Contains("arihub", queryClient.LastQuery.TaskHubNames);
        Assert.NotNull(queryClient.LastQuery.RuntimeStatus);
        Assert.Contains(OrchestrationStatus.Pending, queryClient.LastQuery.RuntimeStatus);
        Assert.Contains(OrchestrationStatus.Running, queryClient.LastQuery.RuntimeStatus);
        Assert.True(queryClient.LastQuery.FetchInputsAndOutputs);
        Assert.True(queryClient.LastQuery.ExcludeEntities);

        var item = Assert.Single(result.Items);
        Assert.Equal("ct-out", result.ContinuationToken);
        Assert.Equal("shape-graph-compilation--default--001", item.ProcessId);
        Assert.Equal("CompileShapeGraph", item.ProcessName);
        Assert.Equal(ProcessExecutionStatus.Waiting, item.Status);
        Assert.Equal(new DateTimeOffset(2026, 05, 05, 12, 00, 00, TimeSpan.Zero), item.StartedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 05, 05, 12, 01, 00, TimeSpan.Zero), item.UpdatedAtUtc);
        Assert.Null(item.CompletedAtUtc);
        Assert.NotNull(item.Parameters);
        var trigger = Assert.IsType<SampleTrigger>(item.Parameters["trigger"]);
        Assert.Equal("edi-1", trigger.EdiSpecId);
    }

    [Fact]
    public async Task GetAsync_FiltersPrefixResultsToExactProcessId()
    {
        var converter = CreateConverter();
        var queryClient = new FakeOrchestrationServiceQueryClient([
            CreateState(
                instanceId: "proc-1-extra",
                orchestrationStatus: OrchestrationStatus.Running,
                customStatus: null,
                input: converter.Serialize(new DurableTaskProcessRequest("wrong", null, new() { ProcessId = "proc-1-extra" })),
                createdTime: DateTime.UtcNow,
                updatedTime: DateTime.UtcNow),
            CreateState(
                instanceId: "proc-1",
                orchestrationStatus: OrchestrationStatus.Pending,
                customStatus: null,
                input: converter.Serialize(new DurableTaskProcessRequest("right", null, new() { ProcessId = "proc-1" })),
                createdTime: DateTime.UtcNow,
                updatedTime: DateTime.UtcNow)
        ]);
        var repository = new DurableTaskProcessExecutionRepository(queryClient, dataConverter: converter);

        var result = await repository.GetAsync(OperationContext.Create(), "proc-1");

        Assert.NotNull(queryClient.LastQuery);
        Assert.Equal("proc-1", queryClient.LastQuery.InstanceIdPrefix);
        Assert.NotNull(result);
        Assert.Equal("proc-1", result.ProcessId);
        Assert.Equal("right", result.ProcessName);
    }

    [Fact]
    public async Task QueryAsync_UsesTerminalRuntimeStatusWhenCustomStatusIsStale()
    {
        var converter = CreateConverter();
        var queryClient = new FakeOrchestrationServiceQueryClient([
            CreateState(
                instanceId: "proc-failed",
                orchestrationStatus: OrchestrationStatus.Failed,
                customStatus: converter.Serialize(new DurableTaskProcessOrchestrationStatus(
                    ProcessName: "CompileShapeGraph",
                    Status: ProcessExecutionStatus.Running,
                    CurrentNode: "compile",
                    CurrentPlace: "default",
                    Wait: null)),
                input: converter.Serialize(new DurableTaskProcessRequest(
                    ProcessName: "CompileShapeGraph",
                    Parameters: new Dictionary<string, object?> { ["trigger"] = new SampleTrigger("edi-1") },
                    RunOptions: new() { ProcessId = "proc-failed" })),
                createdTime: new DateTime(2026, 05, 05, 12, 00, 00, DateTimeKind.Utc),
                updatedTime: new DateTime(2026, 05, 05, 12, 01, 00, DateTimeKind.Utc),
                failureDetails: new(
                    errorType: typeof(InvalidOperationException).FullName!,
                    errorMessage: "Compilation failed.",
                    stackTrace: "stack",
                    innerFailure: null,
                    isNonRetriable: false))
        ]);
        var repository = new DurableTaskProcessExecutionRepository(queryClient, dataConverter: converter);

        var runningResult = await repository.QueryAsync(OperationContext.Create(), new()
        {
            Statuses = [ProcessExecutionStatus.Running]
        });

        Assert.Empty(runningResult.Items);
        Assert.NotNull(queryClient.LastQuery?.RuntimeStatus);
        Assert.Contains(OrchestrationStatus.Running, queryClient.LastQuery.RuntimeStatus);

        var failedResult = await repository.QueryAsync(OperationContext.Create(), new()
        {
            Statuses = [ProcessExecutionStatus.Failed]
        });

        var item = Assert.Single(failedResult.Items);
        Assert.Equal(ProcessExecutionStatus.Failed, item.Status);
        Assert.Equal("Compilation failed.", item.FailureMessage);
        Assert.NotNull(item.Error);
        Assert.Equal(typeof(InvalidOperationException).FullName, item.Error.ErrorType);
        Assert.NotNull(queryClient.LastQuery?.RuntimeStatus);
        Assert.Contains(OrchestrationStatus.Failed, queryClient.LastQuery.RuntimeStatus);
    }

    [Fact]
    public async Task QueryAsync_UsesFailureDetailsWhenRuntimeStatusIsStale()
    {
        var converter = CreateConverter();
        var queryClient = new FakeOrchestrationServiceQueryClient([
            CreateState(
                instanceId: "proc-failed",
                orchestrationStatus: OrchestrationStatus.Pending,
                customStatus: null,
                input: converter.Serialize(new DurableTaskProcessRequest(
                    ProcessName: "CompileShapeGraph",
                    Parameters: null,
                    RunOptions: new() { ProcessId = "proc-failed" })),
                createdTime: new DateTime(2026, 05, 05, 12, 00, 00, DateTimeKind.Utc),
                updatedTime: new DateTime(2026, 05, 05, 12, 01, 00, DateTimeKind.Utc),
                failureDetails: new(
                    errorType: typeof(InvalidOperationException).FullName!,
                    errorMessage: "Compilation failed.",
                    stackTrace: "stack",
                    innerFailure: null,
                    isNonRetriable: false))
        ]);
        var repository = new DurableTaskProcessExecutionRepository(queryClient, dataConverter: converter);

        var result = await repository.QueryAsync(OperationContext.Create(), new()
        {
            Statuses = [ProcessExecutionStatus.Failed]
        });

        var item = Assert.Single(result.Items);
        Assert.Equal(ProcessExecutionStatus.Failed, item.Status);
        Assert.Equal("Compilation failed.", item.FailureMessage);
    }

    static DataConverter CreateConverter() => new DurableTaskSystemTextJsonDataConverter();

    static OrchestrationState CreateState(
        string instanceId,
        OrchestrationStatus orchestrationStatus,
        string? customStatus,
        string? input,
        DateTime createdTime,
        DateTime updatedTime,
        FailureDetails? failureDetails = null
        ) => new()
        {
            OrchestrationInstance = new() { InstanceId = instanceId },
            OrchestrationStatus = orchestrationStatus,
            Status = customStatus,
            Input = input,
            FailureDetails = failureDetails,
            CreatedTime = createdTime,
            LastUpdatedTime = updatedTime,
            CompletedTime = default
        };

    sealed class FakeOrchestrationServiceQueryClient(
        IReadOnlyCollection<OrchestrationState> states,
        string? continuationToken = null
        ) : IOrchestrationServiceQueryClient
    {
        public OrchestrationQuery? LastQuery { get; private set; }

        public Task<OrchestrationQueryResult> GetOrchestrationWithQueryAsync(OrchestrationQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = query;
            return Task.FromResult(new OrchestrationQueryResult(states, continuationToken));
        }
    }

    sealed record SampleTrigger(string EdiSpecId);
}

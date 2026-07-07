using DurableTask.Core;
using DurableTask.Core.Serializing;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Cohesive.Adapters.DurableTask;

sealed record DurableTaskProcessNodeRequest(
    string ProcessName,
    ProcessCheckpoint Checkpoint
);

sealed class DurableTaskExecuteProcessNodeActivity(
    ProcessNodeExecutor nodeExecutor,
    DurableTaskProcessDefinitionRegistry definitions,
    DataConverter dataConverter,
    ILogger<DurableTaskExecuteProcessNodeActivity> logger
    ) : TaskActivity
{
    readonly ProcessNodeExecutor nodeExecutor = Guard.RequireNotNull(nodeExecutor);
    readonly DurableTaskProcessDefinitionRegistry definitions = Guard.RequireNotNull(definitions);
    readonly DataConverter dataConverter = Guard.RequireNotNull(dataConverter);
    readonly ILogger<DurableTaskExecuteProcessNodeActivity> logger = Guard.RequireNotNull(logger);

    public override string Run(TaskContext context, string input) =>
        RunAsync(context, input).GetAwaiter().GetResult();

    public override async Task<string> RunAsync(TaskContext context, string input)
    {
        ArgumentNullException.ThrowIfNull(context);
        logger.LogDebug("Starting durable activity '{ActivityName}' with raw input payload length {PayloadLength}.",
            nameof(DurableTaskExecuteProcessNodeActivity),
            input?.Length ?? 0
            );

        var deserialized = dataConverter.Deserialize<DurableTaskProcessNodeRequest>(UnwrapActivityInput(input ?? string.Empty));
        if (deserialized is not { } request)
        {
            throw new InvalidOperationException($"Durable activity expected '{nameof(DurableTaskProcessNodeRequest)}' input but received '{deserialized?.GetType().FullName ?? "<null>"}'.");
        }

        ValidateRequest(request);
        logger.LogDebug(
            "Executing durable activity for process '{ProcessName}' ({ProcessId}) at node '{NodeName}' in place '{Place}'.",
            request.ProcessName,
            request.Checkpoint.ProcessId,
            request.Checkpoint.CurrentNode ?? "<none>",
            request.Checkpoint.CurrentPlace
            );

        try
        {
            var process = definitions.Get(processName: request.ProcessName);
            var checkpoint = await nodeExecutor.ExecuteNodeAsync(context: OperationContext.Create(), process: process, checkpoint: request.Checkpoint);
            logger.LogDebug(
                "Completed durable activity for process '{ProcessName}' ({ProcessId}); next node '{NodeName}', place '{Place}', status '{Status}'.",
                request.ProcessName,
                checkpoint.ProcessId,
                checkpoint.CurrentNode ?? "<none>",
                checkpoint.CurrentPlace,
                checkpoint.Status
                );
            return dataConverter.Serialize(checkpoint);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Durable activity failed for process '{ProcessName}' ({ProcessId}) at node '{NodeName}' in place '{Place}'.",
                request.ProcessName,
                request.Checkpoint.ProcessId,
                request.Checkpoint.CurrentNode ?? "<none>",
                request.Checkpoint.CurrentPlace
                );
            var details = ex.ToString().Replace(Environment.NewLine, " | ", StringComparison.Ordinal);
            throw new InvalidOperationException(
                $"Durable activity failed while executing node '{request.Checkpoint.CurrentNode ?? "<none>"}' " +
                $"for process '{request.ProcessName}' ({request.Checkpoint.ProcessId}). {details}"
                );
        }
    }

    static void ValidateRequest(DurableTaskProcessNodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcessName);
        ArgumentNullException.ThrowIfNull(request.Checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Checkpoint.ProcessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Checkpoint.ProcessName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Checkpoint.CurrentPlace);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.Parameters);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.Variables);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.ContinuationFrames);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.Transitions);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.ExecutedEffects);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.PendingEffects);
        ArgumentNullException.ThrowIfNull(request.Checkpoint.DeadLetters);
    }

    static string UnwrapActivityInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
            return input;

        if (document.RootElement.GetArrayLength() == 0)
            return string.Empty;

        if (document.RootElement.GetArrayLength() > 1)
            throw new InvalidOperationException(
                $"Durable activity expected a single '{nameof(DurableTaskProcessNodeRequest)}' argument but received multiple parameters.");

        var payload = document.RootElement[0];
        return payload.ValueKind == JsonValueKind.String
            ? payload.GetString() ?? string.Empty
            : payload.GetRawText();
    }
}

using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Worker;

namespace Cohesive.Adapters.Azure.Qualification;

/// <summary>Opt-in, side-effect-free worker tasks and an explicitly invoked execution challenge.</summary>
public static class SchedulerRuntimeQualification
{
    /// <summary>Versioned orchestration identity; never invokes a product workflow.</summary>
    public const string OrchestrationName = "cohesive-runtime-qualification-v1";
    /// <summary>Versioned identity of the single echo activity.</summary>
    public const string ActivityName = "cohesive-runtime-qualification-echo-v1";

    /// <summary>Registers only an orchestration and one pure echo activity. Call solely under explicit deployment policy.</summary>
    public static IDurableTaskWorkerBuilder AddRuntimeQualification(this IDurableTaskWorkerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTasks(tasks =>
        {
            tasks.AddOrchestrator(OrchestrationName, () => new EchoOrchestrator());
            tasks.AddActivityFunc<string, string>(ActivityName, (_, challenge) => ValidateChallenge(challenge));
        });
    }

    /// <summary>Schedules once, waits for exact challenge completion, and retains history for review. Never purges or terminates.</summary>
    /// <remarks>Worker registration must be independently verified first. All statuses are deduplicated; a fresh random
    /// challenge prevents a previous instance from satisfying this attempt. Timeout/cancellation can leave one pending/running
    /// instance, reported Unresolved. No automatic retry, termination or broad history operation is performed.</remarks>
    public static async Task<RuntimeQualificationResult> ExecuteAsync(DurableTaskClient client, RuntimeQualificationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(options);
        if (cancellationToken.IsCancellationRequested)
            return new(options.RunId, options.ObjectName, QualificationOutcome.NotStarted, QualificationCleanup.NotRequired, "qualification.canceled");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.OperationTimeout);
        var challenge = Guid.NewGuid().ToString("N");
        var diagnostic = "qualification.schedulerAdmissionFailed";
        var cleanup = QualificationCleanup.NotRequired;
        try
        {
            // This read is not an atomic ownership claim; deduplication and the independent challenge protect the race.
            if (await client.GetInstanceAsync(options.ObjectName, false, budget.Token).ConfigureAwait(false) is not null)
                return new(options.RunId, options.ObjectName, QualificationOutcome.Collision, QualificationCleanup.NotRequired, "qualification.collision");
            diagnostic = "qualification.schedulerExecutionUnknown";
            cleanup = QualificationCleanup.Unresolved;
            var id = await client.ScheduleNewOrchestrationInstanceAsync(OrchestrationName, challenge,
                new StartOrchestrationOptions { InstanceId = options.ObjectName, DedupeStatuses = Enum.GetNames<OrchestrationRuntimeStatus>() }, budget.Token).ConfigureAwait(false);
            if (id != options.ObjectName) throw new InvalidOperationException();
            var completed = await client.WaitForInstanceCompletionAsync(id, true, budget.Token).ConfigureAwait(false);
            if (completed.Name != OrchestrationName || completed.InstanceId != id
                || completed.RuntimeStatus != OrchestrationRuntimeStatus.Completed
                || completed.ReadInputAs<string>() != challenge || completed.ReadOutputAs<string>() != challenge)
                throw new InvalidOperationException();
            return new(options.RunId, options.ObjectName, QualificationOutcome.Verified, QualificationCleanup.HistoryRetained, null);
        }
        catch (Exception)
        {
            return new(options.RunId, options.ObjectName, QualificationOutcome.Unverified, cleanup, diagnostic);
        }
    }

    internal static string ValidateChallenge(string challenge)
    {
        if (challenge is null || challenge.Length != 32 || !Guid.TryParseExact(challenge, "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("Invalid qualification challenge.");
        return challenge;
    }

    internal sealed class EchoOrchestrator : TaskOrchestrator<string, string>
    {
        public override Task<string> RunAsync(TaskOrchestrationContext context, string input)
        {
            if (!context.InstanceId.StartsWith("cohesive-qualification-", StringComparison.Ordinal)) throw new ArgumentException("Invalid qualification instance.");
            return context.CallActivityAsync<string>(ActivityName, ValidateChallenge(input));
        }
    }
}

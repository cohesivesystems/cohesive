using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using DurableTask.Core.Exceptions;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Worker;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Creates the portable JSON converter required by standalone Durable Task Process workers and clients.</summary>
public static class DurableTaskProcessDataConverter
{
    /// <summary>Creates a System.Text.Json converter with the canonical Cohesive portable-value converters.</summary>
    /// <returns>A new converter suitable for both standalone Durable Task workers and clients.</returns>
    public static DataConverter Create() => new DurableTaskPortableDataConverter(
        DurableTaskSystemTextJsonDataConverter.CreateJsonOptions(
            ExecutionDefinitionJsonSerializer.CreateOptions()));

    sealed class DurableTaskPortableDataConverter(JsonSerializerOptions options) : DataConverter
    {
        readonly JsonSerializerOptions options = options ?? throw new ArgumentNullException(nameof(options));

        public override string? Serialize(object? value) => value is null
            ? null
            : JsonSerializer.Serialize(value, value.GetType(), options);

        public override object? Deserialize(string? data, Type targetType)
        {
            ArgumentNullException.ThrowIfNull(targetType);
            if (data is null)
            {
                return null;
            }
            if (targetType != typeof(object)
                && DurableTaskSystemTextJsonDataConverter.TryExtractTypedValuePayload(data, out var payload))
            {
                data = payload;
            }
            return JsonSerializer.Deserialize(data, targetType, options);
        }
    }
}

/// <summary>Registers the generic sequential Process orchestration and bounded host-operation activity.</summary>
public static class DurableTaskSequentialProcessWorkerBuilderExtensions
{
    /// <summary>Adds the initial canonical Process executable slice to a standalone Durable Task worker.</summary>
    /// <remarks>
    /// The application must also register one <see cref="IProcessReferenceHost"/> for bounded activities. This method
    /// registers the immutable plan catalog and the same portable data converter for the worker and a client built
    /// from the same service collection unless the application supplied one explicitly.
    /// </remarks>
    /// <param name="builder">Standalone Durable Task worker builder.</param>
    /// <param name="catalog">Immutable exact-reference plan catalog deployed to this worker.</param>
    /// <returns><paramref name="builder"/> for composition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="catalog"/> is <see langword="null"/>.
    /// </exception>
    public static IDurableTaskWorkerBuilder AddCohesiveSequentialProcesses(
        this IDurableTaskWorkerBuilder builder,
        DurableTaskSequentialProcessPlanCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(catalog);
        var converter = DurableTaskProcessDataConverter.Create();
        builder.Services.TryAddSingleton(catalog);
        builder.Services.TryAddSingleton(converter);
        builder.Configure(options => options.DataConverter = converter);
        return builder.AddTasks(tasks =>
        {
            tasks.AddOrchestrator(new DurableTaskSequentialProcessOrchestrator(catalog));
            tasks.AddActivity<DurableTaskProcessHostOperationActivity>();
        });
    }
}

/// <summary>Generic standalone Durable Task orchestration over one exact canonical Process plan.</summary>
[DurableTask(DurableTaskSequentialProcessNames.Orchestration)]
public sealed class DurableTaskSequentialProcessOrchestrator
    : TaskOrchestrator<DurableTaskSequentialProcessStart, DurableTaskSequentialProcessResult>
{
    readonly DurableTaskSequentialProcessPlanCatalog catalog;

    /// <summary>Creates an orchestrator over an immutable exact-reference plan catalog.</summary>
    /// <param name="catalog">Worker-deployed exact Process plans.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public DurableTaskSequentialProcessOrchestrator(DurableTaskSequentialProcessPlanCatalog catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public override async Task<DurableTaskSequentialProcessResult> RunAsync(
        TaskOrchestrationContext context,
        DurableTaskSequentialProcessStart input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var physical = catalog.GetExact(input.Receipt.Request.Definition);
        var result = await DurableTaskSequentialProcessInterpreter.RunAsync(
            physical.CanonicalPlan,
            input,
            operation => context.CallActivityAsync<ProcessOperationResult>(
                DurableTaskSequentialProcessNames.HostOperationActivity,
                operation),
            () => context.WaitForExternalEvent<ProcessActivationInput>(
                DurableTaskSequentialProcessNames.InteractionEvent),
            () => context.CreateTimer(TimeSpan.Zero, CancellationToken.None),
            () => ToUtc(context.CurrentUtcDateTime),
            context.SetCustomStatus).ConfigureAwait(true);

        if (result.Disposition == ProcessActivationDisposition.Failed)
        {
            var detail = result.Diagnostics.IsEmpty
                ? "Canonical Process execution reached a failed terminal."
                : string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message));
            throw new DurableTaskProcessFailedException(detail);
        }
        return result;
    }

    static DateTimeOffset ToUtc(DateTime value) => new(
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>Activity boundary for one exact canonical Transition or Relation/Query host operation.</summary>
[DurableTask(DurableTaskSequentialProcessNames.HostOperationActivity)]
public sealed class DurableTaskProcessHostOperationActivity
    : TaskActivity<DurableTaskProcessHostOperation, ProcessOperationResult>
{
    readonly IProcessReferenceHost host;

    /// <summary>Creates an activity over the application's canonical Process host.</summary>
    /// <param name="host">Host that resolves exact Transition and Relation/Query operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    public DurableTaskProcessHostOperationActivity(IProcessReferenceHost host) =>
        this.host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc />
    public override Task<ProcessOperationResult> RunAsync(
        TaskActivityContext context,
        DurableTaskProcessHostOperation input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var result = input.Kind switch
        {
            DurableTaskProcessHostOperationKind.Transition => host.InvokeTransition(input.Transition!),
            DurableTaskProcessHostOperationKind.RelationQuery => host.EvaluateRelation(input.RelationQuery!),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Unsupported host operation kind.")
        };
        return Task.FromResult(result
            ?? throw new InvalidOperationException("The Process host returned null operation evidence."));
    }
}

/// <summary>Physical orchestration failure corresponding to a canonical authored Process failure.</summary>
public sealed class DurableTaskProcessFailedException : Exception
{
    /// <summary>Creates a failure with canonical diagnostic detail.</summary>
    /// <param name="message">Non-empty canonical failure detail.</param>
    public DurableTaskProcessFailedException(string message) : base(message)
    {
    }
}

/// <summary>Idempotent client operations for the generic sequential Process orchestration.</summary>
public static class DurableTaskSequentialProcessClientExtensions
{
    /// <summary>Schedules one exact Process start or reuses an identical existing physical instance.</summary>
    /// <param name="client">Standalone Durable Task client.</param>
    /// <param name="start">Exact canonical Process start evidence.</param>
    /// <param name="cancellationToken">Cancels transport admission only.</param>
    /// <returns>The stable physical instance identity and whether existing equal admission was replayed.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/> or <paramref name="start"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The stable physical identity already exists with different canonical start evidence.
    /// </exception>
    public static async Task<DurableTaskProcessScheduleResult> ScheduleCohesiveProcessAsync(
        this DurableTaskClient client,
        DurableTaskSequentialProcessStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        var instanceId = DurableTaskSequentialProcessIdentities.OrchestrationInstance(start);
        var options = new StartOrchestrationOptions(instanceId)
            .WithDedupeStatuses([.. StartOrchestrationOptionsExtensions.ValidDedupeStatuses]);
        try
        {
            _ = await client.ScheduleNewOrchestrationInstanceAsync(
                DurableTaskSequentialProcessNames.Orchestration,
                start,
                options,
                cancellationToken).ConfigureAwait(false);
            return new(instanceId, Replayed: false);
        }
        catch (OrchestrationAlreadyExistsException)
        {
            var existing = await client.GetInstanceAsync(
                instanceId,
                getInputsAndOutputs: true,
                cancellationToken).ConfigureAwait(false);
            var retained = existing?.ReadInputAs<DurableTaskSequentialProcessStart>();
            var converter = DurableTaskProcessDataConverter.Create();
            if (retained is null
                || !string.Equals(converter.Serialize(retained), converter.Serialize(start), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The stable Durable Task Process instance identity is already bound to different start evidence.");
            }
            return new(instanceId, Replayed: true);
        }
    }

    /// <summary>Raises one canonical interaction to the exact physical instance selected by a Process start.</summary>
    /// <param name="client">Standalone Durable Task client.</param>
    /// <param name="start">Original canonical start used to derive the physical instance identity.</param>
    /// <param name="input">Canonical token-addressed interaction evidence.</param>
    /// <param name="cancellationToken">Cancels transport delivery only.</param>
    /// <returns>A task that completes when the provider admits the external event.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/>, <paramref name="start"/>, or <paramref name="input"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static Task RaiseCohesiveProcessInteractionAsync(
        this DurableTaskClient client,
        DurableTaskSequentialProcessStart start,
        ProcessActivationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(input);
        return client.RaiseEventAsync(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            DurableTaskSequentialProcessNames.InteractionEvent,
            input,
            cancellationToken);
    }
}

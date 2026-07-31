using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Provider-neutral callback boundary retained behind the Cosmos managed-source adapter.</summary>
internal sealed record CosmosManagedMaterializationProviderBatch(
    string FeedRangeJson,
    string ContinuationToken,
    ImmutableArray<CosmosObservationContainerDocument> Documents,
    Func<Task> CheckpointAsync);

/// <summary>Exact provider range and continuation retained by one authenticated managed source position.</summary>
internal sealed record CosmosManagedMaterializationProviderBoundary(
    string FeedRangeJson,
    string ContinuationToken);

/// <summary>Provider-neutral aggregate lag sample retained behind the Cosmos managed-source adapter.</summary>
internal sealed record CosmosManagedMaterializationProviderLag(
    long? EstimatedPendingProviderWork,
    string EvidenceReference);

/// <summary>Narrow managed processor and batch seam used by production Cosmos I/O and deterministic tests.</summary>
internal interface ICosmosManagedMaterializationChangeFeedProcessor
{
    /// <summary>Runs managed callbacks until completion, failure, or cancellation.</summary>
    /// <param name="handler">Handler for one provider callback and its manual checkpoint operation.</param>
    /// <param name="cancellationToken">Cancellation controlling processor lifetime.</param>
    /// <returns>A task representing processor lifetime.</returns>
    Task RunAsync(
        Func<CosmosManagedMaterializationProviderBatch, CancellationToken, Task> handler,
        CancellationToken cancellationToken);

    /// <summary>Reads source-wide aggregate lag estimates without exposing SDK lease state.</summary>
    /// <param name="cancellationToken">Cancellation observed throughout estimator paging.</param>
    /// <returns>Zero or more aggregate provider-work estimates.</returns>
    IAsyncEnumerable<CosmosManagedMaterializationProviderLag> ObserveLagAsync(
        CancellationToken cancellationToken);
}

/// <summary>Creates a managed Cosmos processor for one exact provider lease namespace.</summary>
/// <param name="effectiveProcessorName">
/// Provider deployment name derived from the source binding and managed materialization request.
/// </param>
/// <returns>A processor and lag estimator bound to <paramref name="effectiveProcessorName"/>.</returns>
internal delegate ICosmosManagedMaterializationChangeFeedProcessor
    CosmosManagedMaterializationChangeFeedProcessorFactory(string effectiveProcessorName);

/// <summary>Cosmos SDK latest-version change processor with manual provider checkpointing.</summary>
internal sealed class CosmosManagedMaterializationChangeFeedProcessor(
    Container monitoredContainer,
    Container leaseContainer,
    CosmosManagedMaterializationChangeSourcePolicy policy,
    string effectiveProcessorName)
    : ICosmosManagedMaterializationChangeFeedProcessor
{
    const string LagEvidencePrefix = "cohesive.adapters.cosmos/managed-change/lag/v1";

    readonly Container monitoredContainer = Guard.RequireNotNull(monitoredContainer);
    readonly Container leaseContainer = Guard.RequireNotNull(leaseContainer);
    readonly CosmosManagedMaterializationChangeSourcePolicy policy = Guard.RequireNotNull(policy);
    readonly string effectiveProcessorName = Guard.RequireNotNullOrWhiteSpace(effectiveProcessorName);

    /// <inheritdoc />
    public async Task RunAsync(
        Func<CosmosManagedMaterializationProviderBatch, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<Exception> callbackFailure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = monitoredContainer.GetChangeFeedProcessorBuilderWithManualCheckpoint<CosmosObservationContainerDocument>(
                processorName: effectiveProcessorName,
                onChangesDelegate: async (
                    ChangeFeedProcessorContext context,
                    IReadOnlyCollection<CosmosObservationContainerDocument> changes,
                    Func<Task> checkpointAsync,
                    CancellationToken callbackCancellationToken) =>
                {
                    try
                    {
                        ArgumentNullException.ThrowIfNull(context);
                        ArgumentNullException.ThrowIfNull(changes);
                        ArgumentNullException.ThrowIfNull(checkpointAsync);
                        var feedRangeJson = Guard.RequireNotNullOrWhiteSpace(context.FeedRange.ToJsonString());
                        var continuationToken = Guard.RequireNotNullOrWhiteSpace(context.Headers.ContinuationToken);
                        var documents = ImmutableArray.CreateBuilder<CosmosObservationContainerDocument>(changes.Count);
                        foreach (var document in changes)
                        {
                            documents.Add(Guard.RequireNotNull(document));
                        }

                        await handler(
                            new(
                                FeedRangeJson: feedRangeJson,
                                ContinuationToken: continuationToken,
                                Documents: documents.MoveToImmutable(),
                                CheckpointAsync: checkpointAsync),
                            callbackCancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (callbackCancellationToken.IsCancellationRequested
                            && !cancellationToken.IsCancellationRequested)
                    {
                        // Lease loss cancels only this callback. The SDK may transfer the lease and continue the
                        // deployment; no provider checkpoint has occurred and this is not a processor-wide failure.
                        throw;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        callbackFailure.TrySetResult(exception);
                        throw;
                    }
                })
            .WithInstanceName(instanceName: policy.InstanceName)
            .WithLeaseContainer(leaseContainer: leaseContainer)
            .WithPollInterval(pollInterval: policy.PollInterval)
            .WithMaxItems(maxItemCount: policy.MaximumProviderPageItems);

        if (GetInitialStartTimeUtc(policy: policy) is { } initialStartTimeUtc)
        {
            builder = builder.WithStartTime(startTime: initialStartTimeUtc);
        }

        var processor = builder.Build();
        await processor.StartAsync().ConfigureAwait(false);
        try
        {
            var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(callbackFailure.Task, cancellation).ConfigureAwait(false);
            if (completed == callbackFailure.Task)
            {
                ExceptionDispatchInfo.Capture(await callbackFailure.Task.ConfigureAwait(false)).Throw();
            }

            await cancellation.ConfigureAwait(false);
        }
        finally
        {
            await processor.StopAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CosmosManagedMaterializationProviderLag> ObserveLagAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var estimator = monitoredContainer.GetChangeFeedEstimator(
            processorName: effectiveProcessorName,
            leaseContainer: leaseContainer);
        using var iterator = estimator.GetCurrentStateIterator(new ChangeFeedEstimatorRequestOptions
        {
            MaxItemCount = policy.MaximumLagStateItems
        });

        long total = 0;
        var stateCount = 0;
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var state in response)
            {
                if (state.EstimatedLag < 0)
                {
                    yield return new(
                        EstimatedPendingProviderWork: null,
                        EvidenceReference: string.Concat(LagEvidencePrefix, "/unavailable"));
                    yield break;
                }

                total = SaturatingAdd(left: total, right: state.EstimatedLag);
                stateCount++;
            }
        }

        yield return stateCount == 0
            ? new(
                EstimatedPendingProviderWork: null,
                EvidenceReference: string.Concat(LagEvidencePrefix, "/unavailable/no-states"))
            : new(
                EstimatedPendingProviderWork: total,
                EvidenceReference: string.Concat(
                    LagEvidencePrefix,
                    "/aggregate/states/",
                    stateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    static long SaturatingAdd(long left, long right) => left > long.MaxValue - right
        ? long.MaxValue
        : left + right;

    /// <summary>Projects initial-position policy into the public SDK start-time configuration.</summary>
    internal static DateTime? GetInitialStartTimeUtc(
        CosmosManagedMaterializationChangeSourcePolicy policy) =>
        Guard.RequireNotNull(policy).InitialPosition switch
        {
            CosmosManagedMaterializationInitialPosition.Beginning =>
                DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            CosmosManagedMaterializationInitialPosition.Current => null,
            CosmosManagedMaterializationInitialPosition.AtTime => policy.InitialTimeUtc!.Value.UtcDateTime,
            _ => throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.InitialPosition,
                "Unsupported managed Cosmos initial position.")
        };
}

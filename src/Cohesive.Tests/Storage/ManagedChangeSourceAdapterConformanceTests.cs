using System.Collections.Immutable;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class ManagedChangeSourceAdapterConformanceTests
{
    static readonly ImmutableArray<ManagedChangeSourceConformanceCase> Cases =
    [
        Cosmos.CosmosManagedMaterializationChangeSourceTests.CreateConformanceCase()
    ];

    [Fact]
    public async Task CallbackFailure_DoesNotSettleAnUncommittedPosition()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveCallbackFailureAsync();

            Assert.True(observed.ExpectedFailureObserved, item.Adapter);
            Assert.Equal(1, observed.HandlerInvocations);
            Assert.Equal(0, observed.ProviderSettlementAttempts);
            Assert.Equal(0, observed.ProviderSettlements);
            Assert.Equal(0, observed.SettlementObservations);
        }
    }

    [Fact]
    public async Task DurableCheckpointFailure_DoesNotSettleAnUnprovenPosition()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveDurableCheckpointFailureAsync();

            Assert.True(observed.ExpectedFailureObserved, item.Adapter);
            Assert.Equal(1, observed.HandlerInvocations);
            Assert.Equal(0, observed.ProviderSettlementAttempts);
            Assert.Equal(0, observed.ProviderSettlements);
            Assert.Equal(0, observed.SettlementObservations);
        }
    }

    [Fact]
    public async Task CrashBeforeProviderSettlement_ReplaysStableDurableProgressThenSettles()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveCrashBeforeSettlementAsync();

            Assert.True(observed.ExpectedFailureObserved, item.Adapter);
            Assert.Equal(observed.InitialDelivery, observed.ReplayedDelivery);
            Assert.Equal(observed.InitialChange, observed.ReplayedChange);
            Assert.Equal<MaterializationProgressMutationDisposition>(
                [
                    MaterializationProgressMutationDisposition.Applied,
                    MaterializationProgressMutationDisposition.Replayed
                ],
                observed.ApplicationDispositions);
            Assert.Equal(1, observed.InitialProviderSettlementAttempts);
            Assert.Equal(0, observed.InitialProviderSettlements);
            Assert.Equal(0, observed.InitialSettlementObservations);
            Assert.Equal(1, observed.ReplayProviderSettlements);
            Assert.Equal(1, observed.ReplaySettlementObservations);
        }
    }

    [Fact]
    public async Task CrashAfterProviderSettlement_DoesNotRedeliverTheSettledCallback()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveCrashAfterSettlementAsync();

            Assert.True(observed.RunCompleted, item.Adapter);
            Assert.Equal(1, observed.HandlerInvocations);
            Assert.Equal(1, observed.ProviderSettlements);
            Assert.Equal(1, observed.PostSettlementObservationAttempts);
        }
    }

    [Fact]
    public async Task DuplicateReplay_PreservesLogicalIdentityAndSettlesEachOccurrence()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveDuplicateReplayAsync();

            Assert.Equal<MaterializationProgressMutationDisposition>(
                [
                    MaterializationProgressMutationDisposition.Applied,
                    MaterializationProgressMutationDisposition.Replayed
                ],
                observed.ApplicationDispositions);
            Assert.Equal(2, observed.Deliveries.Length);
            Assert.All(observed.Deliveries, delivery => Assert.Equal(observed.Deliveries[0], delivery));
            Assert.Equal(2, observed.Changes.Length);
            Assert.All(observed.Changes, change => Assert.Equal(observed.Changes[0], change));
            Assert.Equal(2, observed.ProviderSettlements);
            Assert.Equal(2, observed.SettlementIds.Length);
            Assert.Equal(2, observed.SettlementIds.Distinct().Count());
        }
    }

    [Fact]
    public async Task CancellationAfterDurableCheckpoint_DoesNotSettleTheProviderPosition()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveCancellationAsync();

            Assert.True(observed.CancellationObserved, item.Adapter);
            Assert.Equal(1, observed.HandlerInvocations);
            Assert.Equal(0, observed.ProviderSettlementAttempts);
            Assert.Equal(0, observed.ProviderSettlements);
            Assert.Equal(0, observed.SettlementObservations);
        }
    }

    [Fact]
    public async Task LeaseTransfer_PreservesLogicalIdentityAndContinuesWithTheNewOwner()
    {
        foreach (var item in Cases)
        {
            var observed = await item.ObserveLeaseTransferAsync();

            Assert.Equal(observed.InitialDelivery, observed.TransferredDelivery);
            Assert.Equal(observed.InitialChange, observed.TransferredChange);
            Assert.Equal(0, observed.InitialProviderSettlements);
            Assert.Equal(0, observed.InitialSettlementObservations);
            Assert.Equal(1, observed.TransferredHandlerInvocations);
            Assert.Equal(1, observed.TransferredProviderSettlements);
            Assert.Equal(1, observed.TransferredSettlementObservations);
        }
    }
}

internal sealed record ManagedChangeSourceConformanceCase(
    string Adapter,
    Func<Task<ManagedChangeSourceRejectedObservation>> ObserveCallbackFailureAsync,
    Func<Task<ManagedChangeSourceRejectedObservation>> ObserveDurableCheckpointFailureAsync,
    Func<Task<ManagedChangeSourceCrashBeforeSettlementObservation>> ObserveCrashBeforeSettlementAsync,
    Func<Task<ManagedChangeSourceCrashAfterSettlementObservation>> ObserveCrashAfterSettlementAsync,
    Func<Task<ManagedChangeSourceDuplicateReplayObservation>> ObserveDuplicateReplayAsync,
    Func<Task<ManagedChangeSourceCancellationObservation>> ObserveCancellationAsync,
    Func<Task<ManagedChangeSourceLeaseTransferObservation>> ObserveLeaseTransferAsync);

internal sealed record ManagedChangeSourceRejectedObservation(
    bool ExpectedFailureObserved,
    int HandlerInvocations,
    int ProviderSettlementAttempts,
    int ProviderSettlements,
    int SettlementObservations);

internal sealed record ManagedChangeSourceCrashBeforeSettlementObservation(
    bool ExpectedFailureObserved,
    MaterializationDeliveryId InitialDelivery,
    MaterializationDeliveryId ReplayedDelivery,
    MaterializationChangeId InitialChange,
    MaterializationChangeId ReplayedChange,
    ImmutableArray<MaterializationProgressMutationDisposition> ApplicationDispositions,
    int InitialProviderSettlementAttempts,
    int InitialProviderSettlements,
    int InitialSettlementObservations,
    int ReplayProviderSettlements,
    int ReplaySettlementObservations);

internal sealed record ManagedChangeSourceCrashAfterSettlementObservation(
    bool RunCompleted,
    int HandlerInvocations,
    int ProviderSettlements,
    int PostSettlementObservationAttempts);

internal sealed record ManagedChangeSourceDuplicateReplayObservation(
    ImmutableArray<MaterializationProgressMutationDisposition> ApplicationDispositions,
    ImmutableArray<MaterializationDeliveryId> Deliveries,
    ImmutableArray<MaterializationChangeId> Changes,
    int ProviderSettlements,
    ImmutableArray<MaterializationSettlementId> SettlementIds);

internal sealed record ManagedChangeSourceCancellationObservation(
    bool CancellationObserved,
    int HandlerInvocations,
    int ProviderSettlementAttempts,
    int ProviderSettlements,
    int SettlementObservations);

internal sealed record ManagedChangeSourceLeaseTransferObservation(
    MaterializationDeliveryId InitialDelivery,
    MaterializationDeliveryId TransferredDelivery,
    MaterializationChangeId InitialChange,
    MaterializationChangeId TransferredChange,
    int InitialProviderSettlements,
    int InitialSettlementObservations,
    int TransferredHandlerInvocations,
    int TransferredProviderSettlements,
    int TransferredSettlementObservations);

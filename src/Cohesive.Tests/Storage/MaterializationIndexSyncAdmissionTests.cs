using Cohesive.Control;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationIndexSyncAdmissionTests
{
    [Fact]
    public async Task QueuedRealtimePrecedesQueuedRebuildWithoutPreemptingInFlightWork()
    {
        MaterializationIndexSyncAdmissionGate gate = new();
        MaterializationIndexSyncAdmissionResource resource = new(ControlStageKind.Target, "target-a");
        MaterializationIndexSyncAdmissionLimits limits = new(
            totalMaximum: 1,
            realtimeMaximum: 1,
            rebuildMaximum: 1,
            realtimeReservation: 0);
        var firstRebuild = await gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            limits);
        var queuedRebuild = gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            limits).AsTask();
        var queuedRealtime = gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Realtime,
            limits).AsTask();

        var saturated = gate.GetSnapshot(resource)!.Value;
        Assert.Equal(1, saturated.InFlightRebuild);
        Assert.Equal(1, saturated.QueuedRebuild);
        Assert.Equal(1, saturated.QueuedRealtime);
        Assert.False(queuedRebuild.IsCompleted);
        Assert.False(queuedRealtime.IsCompleted);

        firstRebuild.Dispose();
        var realtime = await queuedRealtime.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queuedRebuild.IsCompleted);
        Assert.Equal(1, gate.GetSnapshot(resource)!.Value.InFlightRealtime);

        realtime.Dispose();
        var rebuild = await queuedRebuild.WaitAsync(TimeSpan.FromSeconds(5));
        rebuild.Dispose();
        Assert.Equal(0, gate.GetSnapshot(resource)!.Value.InFlightTotal);
    }

    [Fact]
    public async Task LowerLimitDrainsExistingWorkAndDoesNotPreemptIt()
    {
        MaterializationIndexSyncAdmissionGate gate = new();
        MaterializationIndexSyncAdmissionResource resource = new(ControlStageKind.Target, "target-a");
        MaterializationIndexSyncAdmissionLimits initial = new(2, 2, 2, 0);
        var first = await gate.AcquireAsync(resource, MaterializationIndexSyncWorkloadKind.Rebuild, initial);
        var second = await gate.AcquireAsync(resource, MaterializationIndexSyncWorkloadKind.Rebuild, initial);

        MaterializationIndexSyncAdmissionLimits lowered = new(1, 1, 1, 0);
        gate.ApplyLimits(resource, lowered);
        var realtimeTask = gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Realtime,
            lowered).AsTask();

        Assert.Equal(2, gate.GetSnapshot(resource)!.Value.InFlightRebuild);
        first.Dispose();
        Assert.False(realtimeTask.IsCompleted);
        Assert.Equal(1, gate.GetSnapshot(resource)!.Value.InFlightRebuild);

        second.Dispose();
        var realtime = await realtimeTask.WaitAsync(TimeSpan.FromSeconds(5));
        realtime.Dispose();
        Assert.Equal(0, gate.GetSnapshot(resource)!.Value.InFlightTotal);
    }

    [Fact]
    public async Task CancellationBeforeAndWhileQueuedDoesNotLeakAQueueNodeOrPermit()
    {
        MaterializationIndexSyncAdmissionGate gate = new();
        MaterializationIndexSyncAdmissionResource resource = new(ControlStageKind.Target, "target-a");
        MaterializationIndexSyncAdmissionLimits limits = new(1, 1, 1, 0);
        using CancellationTokenSource alreadyCanceled = new();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Realtime,
            limits,
            alreadyCanceled.Token));

        var held = await gate.AcquireAsync(resource, MaterializationIndexSyncWorkloadKind.Rebuild, limits);
        using CancellationTokenSource queuedCancellation = new();
        var canceledWaiter = gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Realtime,
            limits,
            queuedCancellation.Token).AsTask();
        Assert.Equal(1, gate.GetSnapshot(resource)!.Value.QueuedRealtime);

        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWaiter);
        Assert.Equal(0, gate.GetSnapshot(resource)!.Value.QueuedTotal);

        held.Dispose();
        var final = await gate.AcquireAsync(resource, MaterializationIndexSyncWorkloadKind.Realtime, limits);
        final.Dispose();
        Assert.Equal(0, gate.GetSnapshot(resource)!.Value.InFlightTotal);
    }


    [Fact]
    public async Task RebuildCannotConsumeExplicitRealtimeReservationEvenWhenRealtimeIsIdle()
    {
        MaterializationIndexSyncAdmissionGate gate = new();
        MaterializationIndexSyncAdmissionResource resource = new(ControlStageKind.Target, "target-a");
        MaterializationIndexSyncAdmissionLimits limits = new(
            totalMaximum: 3,
            realtimeMaximum: 3,
            rebuildMaximum: 1,
            realtimeReservation: 2);

        var admittedRebuild = await gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            limits);
        var queuedRebuild = gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            limits).AsTask();

        Assert.False(queuedRebuild.IsCompleted);
        var snapshot = gate.GetSnapshot(resource)!.Value;
        Assert.Equal(2, snapshot.Limits.RealtimeReservation);
        Assert.Equal(1, snapshot.InFlightRebuild);

        var realtime = await gate.AcquireAsync(
            resource,
            MaterializationIndexSyncWorkloadKind.Realtime,
            limits);
        Assert.Equal(2, gate.GetSnapshot(resource)!.Value.InFlightTotal);

        realtime.Dispose();
        admittedRebuild.Dispose();
        var nextRebuild = await queuedRebuild.WaitAsync(TimeSpan.FromSeconds(5));
        nextRebuild.Dispose();
    }

    [Fact]
    public async Task EqualRawSourceAndTargetIdentitiesRemainIndependentStageResources()
    {
        MaterializationIndexSyncAdmissionGate gate = new();
        MaterializationIndexSyncAdmissionLimits limits = new(1, 1, 1, 0);
        MaterializationIndexSyncAdmissionResource source = new(ControlStageKind.Source, "shared-raw-id");
        MaterializationIndexSyncAdmissionResource target = new(ControlStageKind.Target, "shared-raw-id");

        var sourceLease = await gate.AcquireAsync(
            source,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            limits);
        var targetLease = await gate.AcquireAsync(
            target,
            MaterializationIndexSyncWorkloadKind.Rebuild,
            limits);

        Assert.Equal(1, gate.GetSnapshot(source)!.Value.InFlightRebuild);
        Assert.Equal(1, gate.GetSnapshot(target)!.Value.InFlightRebuild);
        Assert.Equal(2, gate.GetSnapshots().Length);

        sourceLease.Dispose();
        targetLease.Dispose();
    }
}

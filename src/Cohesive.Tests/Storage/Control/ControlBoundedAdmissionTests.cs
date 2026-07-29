using Cohesive.Control;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlBoundedAdmissionTests
{
    [Fact]
    public void ConcurrencyDecrease_DrainsExistingWorkWithoutPreemption()
    {
        var point = ControlTestFixture.Point((ControlActuatorKind.Concurrency, 3));

        Assert.Equal(
            ControlAdmissionDisposition.Deferred,
            ControlBoundedAdmission.CheckConcurrency(point, inFlight: 5).Disposition);
        Assert.Equal(
            ControlAdmissionDisposition.Deferred,
            ControlBoundedAdmission.CheckConcurrency(point, inFlight: 3).Disposition);

        var afterDrain = ControlBoundedAdmission.CheckConcurrency(point, inFlight: 2);
        Assert.Equal(ControlAdmissionDisposition.Admitted, afterDrain.Disposition);
        Assert.Equal(ControlActuatorKind.Concurrency, afterDrain.ConstrainedBy);
    }

    [Fact]
    public void BatchItem_AtBothLimits_IsAdmittedConjunctively()
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BatchItems, 2),
            (ControlActuatorKind.BatchBytes, 10));

        var decision = ControlBoundedAdmission.CheckBatchItem(
            point,
            new(itemCount: 1, byteCount: 6),
            itemByteCount: 4,
            out var resultingBatch);

        Assert.Equal(ControlAdmissionDisposition.Admitted, decision.Disposition);
        Assert.Equal(new ControlWorkloadUsage(itemCount: 2, byteCount: 10), resultingBatch);
    }

    [Theory]
    [InlineData(2, 6, 4, ControlActuatorKind.BatchItems)]
    [InlineData(1, 9, 2, ControlActuatorKind.BatchBytes)]
    public void BatchItem_CrossingEitherLimit_EndsBatchAndRetainsCandidate(
        long currentItems,
        long currentBytes,
        long candidateBytes,
        ControlActuatorKind expectedConstraint)
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BatchItems, 2),
            (ControlActuatorKind.BatchBytes, 10));
        var current = new ControlWorkloadUsage(currentItems, currentBytes);

        var decision = ControlBoundedAdmission.CheckBatchItem(
            point,
            current,
            candidateBytes,
            out var resultingBatch);

        Assert.Equal(ControlAdmissionDisposition.Boundary, decision.Disposition);
        Assert.False(decision.AdmitsCandidate);
        Assert.Equal(expectedConstraint, decision.ConstrainedBy);
        Assert.Equal(current, resultingBatch);
    }

    [Fact]
    public void BatchItem_FirstOversizedItem_IsUnfulfillableAndNeverAdmitted()
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BatchItems, 100),
            (ControlActuatorKind.BatchBytes, 1_000));

        var decision = ControlBoundedAdmission.CheckBatchItem(
            point,
            ControlWorkloadUsage.Zero,
            itemByteCount: 1_001,
            out var resultingBatch);

        Assert.Equal(ControlAdmissionDisposition.Unfulfillable, decision.Disposition);
        Assert.False(decision.AdmitsCandidate);
        Assert.Equal(ControlActuatorKind.BatchBytes, decision.ConstrainedBy);
        Assert.Equal(ControlWorkloadUsage.Zero, resultingBatch);
    }

    [Theory]
    [InlineData(9, 50, 2, 1, ControlActuatorKind.BufferedItems)]
    [InlineData(5, 95, 1, 6, ControlActuatorKind.BufferedBytes)]
    public void BufferPressure_DefersWithoutConsumingCallerOwnedWork(
        long bufferedItems,
        long bufferedBytes,
        long incomingItems,
        long incomingBytes,
        ControlActuatorKind expectedConstraint)
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BufferedItems, 10),
            (ControlActuatorKind.BufferedBytes, 100));
        var buffered = new ControlWorkloadUsage(bufferedItems, bufferedBytes);
        var incoming = new ControlWorkloadUsage(incomingItems, incomingBytes);

        var first = ControlBoundedAdmission.CheckBuffer(point, buffered, incoming);
        var retry = ControlBoundedAdmission.CheckBuffer(point, buffered, incoming);

        Assert.Equal(ControlAdmissionDisposition.Deferred, first.Disposition);
        Assert.Equal(expectedConstraint, first.ConstrainedBy);
        Assert.Equal(first, retry);
        Assert.False(first.AdmitsCandidate);
    }

    [Fact]
    public void BufferPressure_AdmitsOnlyWhenBothFiniteLimitsHaveCapacity()
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BufferedItems, 10),
            (ControlActuatorKind.BufferedBytes, 100));

        var decision = ControlBoundedAdmission.CheckBuffer(
            point,
            new(itemCount: 8, byteCount: 80),
            new(itemCount: 2, byteCount: 20));

        Assert.Equal(ControlAdmissionDisposition.Admitted, decision.Disposition);
    }

    [Theory]
    [InlineData(11, 1, ControlActuatorKind.BufferedItems)]
    [InlineData(1, 101, ControlActuatorKind.BufferedBytes)]
    public void BufferCandidateThatCannotFitWhenEmpty_IsUnfulfillable(
        long incomingItems,
        long incomingBytes,
        ControlActuatorKind expectedConstraint)
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BufferedItems, 10),
            (ControlActuatorKind.BufferedBytes, 100));

        var decision = ControlBoundedAdmission.CheckBuffer(
            point,
            ControlWorkloadUsage.Zero,
            new(incomingItems, incomingBytes));

        Assert.Equal(ControlAdmissionDisposition.Unfulfillable, decision.Disposition);
        Assert.Equal(expectedConstraint, decision.ConstrainedBy);
    }

    [Theory]
    [InlineData(4, 90, 1, 10, ControlAdmissionDisposition.Admitted, ControlActuatorKind.ItemRate)]
    [InlineData(4, 90, 2, 10, ControlAdmissionDisposition.Deferred, ControlActuatorKind.ItemRate)]
    [InlineData(4, 90, 1, 11, ControlAdmissionDisposition.Deferred, ControlActuatorKind.ByteRate)]
    public void RateAdmission_UsesCallerSuppliedItemAndByteWindowUsage(
        long usedItems,
        long usedBytes,
        long incomingItems,
        long incomingBytes,
        ControlAdmissionDisposition expectedDisposition,
        ControlActuatorKind expectedConstraint)
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.ItemRate, 5),
            (ControlActuatorKind.ByteRate, 100));

        var decision = ControlBoundedAdmission.CheckRate(
            point,
            new(usedItems, usedBytes),
            new(incomingItems, incomingBytes));

        Assert.Equal(expectedDisposition, decision.Disposition);
        Assert.Equal(expectedConstraint, decision.ConstrainedBy);
    }

    [Theory]
    [InlineData(6, 1, ControlActuatorKind.ItemRate)]
    [InlineData(1, 101, ControlActuatorKind.ByteRate)]
    public void RateCandidateThatCannotFitInAnyWindow_IsUnfulfillable(
        long incomingItems,
        long incomingBytes,
        ControlActuatorKind expectedConstraint)
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.ItemRate, 5),
            (ControlActuatorKind.ByteRate, 100));

        var decision = ControlBoundedAdmission.CheckRate(
            point,
            ControlWorkloadUsage.Zero,
            new(incomingItems, incomingBytes));

        Assert.Equal(ControlAdmissionDisposition.Unfulfillable, decision.Disposition);
        Assert.Equal(expectedConstraint, decision.ConstrainedBy);
    }

    [Fact]
    public void CapacityChecks_DoNotOverflowWhenUsageAlreadyExceedsADecreasedTarget()
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BufferedItems, 1),
            (ControlActuatorKind.BufferedBytes, 1));

        var decision = ControlBoundedAdmission.CheckBuffer(
            point,
            new(ControlQuantity.MaximumPortableValue, ControlQuantity.MaximumPortableValue),
            new(itemCount: 1, byteCount: 1));

        Assert.Equal(ControlAdmissionDisposition.Deferred, decision.Disposition);
    }
}

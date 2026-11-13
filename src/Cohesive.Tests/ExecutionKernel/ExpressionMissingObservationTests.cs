using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Tests.ExecutionKernel;

public class ExpressionMissingObservationTests
{
    [Fact]
    public void MissingObservationPolicyPreservesExecutionDefaultsAndConcreteProjectionWithoutAllocation()
    {
        var value = PortableExpressionValue.Concrete(ObservationValue.FromObject(
            new Dictionary<string, ObservationValue> { ["present"] = ObservationValue.FromInt64(42) }));
        var present = FieldPath.Parse("present");
        var missing = FieldPath.Parse("missing");
        Assert.Equal(PortableValueState.Absent, value.Project(missing).State);
        Assert.Equal(PortableValueState.Missing, value.Project(missing, missingFieldState: PortableValueState.Missing).State);
        Assert.Equal(value.Project(present), value.Project(present, missingFieldState: PortableValueState.Missing));
        for (var i = 0; i < 100; i++) _ = value.Project(present, missingFieldState: PortableValueState.Missing);
        var before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (var i = 0; i < 1000; i++) sum += value.Project(present, missingFieldState: PortableValueState.Missing).Observation.Int64;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(42000, sum);
        Assert.Equal(0, allocated);
    }
}

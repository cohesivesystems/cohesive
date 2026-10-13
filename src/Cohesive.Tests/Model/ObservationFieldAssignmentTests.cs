using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class ObservationFieldAssignmentTests
{
    static FieldPath Path(params string[] names) => new([.. names.Select(FieldPathSegment.ForField)]);
    [Fact]
    public void BatchMatchesSequentialOverwriteRemovalAndNestedNullSemantics()
    {
        KeyValuePair<FieldPath, ObservationValue>[] fields = [
            new(Path("z"), ObservationValue.FromInt64(1)), new(Path("a"), ObservationValue.Null),
            new(Path("a", "child"), ObservationValue.FromString("value")),
            new(Path("a", "other"), ObservationValue.FromInt64(3)),
            new(Path("a", "child"), ObservationValue.Undefined),
            new(Path("z"), ObservationValue.FromInt64(2))];
        var expected = ObservationValue.EmptyObject;
        foreach (var field in fields) expected = expected.WithField(field.Key, field.Value);
        var actual = ObservationValue.FromFieldAssignments(fields);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetRawText(), actual.GetRawText());
        Assert.Equal(new[] { "a", "z" }, actual.Fields!.Keys);
        Array.Clear(fields);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ScalarTraversalAndEmptyPathsKeepTheirFailures()
    {
        KeyValuePair<FieldPath, ObservationValue>[] fields = [new(Path("a"), ObservationValue.FromInt64(1)),
            new(Path("a", "child"), ObservationValue.Null)];
        Assert.Throws<InvalidOperationException>(() => ObservationValue.FromFieldAssignments(fields));
        Assert.Throws<ArgumentException>(() => ObservationValue.FromFieldAssignments([new(default, ObservationValue.Null)]));
    }

    [Fact]
    public void WideBatchAssemblyHasLinearWarmAllocationCeiling()
    {
        var fields = Enumerable.Range(0, 128).Select(i => new KeyValuePair<FieldPath, ObservationValue>(
            Path($"field{i:D4}"), ObservationValue.FromInt64(i))).ToArray();
        for (var i = 0; i < 20; i++) ObservationValue.FromFieldAssignments(fields);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = ObservationValue.FromFieldAssignments(fields);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 40_000);
    }
}

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
    [Fact]
    public void ExistingBatchMatchesSequentialUpdatesAndSharesUntouchedSubtrees()
    {
        var source = ObservationValue.FromObject(new { Customer = new { Name = "before", Remove = 1 }, Untouched = new { Values = new[] { 1, 2 } } });
        KeyValuePair<FieldPath, ObservationValue>[] assignments = [
            new(Path("Customer", "Name"), ObservationValue.FromString("after")),
            new(Path("Customer", "Remove"), ObservationValue.Undefined),
            new(Path("Added"), ObservationValue.Null),
            new(Path("Added", "Child"), ObservationValue.FromBool(true))];
        var expected = source;
        foreach (var assignment in assignments) expected = expected.WithField(assignment.Key, assignment.Value);
        var actual = source.WithFields(assignments);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetRawText(), actual.GetRawText());
        Assert.Equal("before", source.GetProperty("Customer").GetProperty("Name").GetString());
        Assert.Same(source.GetProperty("Untouched").Fields, actual.GetProperty("Untouched").Fields);
        Assert.Same(source.Fields, source.WithFields([]).Fields);
        Assert.Equal(ObservationValue.FromInt64(42), ObservationValue.FromInt64(42).WithFields([]));
    }

    [Fact]
    public void BatchFailureLeavesTheOriginalValueUntouched()
    {
        var source = ObservationValue.FromObject(new { a = 42, b = "before" });
        var before = source.GetRawText();
        Assert.Throws<InvalidOperationException>(() => source.WithFields([
            new(Path("b"), ObservationValue.FromString("after")), new(Path("a", "child"), ObservationValue.Null)]));
        Assert.Equal(before, source.GetRawText());
        Assert.Throws<InvalidOperationException>(() => ObservationValue.FromInt64(42).WithFields([new(Path("x"), ObservationValue.Null)]));
    }

    [Fact]
    public void WideExistingBatchHasLinearWarmAllocationCeiling()
    {
        var initial = Enumerable.Range(0, 128).Select(i => new KeyValuePair<FieldPath, ObservationValue>(
            Path($"field{i:D4}"), ObservationValue.FromInt64(i))).ToArray();
        var source = ObservationValue.FromFieldAssignments(initial);
        var updates = initial.Select(field => new KeyValuePair<FieldPath, ObservationValue>(field.Key, ObservationValue.FromInt64(42))).ToArray();
        var expected = source;
        foreach (var update in updates) expected = expected.WithField(update.Key, update.Value);
        for (var i = 0; i < 8; i++) source.WithFields(updates);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = source.WithFields(updates);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected, actual);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 60_000);
    }

}

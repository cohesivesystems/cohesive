using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

internal static class FieldAssignmentAllocationBenchmark
{
    internal static int Run(string[] args)
    {
        var count = AllocationMeasurement.Iterations(args);
        AllocationMeasurement.Header();
        foreach (var fields in new[] { 16, 128, 256 })
        {
            var assignments = Enumerable.Range(0, fields).Select(i => new KeyValuePair<FieldPath, ObservationValue>(
                FieldPath.FromField($"field{i:D3}"), ObservationValue.FromInt64(i))).ToArray();
            ObservationValue Repeated()
            {
                var value = ObservationValue.EmptyObject;
                foreach (var field in assignments) value = value.WithField(field.Key, field.Value);
                return value;
            }
            if (!Repeated().Equals(ObservationValue.FromFieldAssignments(assignments))) throw new InvalidOperationException("Fields differ.");
            AllocationMeasurement.Measure($"fields-{fields}", "repeated-copy", () => Repeated(), count);
            AllocationMeasurement.Measure($"fields-{fields}", "batch", () => ObservationValue.FromFieldAssignments(assignments), count);
        }
        return 0;
    }
}

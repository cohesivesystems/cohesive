using System.Collections.Immutable;

namespace Cohesive.Processes.IR;

static class ProcessIrCollections
{
    public static ImmutableArray<ProcessCapacityDomainLimit> NormalizeCapacityDomains(
        ImmutableArray<ProcessCapacityDomainLimit> values) => NormalizeSet(values, CompareCapacityDomains);

    public static ImmutableArray<T> NormalizeSet<T>(
        ImmutableArray<T> values,
        Comparison<T> comparison)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        for (var index = 1; index < values.Length; index++)
        {
            if (comparison(values[index - 1], values[index]) <= 0)
                continue;

            var sorted = ImmutableArray.CreateBuilder<T>(values.Length);
            sorted.AddRange(values);
            sorted.Sort(comparison);
            return sorted.MoveToImmutable();
        }

        return values;
    }

    static int CompareCapacityDomains(
        ProcessCapacityDomainLimit? left,
        ProcessCapacityDomainLimit? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        return StringComparer.Ordinal.Compare(left.Identity, right.Identity);
    }
}

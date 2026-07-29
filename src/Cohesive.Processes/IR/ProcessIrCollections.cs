using System.Collections.Immutable;

namespace Cohesive.Processes.IR;

static class ProcessIrCollections
{
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
}

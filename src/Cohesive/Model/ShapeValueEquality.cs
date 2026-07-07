using System.Collections.Immutable;

namespace Cohesive.Model;

/// <summary>
/// Shared value-semantics helpers for shape model records.
/// </summary>
static class ShapeValueEquality
{
    public static bool AreAnnotationsEqual(ImmutableDictionary<AnnotationKey, AnnotationValue> left, ImmutableDictionary<AnnotationKey, AnnotationValue> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, leftValue) in left)
        {
            if (!right.TryGetValue(key, out var rightValue))
                return false;

            if (leftValue != rightValue)
                return false;
        }

        return true;
    }

    public static int GetAnnotationsHashCode(ImmutableDictionary<AnnotationKey, AnnotationValue> annotations)
    {
        if (annotations.Count == 0)
            return 0;

        unchecked
        {
            var xor = 0;
            var sum = 0;
            var product = 1;
            foreach (var (key, value) in annotations)
            {
                var entryHash = HashCode.Combine(key.GetHashCode(), value.GetHashCode());
                xor ^= entryHash;
                sum += entryHash;
                product *= (entryHash | 1);
            }

            return HashCode.Combine(annotations.Count, xor, sum, product);
        }
    }
}

using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Immutable, ordered path to a construct in a canonical execution definition.
/// </summary>
/// <remarks>
/// String formatting uses JSON Pointer escaping: <c>~</c> becomes <c>~0</c>, <c>/</c>
/// becomes <c>~1</c>, and every segment is prefixed with <c>/</c>. Segment comparison is ordinal.
/// </remarks>
public readonly record struct ExecutionSemanticPath : IEquatable<ExecutionSemanticPath>
{
    /// <summary>Creates an execution semantic path.</summary>
    /// <param name="segments">Ordered semantic path segments.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="segments"/> is default or empty, or contains a null, empty, or white-space segment.
    /// </exception>
    [JsonConstructor]
    public ExecutionSemanticPath(ImmutableArray<string> segments)
    {
        if (segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "An execution semantic path requires at least one segment.",
                nameof(segments));
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(segments[index]))
                continue;

            throw new ArgumentException(
                "Execution semantic path segments cannot be null, empty, or white space.",
                nameof(segments));
        }

        Segments = segments;
    }

    /// <summary>Ordered semantic path segments.</summary>
    public ImmutableArray<string> Segments { get; }

    /// <summary>Creates a semantic path containing one segment.</summary>
    /// <param name="segment">The first semantic path segment.</param>
    /// <returns>A semantic path whose only segment is <paramref name="segment"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="segment"/> is empty or consists only of white-space characters.
    /// </exception>
    public static ExecutionSemanticPath From(string segment) =>
        new([Guard.RequireNotNullOrWhiteSpace(segment)]);

    /// <summary>Appends one semantic segment and returns a new path.</summary>
    /// <param name="segment">Semantic segment to append.</param>
    /// <returns>A new path containing the current segments followed by <paramref name="segment"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="segment"/> is empty or consists only of white-space characters.
    /// </exception>
    /// <exception cref="InvalidOperationException">This value is a default, uninitialized path.</exception>
    public ExecutionSemanticPath Append(string segment)
    {
        if (Segments.IsDefaultOrEmpty)
            throw new InvalidOperationException("Cannot append to a default execution semantic path.");

        segment = Guard.RequireNotNullOrWhiteSpace(segment);
        return new([.. Segments, segment]);
    }

    /// <summary>Compares two semantic paths structurally using ordinal segment comparison.</summary>
    /// <param name="other">Path to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when both paths contain the same ordered segments; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool Equals(ExecutionSemanticPath other)
    {
        if (Segments.IsDefault || other.Segments.IsDefault)
            return Segments.IsDefault == other.Segments.IsDefault;

        if (Segments.Length != other.Segments.Length)
            return false;

        for (var index = 0; index < Segments.Length; index++)
        {
            if (!string.Equals(Segments[index], other.Segments[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Returns a structural hash code for the ordered path segments.</summary>
    /// <returns>A hash code derived from every path segment using ordinal string hashing.</returns>
    public override int GetHashCode()
    {
        if (Segments.IsDefault)
            return 0;

        var hash = new HashCode();
        foreach (var segment in Segments)
            hash.Add(segment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>Formats the path in its canonical JSON Pointer representation.</summary>
    /// <returns>
    /// A slash-prefixed, JSON Pointer-escaped path, or an empty string for a default uninitialized value.
    /// </returns>
    public override string ToString()
    {
        if (Segments.IsDefaultOrEmpty)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var segment in Segments)
        {
            builder.Append('/');
            foreach (var character in segment)
            {
                switch (character)
                {
                    case '~':
                        builder.Append("~0");
                        break;
                    case '/':
                        builder.Append("~1");
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }
        }

        return builder.ToString();
    }
}

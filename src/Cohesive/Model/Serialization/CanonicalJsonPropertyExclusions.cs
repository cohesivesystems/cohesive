using System.Text;

namespace Cohesive.Model.Serialization;

/// <summary>Immutable, reusable omissions for exact object-property paths in canonical JSON.</summary>
/// <remarks>
/// Paths use JSON Pointer escaping (<c>~0</c> and <c>~1</c>) and must name a property,
/// not the document root. Matching is ordinal and applies to all repeated occurrences.
/// Arrays do not inherit object-path rules. Omitted values are neither visited nor validated;
/// this is an explicit fingerprint projection, not document admission validation.
/// </remarks>
public sealed class CanonicalJsonPropertyExclusions
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal Node Root { get; }

    /// <summary>Compiles exact property paths, copying caller-owned configuration.</summary>
    /// <param name="propertyPaths">Absolute object-property paths, such as <c>/identity/contentHash</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="propertyPaths"/> is null.</exception>
    /// <exception cref="ArgumentException">A path is null, not absolute, contains an invalid escape, or has invalid Unicode.</exception>
    public CanonicalJsonPropertyExclusions(params string[] propertyPaths)
    {
        ArgumentNullException.ThrowIfNull(propertyPaths);
        var root = new Builder();
        foreach (var path in propertyPaths)
        {
            if (path is null || !path.StartsWith('/'))
                throw new ArgumentException("An exclusion must name an absolute object-property path.", nameof(propertyPaths));
            var current = root;
            foreach (var segment in path[1..].Split('/'))
            {
                for (var i = 0; i < segment.Length; i++)
                    if (segment[i] == '~' && (++i == segment.Length || segment[i] is not ('0' or '1')))
                        throw new ArgumentException("An exclusion contains an invalid JSON Pointer escape.", nameof(propertyPaths));
                var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (!current.Children.TryGetValue(name, out var next))
                    current.Children.Add(name, next = new());
                current = next;
            }
            current.Omit = true;
        }
        Root = root.Freeze();
    }

    // Tiny projection configurations use direct comparisons, without a name string or path allocation.
    internal sealed class Node(string name, bool omit, Node[] children)
    {
        readonly byte[] utf8Name = StrictUtf8.GetBytes(name);
        internal string Name => name;
        internal ReadOnlySpan<byte> Utf8Name => utf8Name;
        internal bool Omit => omit;
        internal ReadOnlySpan<Node> Children => children;
    }

    sealed class Builder
    {
        internal bool Omit;
        internal Dictionary<string, Builder> Children { get; } = new(StringComparer.Ordinal);
        internal Node Freeze(string name = "")
        {
            var children = new Node[Children.Count];
            var index = 0;
            foreach (var child in Children) children[index++] = child.Value.Freeze(child.Key);
            return new(name, Omit, children);
        }
    }
}

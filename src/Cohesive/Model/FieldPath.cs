using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>Resolves one complete CLR property chain to its canonical semantic field path.</summary>
/// <param name="rootType">CLR type at which <paramref name="members"/> is rooted.</param>
/// <param name="members">Ordered readable-property chain from the root value to the selected value.</param>
/// <returns>The canonical semantic field path represented by the complete property chain.</returns>
/// <exception cref="ArgumentNullException">
/// <paramref name="rootType"/> or <paramref name="members"/> is <see langword="null"/>.
/// </exception>
/// <exception cref="ArgumentException">The property chain is invalid for the selected metadata profile.</exception>
/// <exception cref="InvalidOperationException">The selected metadata profile cannot resolve the property chain.</exception>
/// <exception cref="NotSupportedException">The metadata profile does not support a property in the chain.</exception>
public delegate FieldPath ClrMemberPathResolver(
    Type rootType,
    IReadOnlyList<PropertyInfo> members);

/// <summary>
/// Strongly typed path to a nested field.
/// </summary>
public readonly record struct FieldPath : IEquatable<FieldPath>
{
    /// <summary>
    /// Creates a typed field path.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    [JsonConstructor]
    public FieldPath(ImmutableArray<FieldPathSegment> segments)
    {
        Segments = segments.IsDefault ? [] : segments;
        if (Segments.IsDefaultOrEmpty)
            throw new ArgumentException(message: "Field path requires at least one segment.", paramName: nameof(segments));
    }

    /// <summary>
    /// The default field separator.
    /// </summary>
    public const char Separator = '.';

    /// <summary>
    /// Ordered path segments.
    /// </summary>
    public ImmutableArray<FieldPathSegment> Segments { get; }

    /// <summary>
    /// Creates a path from a single field-name segment.
    /// </summary>
    public static FieldPath FromField(string field) => new([FieldPathSegment.ForField(field)]);

    /// <summary>
    /// Captures a convention-named field path from a CLR member selector rooted at the lambda parameter.
    /// </summary>
    /// <typeparam name="TRecord">CLR record type at which the selector is rooted.</typeparam>
    /// <param name="selector">Member-path selector to capture.</param>
    /// <returns>
    /// A field path whose CLR members use their <see cref="JsonPropertyNameAttribute"/> name when present
    /// and otherwise their CLR member name.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> is not a supported member path rooted at its lambda parameter.
    /// </exception>
    public static FieldPath Capture<TRecord>(Expression<Func<TRecord, object?>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return CaptureCore(selector, typeof(TRecord), resolver: null);
    }

    /// <summary>
    /// Captures a CLR property selector and resolves it through an authoritative semantic member-path mapping.
    /// </summary>
    /// <typeparam name="TRoot">CLR record type at which the selector is rooted.</typeparam>
    /// <typeparam name="TValue">CLR value type returned by the selected property chain.</typeparam>
    /// <param name="selector">Direct or nested readable-property selector rooted at its lambda parameter.</param>
    /// <param name="resolver">Resolver for the complete ordered CLR property chain.</param>
    /// <returns>The canonical semantic field path returned by <paramref name="resolver"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="selector"/> or <paramref name="resolver"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> is not a readable-property chain rooted at its lambda parameter, or
    /// <paramref name="resolver"/> rejects the chain for its selected metadata profile.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="resolver"/> cannot resolve the chain or returns an empty or uninitialized field path.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="resolver"/> does not support a property in the chain.
    /// </exception>
    public static FieldPath Capture<TRoot, TValue>(
        Expression<Func<TRoot, TValue>> selector,
        ClrMemberPathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resolver);
        return CaptureCore(selector, typeof(TRoot), resolver);
    }

    static FieldPath CaptureCore(
        LambdaExpression selector,
        Type rootType,
        ClrMemberPathResolver? resolver)
    {
        var usesSemanticResolver = resolver is not null;
        List<FieldPathSegment>? reversedSegments = usesSemanticResolver ? null : [];
        List<PropertyInfo>? reversedProperties = usesSemanticResolver ? [] : null;

        var current = StripConvert(selector.Body);
        while (true)
        {
            current = StripConvert(current);
            switch (current)
            {
                case MemberExpression member when member.Expression is not null:
                    if (usesSemanticResolver)
                    {
                        if (member.Member is not PropertyInfo { GetMethod: not null } property)
                        {
                            throw new ArgumentException(
                                "A semantic field selector must use readable CLR properties.",
                                nameof(selector));
                        }

                        reversedProperties!.Add(property);
                    }
                    else
                    {
                        if (!TryCreateFieldSegment(member.Member, out var fieldSegment))
                        {
                            throw new ArgumentException(
                                "Field selector must use CLR fields or properties.",
                                nameof(selector));
                        }

                        reversedSegments!.Add(fieldSegment);
                    }

                    current = member.Expression;
                    continue;
                case BinaryExpression binary when !usesSemanticResolver
                    && binary.NodeType == ExpressionType.ArrayIndex
                    && IsCollectionType(binary.Left.Type):
                    reversedSegments!.Add(FieldPathSegment.Element());
                    current = binary.Left;
                    continue;
                case IndexExpression index when !usesSemanticResolver
                    && index.Object is not null
                    && TryCreateStringKeySegment(index.Object, index.Arguments, out var stringKeySegment):
                    reversedSegments!.Add(stringKeySegment);
                    current = index.Object;
                    continue;
                case IndexExpression index when !usesSemanticResolver
                    && index.Object is not null
                    && index.Arguments.Count == 1
                    && IsCollectionType(index.Object.Type):
                    reversedSegments!.Add(FieldPathSegment.Element());
                    current = index.Object;
                    continue;
                case MethodCallExpression call when !usesSemanticResolver
                    && TryCreateStringKeySegment(call.Object, call.Arguments, out var methodStringKeySegment):
                    reversedSegments!.Add(methodStringKeySegment);
                    current = call.Object!;
                    continue;
                case MethodCallExpression call when !usesSemanticResolver && IsCollectionIndexerCall(call):
                    reversedSegments!.Add(FieldPathSegment.Element());
                    current = call.Object!;
                    continue;
                case ParameterExpression parameter when ReferenceEquals(parameter, selector.Parameters[0]):
                    if (usesSemanticResolver)
                    {
                        if (reversedProperties!.Count == 0)
                            throw new ArgumentException("Field selector must reference a property path.", nameof(selector));

                        reversedProperties.Reverse();
                        var resolved = resolver!(rootType, reversedProperties);
                        if (resolved.Segments.IsDefaultOrEmpty)
                        {
                            throw new InvalidOperationException(
                                "The CLR member-path resolver returned an empty or uninitialized field path.");
                        }

                        return resolved;
                    }

                    if (reversedSegments!.Count == 0)
                        throw new ArgumentException("Field selector must reference a member path.", nameof(selector));

                    reversedSegments.Reverse();
                    return new([.. reversedSegments]);
                default:
                    throw new ArgumentException(
                        usesSemanticResolver
                            ? "A semantic field selector must be a direct or nested readable-property chain rooted at its lambda parameter."
                            : "Field selector must be a member path rooted at the lambda parameter.",
                        nameof(selector));
            }
        }

        static bool TryCreateFieldSegment(MemberInfo member, out FieldPathSegment segment)
        {
            switch (member)
            {
                case PropertyInfo property:
                    segment = FieldPathSegment.ForField(ResolveFieldName(property));
                    return true;
                case FieldInfo field:
                    segment = FieldPathSegment.ForField(ResolveFieldName(field));
                    return true;
                default:
                    segment = default;
                    return false;
            }
        }

        static string ResolveFieldName(MemberInfo member)
        {
            var attribute = member.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
            return attribute?.Name ?? member.Name;
        }

        static bool TryCreateStringKeySegment(Expression? target, IReadOnlyList<Expression> arguments, out FieldPathSegment segment)
        {
            if (target is not null
                && arguments.Count == 1
                && TryResolveConstantString(arguments[0], out var key))
            {
                segment = FieldPathSegment.ForField(key);
                return true;
            }

            segment = default;
            return false;
        }

        static bool TryResolveConstantString(Expression expression, out string value)
        {
            var current = StripConvert(expression);
            if (current is ConstantExpression { Value: string constant } && !string.IsNullOrWhiteSpace(constant))
            {
                value = constant;
                return true;
            }

            value = string.Empty;
            return false;
        }

        static bool IsCollectionIndexerCall(MethodCallExpression call) =>
            call.Object is not null
            && call.Arguments.Count == 1
            && string.Equals(call.Method.Name, "get_Item", StringComparison.Ordinal)
            && IsCollectionType(call.Object.Type);

        static bool IsCollectionType(Type type) =>
            type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

        static Expression StripConvert(Expression expression)
        {
            var current = expression;
            while (current is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
                current = unary.Operand;
            return current;
        }
    }

    /// <summary>
    /// Parses dotted path text into a typed field path.
    /// </summary>
    public static FieldPath Parse(string value, char separator = Separator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var tokens = value
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(ParseSegment);
        return new([.. tokens]);
    }

    /// <summary>
    /// Appends a path segment and returns a new path.
    /// </summary>
    public FieldPath Append(FieldPathSegment segment) => new([.. Segments, segment]);

    /// <summary>
    /// Returns true when the terminal segment matches <paramref name="segment"/>.
    /// </summary>
    public bool EndsWith(FieldPathSegment segment) => Segments.Length > 0 && Segments[^1] == segment;

    /// <summary>
    /// Returns true when this path ends with <paramref name="suffix"/>.
    /// </summary>
    public bool EndsWith(FieldPath suffix)
    {
        if (suffix.Segments.Length > Segments.Length)
            return false;

        var offset = Segments.Length - suffix.Segments.Length;
        for (var i = 0; i < suffix.Segments.Length; i++)
        {
            if (Segments[offset + i] != suffix.Segments[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether this path is an ancestor of, or equal to, <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The path whose leading segments are compared with this path.</param>
    /// <returns>
    /// <see langword="true"/> when every segment in this path equals the corresponding leading
    /// segment in <paramref name="path"/>; otherwise <see langword="false"/>.
    /// </returns>
    public bool IsPrefixOf(FieldPath path)
    {
        if (Segments.Length > path.Segments.Length)
            return false;

        for (var index = 0; index < Segments.Length; index++)
        {
            if (Segments[index] != path.Segments[index])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether this path and <paramref name="path"/> address equal or nested semantic fields.
    /// </summary>
    /// <param name="path">The path to compare for equal or ancestor/descendant coverage.</param>
    /// <returns>
    /// <see langword="true"/> when either path is a prefix of the other; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool Overlaps(FieldPath path) => IsPrefixOf(path) || path.IsPrefixOf(this);

    /// <summary>
    /// Tries to resolve the terminal segment carrying a field identity.
    /// </summary>
    public bool TryGetTerminalFieldIdentity(out string fieldIdentity)
    {
        for (var i = Segments.Length - 1; i >= 0; i--)
        {
            if (!Segments[i].TryGetFieldIdentity(out var segmentIdentity))
                continue;

            fieldIdentity = segmentIdentity;
            return true;
        }

        fieldIdentity = string.Empty;
        return false;
    }

    /// <summary>Tries to resolve a direct field name without nested or collection navigation.</summary>
    /// <param name="fieldName">Direct field name when this path contains exactly one field segment.</param>
    /// <returns><see langword="true"/> when this path addresses one direct field; otherwise, <see langword="false"/>.</returns>
    public bool TryGetDirectFieldName(out string fieldName)
    {
        if (Segments is [{ Kind: SegmentKind.Field, Segment: { } directField }])
        {
            fieldName = directField;
            return true;
        }

        fieldName = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(FieldPath other)
    {
        if (Segments.Length != other.Segments.Length)
            return false;

        for (var i = 0; i < Segments.Length; i++)
        {
            if (Segments[i] != other.Segments[i])
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var segment in Segments)
            hash.Add(segment);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether the complete path string is equal to the given string.
    /// </summary>
    /// <param name="str"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    public bool Matches(string? str, StringComparison comparison = StringComparison.Ordinal) =>
        string.Equals(ToString(), str, comparison);

    /// <summary>
    /// Formats the path as a string using the specified separator.
    /// </summary>
    /// <param name="separator">The separator used to join the fields.</param>
    /// <returns>A concatenated string representing the field path</returns>
    public string ToString(char separator)
    {
        Span<char> initialBuffer = stackalloc char[Math.Min(GetFormattedLength(), 256)];
        var builder = new ValueStringBuilder(initialBuffer);
        ToString(ref builder, separator);
        return builder.ToString();
    }

    /// <summary>
    /// Formats the path as a string using the specified separator.
    /// </summary>
    /// <param name="builder">The string builder to write to.</param>
    /// <param name="separator">The separator used to join the fields.</param>
    public void ToString(ref ValueStringBuilder builder, char separator = Separator)
    {
        for (var i = 0; i < Segments.Length; i++)
        {
            if (i > 0)
                builder.Append(separator);
            Segments[i].WriteString(ref builder);
        }
    }

    /// <summary>
    /// Formats the path as a string using the default separator <see cref="Separator"/>.
    /// </summary>
    /// <returns>A concatenated string representing the field path</returns>
    public override string ToString() =>
        ToString(Separator);

    int GetFormattedLength()
    {
        var length = Segments.Length - 1;
        foreach (var segment in Segments)
            length += segment.GetFormattedLength();
        return length;
    }

    static IEnumerable<FieldPathSegment> ParseSegment(string token)
    {
        if (string.Equals(token, "[]", StringComparison.Ordinal))
        {
            yield return FieldPathSegment.Element();
            yield break;
        }

        if (token.EndsWith("[]", StringComparison.Ordinal) && token.Length > 2)
        {
            yield return FieldPathSegment.ForField(token[..^2]);
            yield return FieldPathSegment.Element();
            yield break;
        }

        yield return FieldPathSegment.ForField(token);
    }
}

/// <summary>
/// One step in a field path navigation.
/// </summary>
public readonly record struct FieldPathSegment : IEquatable<FieldPathSegment>
{
    /// <summary>
    /// Creates a field path segment.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    [JsonConstructor]
    public FieldPathSegment(SegmentKind kind, string? segment = null)
    {
        Kind = kind;
        Segment = segment;
        switch (kind)
        {
            case SegmentKind.Field:
                Segment = Guard.RequireNotNullOrWhiteSpace(segment);
                break;
            case SegmentKind.Element:
                if (segment is not null)
                    throw new ArgumentException(message: "Element segment cannot carry token text.", paramName: nameof(segment));
                break;
        }
    }

    /// <summary>
    /// Segment kind.
    /// </summary>
    public SegmentKind Kind { get; }

    /// <summary>
    /// Segment token text.
    /// </summary>
    public string? Segment { get; }

    /// <summary>
    /// Tries to resolve the segment token as a field identity for field segments.
    /// </summary>
    public bool TryGetFieldIdentity(out string fieldIdentity)
    {
        if (Kind is SegmentKind.Field && Segment is not null)
        {
            fieldIdentity = Segment;
            return true;
        }
        fieldIdentity = string.Empty;
        return false;
    }

    /// <summary>
    /// Creates a direct field segment from token text.
    /// </summary>
    public static FieldPathSegment ForField(string segment) => new(SegmentKind.Field, segment);

    /// <summary>
    /// Creates an element navigation segment.
    /// </summary>
    public static FieldPathSegment Element() => new(SegmentKind.Element);

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind switch
        {
            SegmentKind.Field => Segment!,
            SegmentKind.Element => "[]",
            _ => throw new InvalidOperationException($"Unsupported segment kind '{Kind}'.")
        };
    }

    /// <summary>Writes string.</summary>
    public void WriteString(ref ValueStringBuilder builder)
    {
        switch (Kind)
        {
            case SegmentKind.Field:
                builder.Append(Segment);
                return;
            case SegmentKind.Element:
                builder.Append("[]");
                return;
            default:
                throw new InvalidOperationException($"Unsupported segment kind '{Kind}'.");
        }
    }

    internal int GetFormattedLength()
    {
        return Kind switch
        {
            SegmentKind.Field => Segment!.Length,
            SegmentKind.Element => 2,
            _ => throw new InvalidOperationException($"Unsupported segment kind '{Kind}'.")
        };
    }
}

/// <summary>
/// Navigation segment kind in a typed field path.
/// </summary>
public enum SegmentKind
{
    /// <summary>
    /// A field segment.
    /// </summary>
    Field = 0,

    /// <summary>
    /// A segment indicating access to an element of a collection.
    /// </summary>
    Element = 2
}

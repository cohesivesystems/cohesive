using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>A validated repository-relative path retained by a portable semantic artifact.</summary>
[JsonConverter(typeof(RepositoryPathJsonConverter))]
public readonly record struct RepositoryPath : IComparable<RepositoryPath>
{
    /// <summary>Creates a validated repository-relative path.</summary>
    /// <param name="value">Path relative to the repository root, using either platform separator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, absolute, contains an empty, current-directory, or parent-directory
    /// segment, or otherwise escapes the repository.
    /// </exception>
    public RepositoryPath(string value)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value).Replace('\\', '/');
        var segments = value.Split('/');
        var isWindowsAbsolute = value.Length >= 3
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] == '/';
        if (Path.IsPathRooted(value)
            || isWindowsAbsolute
            || segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException(
                "A repository path must be relative to the repository root and cannot escape it.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Canonical slash-separated repository-relative path.</summary>
    public string Value { get; }

    /// <summary>Compares paths by their canonical ordinal representation.</summary>
    /// <param name="other">Other repository path.</param>
    /// <returns>A value indicating the canonical ordinal ordering.</returns>
    public int CompareTo(RepositoryPath other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <summary>Returns the canonical repository-relative path.</summary>
    /// <returns>The canonical slash-separated path.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable attributable reference to a source, specification, provider artifact, decision, or observation.
/// </summary>
/// <remarks>
/// References use a URI-shaped canonical representation while remaining independent of network retrieval. A scheme
/// identifies the authority or reference kind; the scheme-specific value identifies the exact source within it.
/// </remarks>
[JsonConverter(typeof(SourceReferenceJsonConverter))]
public readonly record struct SourceReference : IComparable<SourceReference>
{
    /// <summary>Creates a canonical source reference from an already composed value.</summary>
    /// <param name="value">Canonical URI-shaped reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, contains white-space, or is not in canonical
    /// <c>scheme://identity</c> form.
    /// </exception>
    public SourceReference(string value)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value);
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        var identityStart = separator + 3;
        if (separator <= 0
            || identityStart >= value.Length
            || !IsSchemeStart(value[0])
            || value.AsSpan(1, separator - 1).ContainsAnyExcept(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+.-")
            || value.AsSpan(0, separator).ContainsAny("ABCDEFGHIJKLMNOPQRSTUVWXYZ"))
        {
            throw new ArgumentException(
                "A source reference requires canonical scheme://identity form.",
                nameof(value));
        }
        if (value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A source reference cannot contain white-space.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Canonical URI-shaped value.</summary>
    public string Value { get; }

    /// <summary>Validates and converts a canonical URI-shaped string reference.</summary>
    /// <param name="value">Canonical URI-shaped reference.</param>
    /// <returns>The validated source reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical source reference.</exception>
    public static implicit operator SourceReference(string value) => new(value);

    /// <summary>Creates a source reference from a scheme and scheme-specific identity.</summary>
    /// <param name="scheme">Stable lowercase reference scheme.</param>
    /// <param name="identity">Non-empty scheme-specific identity.</param>
    /// <returns>A canonical reference formatted as <c>scheme://identity</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The scheme or identity is invalid.</exception>
    public static SourceReference Create(string scheme, string identity)
    {
        scheme = Guard.RequireNotNullOrWhiteSpace(scheme);
        identity = Guard.RequireNotNullOrWhiteSpace(identity);
        if (!IsSchemeStart(scheme[0])
            || scheme.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '+' and not '.' and not '-'))
        {
            throw new ArgumentException("A source-reference scheme contains an unsupported character.", nameof(scheme));
        }
        if (!string.Equals(scheme, scheme.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A source-reference scheme must be lowercase.", nameof(scheme));
        }

        if (identity.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A source-reference identity cannot contain white-space.", nameof(identity));
        }

        return new($"{scheme}://{identity}");
    }

    /// <summary>Creates a canonical reference to a repository artifact.</summary>
    /// <param name="path">Validated repository-relative path.</param>
    /// <returns>A canonical <c>repo://</c> reference.</returns>
    public static SourceReference Repository(RepositoryPath path) => Create("repo", path.Value);

    /// <summary>Creates a canonical reference to an external issue or decision record.</summary>
    /// <param name="provider">Stable provider scheme, such as <c>linear</c>.</param>
    /// <param name="identifier">Provider-local issue or decision identity.</param>
    /// <returns>A canonical provider reference.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The provider or identifier is invalid.</exception>
    public static SourceReference Issue(string provider, string identifier) => Create(provider, identifier);

    /// <summary>Validates, ordinally sorts, and deduplicates a source-reference set.</summary>
    /// <param name="references">Source references to normalize.</param>
    /// <param name="requireNonEmpty">Whether the normalized set must contain at least one reference.</param>
    /// <param name="parameterName">Caller argument name reported by validation exceptions.</param>
    /// <returns>An immutable canonical set ordered by reference value.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is default, the set contains duplicates, or <paramref name="requireNonEmpty"/> is
    /// <see langword="true"/> and <paramref name="references"/> is empty.
    /// </exception>
    public static ImmutableArray<SourceReference> NormalizeSet(
        ImmutableArray<SourceReference> references,
        bool requireNonEmpty = false,
        [CallerArgumentExpression(nameof(references))] string? parameterName = null)
    {
        parameterName ??= nameof(references);
        if (references.IsDefaultOrEmpty)
        {
            if (requireNonEmpty)
                throw new ArgumentException("The source-reference collection cannot be empty.", parameterName);
            return [];
        }

        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Value))
                throw new ArgumentException("Source references cannot be default or empty.", parameterName);
        }

        var ordered = references.Sort();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1] == ordered[index])
                throw new ArgumentException($"Source reference '{ordered[index].Value}' is duplicated.", parameterName);
        }
        return ordered;
    }

    /// <summary>Compares references by their canonical ordinal representation.</summary>
    /// <param name="other">Other source reference.</param>
    /// <returns>A value indicating the canonical ordinal ordering.</returns>
    public int CompareTo(SourceReference other) => StringComparer.Ordinal.Compare(Value, other.Value);

    /// <summary>Returns the canonical URI-shaped representation.</summary>
    /// <returns>The canonical source-reference value.</returns>
    public override string ToString() => Value;

    static bool IsSchemeStart(char character) => char.IsAsciiLetter(character);
}

sealed class RepositoryPathJsonConverter : JsonConverter<RepositoryPath>
{
    public override RepositoryPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A repository path must be a JSON string."));

    public override void Write(Utf8JsonWriter writer, RepositoryPath value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

sealed class SourceReferenceJsonConverter : JsonConverter<SourceReference>
{
    public override SourceReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A source reference must be a JSON string."));

    public override void Write(Utf8JsonWriter writer, SourceReference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

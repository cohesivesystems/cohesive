using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Structured, portable evidence that explains a document diagnostic and possible resolutions.
/// </summary>
/// <remarks>
/// Collection members are semantic sets. They are retained in deterministic ordinal order so
/// equality, hashing, serialization, and diagnostic ordering do not depend on producer order.
/// </remarks>
public sealed record DocumentDiagnosticEvidence
{
    /// <summary>Creates normalized structured diagnostic evidence.</summary>
    /// <param name="stage">Optional validation or compilation stage that produced the diagnostic.</param>
    /// <param name="subject">Optional stable identity of the semantic construct under analysis.</param>
    /// <param name="relatedLocations">Other persisted or semantic locations relevant to the diagnostic.</param>
    /// <param name="sourceReferences">Producer-defined references supporting the diagnostic.</param>
    /// <param name="resolutionOptions">Stable descriptions of available ways to resolve the diagnostic.</param>
    /// <param name="expected">Optional expected value, contract, state, or guarantee.</param>
    /// <param name="observed">Optional observed value, contract, state, or guarantee.</param>
    /// <exception cref="ArgumentException">
    /// An optional scalar is empty or consists only of white-space characters; or a collection
    /// contains an empty, white-space, or duplicate entry.
    /// </exception>
    [JsonConstructor]
    public DocumentDiagnosticEvidence(
        string? stage = null,
        string? subject = null,
        ImmutableArray<string> relatedLocations = default,
        ImmutableArray<string> sourceReferences = default,
        ImmutableArray<string> resolutionOptions = default,
        string? expected = null,
        string? observed = null)
    {
        Stage = ValidateOptional(stage, nameof(stage));
        Subject = ValidateOptional(subject, nameof(subject));
        RelatedLocations = NormalizeOrdinalSet(relatedLocations, nameof(relatedLocations));
        SourceReferences = NormalizeOrdinalSet(sourceReferences, nameof(sourceReferences));
        ResolutionOptions = NormalizeOrdinalSet(resolutionOptions, nameof(resolutionOptions));
        Expected = ValidateOptional(expected, nameof(expected));
        Observed = ValidateOptional(observed, nameof(observed));
    }

    /// <summary>Optional validation or compilation stage that produced the diagnostic.</summary>
    public string? Stage { get; }

    /// <summary>Optional stable identity of the semantic construct under analysis.</summary>
    public string? Subject { get; }

    /// <summary>Other persisted or semantic locations in deterministic ordinal order.</summary>
    public ImmutableArray<string> RelatedLocations { get; }

    /// <summary>Producer-defined supporting references in deterministic ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Available resolution descriptions in deterministic ordinal order.</summary>
    public ImmutableArray<string> ResolutionOptions { get; }

    /// <summary>Optional expected value, contract, state, or guarantee.</summary>
    public string? Expected { get; }

    /// <summary>Optional observed value, contract, state, or guarantee.</summary>
    public string? Observed { get; }

    /// <summary>Compares evidence using structural ordinal value semantics.</summary>
    /// <param name="other">Evidence to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when every scalar and normalized collection is equal; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool Equals(DocumentDiagnosticEvidence? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null
            || !string.Equals(Stage, other.Stage, StringComparison.Ordinal)
            || !string.Equals(Subject, other.Subject, StringComparison.Ordinal)
            || !string.Equals(Expected, other.Expected, StringComparison.Ordinal)
            || !string.Equals(Observed, other.Observed, StringComparison.Ordinal))
        {
            return false;
        }

        return RelatedLocations.SequenceEqual(other.RelatedLocations)
               && SourceReferences.SequenceEqual(other.SourceReferences)
               && ResolutionOptions.SequenceEqual(other.ResolutionOptions);
    }

    /// <summary>Returns a structural ordinal hash code for all evidence.</summary>
    /// <returns>A hash code derived from every scalar and normalized collection entry.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Stage, StringComparer.Ordinal);
        hash.Add(Subject, StringComparer.Ordinal);
        Add(ref hash, RelatedLocations);
        Add(ref hash, SourceReferences);
        Add(ref hash, ResolutionOptions);
        hash.Add(Expected, StringComparer.Ordinal);
        hash.Add(Observed, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    static string? ValidateOptional(string? value, string paramName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Optional diagnostic evidence cannot be empty or white-space.", paramName);

        return value;
    }

    static ImmutableArray<string> NormalizeOrdinalSet(
        ImmutableArray<string> values,
        string paramName)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Diagnostic evidence collections cannot contain empty or white-space entries.",
                    paramName);
            }

            if (!observed.Add(value))
            {
                throw new ArgumentException(
                    $"Diagnostic evidence entry '{value}' is duplicated.",
                    paramName);
            }
        }

        return CanonicalDocumentCollections.SortIfNeeded(
            values,
            static (left, right) => StringComparer.Ordinal.Compare(left, right));
    }

    static void Add(ref HashCode hash, ImmutableArray<string> values)
    {
        foreach (var value in values)
            hash.Add(value, StringComparer.Ordinal);
    }
}

/// <summary>
/// Validation diagnostic for portable semantic documents.
/// </summary>
/// <param name="Code">Stable machine-readable diagnostic code.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="Location">Optional persisted-document location, normally a JSON Pointer.</param>
/// <param name="SchemaLocation">Optional canonical semantic or schema location.</param>
/// <param name="Evidence">Optional structured evidence supporting the diagnostic and its resolution.</param>
public sealed record DocumentValidationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Location = null,
    string? SchemaLocation = null,
    DocumentDiagnosticEvidence? Evidence = null
    );

/// <summary>Shared canonical collection handling for portable document diagnostics.</summary>
/// <remarks>
/// Use this authority when diagnostic ordering is explicitly non-semantic. Validation results or
/// protocols that define stage or producer order must retain that order instead of normalizing it.
/// </remarks>
public static class DocumentValidationDiagnostics
{
    /// <summary>
    /// Initializes, validates, and deterministically orders a diagnostic collection.
    /// </summary>
    /// <param name="diagnostics">Diagnostics whose collection representation is normalized.</param>
    /// <returns>
    /// An initialized empty collection when <paramref name="diagnostics"/> is default or empty;
    /// <paramref name="diagnostics"/> itself when already canonical; otherwise an ordinally sorted
    /// immutable copy.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="diagnostics"/> contains a <see langword="null"/> entry.
    /// </exception>
    public static ImmutableArray<DocumentValidationDiagnostic> Normalize(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
            return [];

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic is null)
            {
                throw new ArgumentException(
                    "Portable document diagnostics cannot contain null entries.",
                    nameof(diagnostics));
            }
        }

        return CanonicalDocumentCollections.SortIfNeeded(
            diagnostics,
            DocumentValidationDiagnosticComparer.Ordinal.Compare);
    }
}

/// <summary>
/// Validation result for portable semantic documents.
/// </summary>
public sealed record DocumentValidationResult
{
    /// <summary>
    /// Empty successful validation result.
    /// </summary>
    public static DocumentValidationResult Valid { get; } = new([]);

    /// <summary>
    /// Creates a validation result.
    /// </summary>
    /// <param name="diagnostics">Diagnostics retained by the result.</param>
    public DocumentValidationResult(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>
    /// Validation diagnostics.
    /// </summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares validation results using structural diagnostic value semantics.</summary>
    /// <param name="other">Validation result to compare with this result.</param>
    /// <returns><see langword="true"/> when every diagnostic is equal in canonical order; otherwise <see langword="false"/>.</returns>
    public bool Equals(DocumentValidationResult? other) =>
        ReferenceEquals(this, other)
        || other is not null && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code for the ordered diagnostics.</summary>
    /// <returns>A hash code derived from every diagnostic in canonical order.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var diagnostic in Diagnostics)
        {
            hash.Add(diagnostic);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns true when there are no error diagnostics.
    /// </summary>
    public bool IsValid => !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Creates a result from diagnostics.
    /// </summary>
    /// <param name="diagnostics">Diagnostics to materialize into a result.</param>
    /// <returns>A valid singleton result when empty; otherwise a result retaining every diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult FromDiagnostics(IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var array = diagnostics.ToImmutableArray();
        return array.IsDefaultOrEmpty ? Valid : new(array);
    }

    /// <summary>
    /// Combines validation results.
    /// </summary>
    /// <param name="results">Validation results whose diagnostics are concatenated in supplied order.</param>
    /// <returns>A result containing every supplied diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Combine(params DocumentValidationResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return FromDiagnostics(results.SelectMany(x => x.Diagnostics));
    }
}

/// <summary>
/// Deterministic ordinal ordering for portable document diagnostics.
/// </summary>
/// <remarks>
/// Diagnostics are ordered by persisted location, semantic/schema location, stable code, severity,
/// message, and structured evidence. Producers may use this comparer when diagnostic order is
/// non-semantic but serialized output must remain reproducible.
/// </remarks>
public sealed class DocumentValidationDiagnosticComparer : IComparer<DocumentValidationDiagnostic>
{
    /// <summary>Shared deterministic ordinal diagnostic comparer.</summary>
    public static DocumentValidationDiagnosticComparer Ordinal { get; } = new();

    DocumentValidationDiagnosticComparer()
    {
    }

    /// <summary>Compares two diagnostics in deterministic portable-document order.</summary>
    /// <param name="x">First diagnostic, or <see langword="null"/>.</param>
    /// <param name="y">Second diagnostic, or <see langword="null"/>.</param>
    /// <returns>
    /// A negative value when <paramref name="x"/> precedes <paramref name="y"/>, zero when their
    /// ordering fields are equal, or a positive value otherwise. Null values precede non-null values.
    /// </returns>
    public int Compare(DocumentValidationDiagnostic? x, DocumentValidationDiagnostic? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(x.Location, y.Location);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(x.SchemaLocation, y.SchemaLocation);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(x.Code, y.Code);
        if (comparison != 0)
            return comparison;
        comparison = x.Severity.CompareTo(y.Severity);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(x.Message, y.Message);
        return comparison != 0 ? comparison : CompareEvidence(x.Evidence, y.Evidence);
    }

    static int CompareEvidence(DocumentDiagnosticEvidence? left, DocumentDiagnosticEvidence? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(left.Stage, right.Stage);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Subject, right.Subject);
        if (comparison != 0)
            return comparison;
        comparison = CompareOrdinal(left.RelatedLocations, right.RelatedLocations);
        if (comparison != 0)
            return comparison;
        comparison = CompareOrdinal(left.SourceReferences, right.SourceReferences);
        if (comparison != 0)
            return comparison;
        comparison = CompareOrdinal(left.ResolutionOptions, right.ResolutionOptions);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Expected, right.Expected);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Observed, right.Observed);
    }

    static int CompareOrdinal(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        var commonLength = Math.Min(left.Length, right.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }
}

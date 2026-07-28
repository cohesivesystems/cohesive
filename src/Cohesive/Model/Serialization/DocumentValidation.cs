using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Validation diagnostic for portable semantic documents.
/// </summary>
/// <param name="Code">Stable machine-readable diagnostic code.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Message">Human-readable diagnostic message.</param>
/// <param name="Location">Optional persisted-document location, normally a JSON Pointer.</param>
/// <param name="SchemaLocation">Optional canonical semantic or schema location.</param>
public sealed record DocumentValidationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Location = null,
    string? SchemaLocation = null
    );

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
/// and message. Producers may use this comparer when diagnostic order is non-semantic but serialized
/// output must remain reproducible.
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
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(x.Message, y.Message);
    }
}

/// <summary>Shared allocation-aware normalization for canonical immutable document collections.</summary>
static class CanonicalDocumentCollections
{
    /// <summary>Retains canonical storage or returns an ordinally sorted immutable copy.</summary>
    /// <typeparam name="T">Collection item type.</typeparam>
    /// <param name="values">Initialized immutable values to normalize.</param>
    /// <param name="comparison">Canonical ordering comparison.</param>
    /// <returns>
    /// <paramref name="values"/> when already ordered; otherwise a sorted immutable copy.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> is the default immutable array.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="comparison"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<T> SortIfNeeded<T>(
        ImmutableArray<T> values,
        Comparison<T> comparison)
    {
        if (values.IsDefault)
            throw new ArgumentException("Canonical document values must be initialized.", nameof(values));
        ArgumentNullException.ThrowIfNull(comparison);

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

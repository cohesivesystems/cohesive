using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Realization;

/// <summary>Structured outcome of compiling one canonical storage structure for a physical adapter.</summary>
public sealed record StorageRealizationCompilationResult
{
    StorageRealizationCompilationResult(
        StorageRealizationDocument? document,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        var normalized = diagnostics.IsDefault
            ? []
            : diagnostics
                .Distinct()
                .Order(DocumentValidationDiagnosticComparer.Ordinal)
                .ToImmutableArray();
        var hasErrors = normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (document is null && !hasErrors)
        {
            throw new ArgumentException(
                "An unsuccessful storage-realization compilation requires an error diagnostic.",
                nameof(diagnostics));
        }
        if (document is not null && hasErrors)
        {
            throw new ArgumentException(
                "A successful storage-realization compilation cannot retain error diagnostics.",
                nameof(diagnostics));
        }

        Document = document;
        Diagnostics = normalized;
    }

    /// <summary>Compiled portable realization document, or <see langword="null"/> when compilation failed.</summary>
    public StorageRealizationDocument? Document { get; }

    /// <summary>Deterministically ordered compilation diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether compilation produced an exact portable realization document.</summary>
    public bool IsSuccessful => Document is not null;

    /// <summary>Creates an exact successful compilation result.</summary>
    /// <param name="document">Validated realization document produced by the adapter.</param>
    /// <param name="diagnostics">Optional non-error diagnostics retained with the result.</param>
    /// <returns>A successful compilation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> contains an error.</exception>
    public static StorageRealizationCompilationResult Success(
        StorageRealizationDocument document,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) => new(
        Guard.RequireNotNull(document),
        diagnostics);

    /// <summary>Creates an unsuccessful compilation result.</summary>
    /// <param name="diagnostics">Diagnostics containing at least one error.</param>
    /// <returns>An unsuccessful compilation result.</returns>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> contains no error.</exception>
    public static StorageRealizationCompilationResult Failure(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) => new(
        document: null,
        diagnostics);
}

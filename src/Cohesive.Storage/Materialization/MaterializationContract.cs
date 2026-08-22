using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Shared validation and normalization for materialization wire and runtime contracts.</summary>
public static class MaterializationContract
{
    internal const long MaximumPortableInteger = 9_007_199_254_740_991;
    static readonly System.Text.Json.JsonSerializerOptions CanonicalJsonOptions =
        MaterializationJsonSerializer.CreateOptions();

    internal static bool CanonicalEquals<T>(T left, T right) where T : class
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return StrictDocumentJson.GetCanonicalBytes(left, CanonicalJsonOptions)
            .AsSpan()
            .SequenceEqual(StrictDocumentJson.GetCanonicalBytes(right, CanonicalJsonOptions));
    }

    /// <summary>Creates one complete, attributable materialization diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="message">Human-readable diagnostic message.</param>
    /// <param name="location">Portable semantic document or operation location.</param>
    /// <param name="stage">Interpretation stage that emitted the diagnostic.</param>
    /// <param name="subject">Semantic subject of the diagnostic.</param>
    /// <param name="sourceReferences">Non-empty attributable evidence references.</param>
    /// <param name="expected">Expected semantic or operational state.</param>
    /// <param name="observed">Observed state, excluding secrets and sensitive values.</param>
    /// <param name="schemaLocation">Optional schema location.</param>
    /// <param name="relatedLocations">Optional related semantic locations.</param>
    /// <param name="resolutionOptions">Optional actionable resolution choices.</param>
    /// <returns>A normalized, complete document-validation diagnostic.</returns>
    /// <exception cref="ArgumentNullException">A required string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required string or source reference is absent, or a collection is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    public static DocumentValidationDiagnostic CreateDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        string location,
        string stage,
        string subject,
        ImmutableArray<string> sourceReferences,
        string expected,
        string observed,
        string? schemaLocation = null,
        ImmutableArray<string> relatedLocations = default,
        ImmutableArray<string> resolutionOptions = default)
    {
        RequireIdentity(code, nameof(code));
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        }

        RequireIdentity(message, nameof(message));
        RequireIdentity(location, nameof(location));
        RequireIdentity(stage, nameof(stage));
        RequireIdentity(subject, nameof(subject));
        RequireIdentity(expected, nameof(expected));
        RequireIdentity(observed, nameof(observed));
        if (sourceReferences.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A materialization diagnostic requires source attribution.", nameof(sourceReferences));
        }

        DocumentValidationDiagnostic diagnostic = new(
            code,
            severity,
            message,
            location,
            schemaLocation,
            new(
                stage,
                subject,
                relatedLocations,
                sourceReferences,
                resolutionOptions,
                expected,
                observed));
        RequireCompleteDiagnostic(diagnostic, nameof(diagnostic));
        return diagnostic;
    }

    internal static DocumentValidationResult ErrorResult(
        string code,
        string message,
        string location,
        string stage,
        string subject,
        ImmutableArray<string> sourceReferences,
        string expected,
        string observed) =>
        new([
            CreateDiagnostic(
                code,
                DiagnosticSeverity.Error,
                message,
                location,
                stage,
                subject,
                sourceReferences,
                expected,
                observed)
        ]);

    internal static string RequireIdentity(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    internal static string RequireUnicodeIdentity(string? value, string parameterName)
    {
        value = RequireIdentity(value, parameterName);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException(
                    "A materialization identity must contain well-formed Unicode scalar values.",
                    parameterName);
            }
            remaining = remaining[consumed..];
        }
        return value;
    }

    internal static string RequireDefinedIdentity(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A required identity must not be default, empty, or white-space.",
                parameterName);
        }

        return value;
    }

    internal static long RequirePortablePositiveBound(long value, string parameterName)
    {
        if (value is <= 0 or > MaximumPortableInteger)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A materialization bound must be positive and portable to JSON runtimes.");
        }

        return value;
    }

    internal static void RequireSource(RelationQuerySourceInstanceId source, string parameterName) =>
        RequireDefinedIdentity(source.Value, parameterName);

    internal static void RequirePartition(MaterializationSourcePartitionId partition, string parameterName) =>
        RequireDefinedIdentity(partition.Value, parameterName);

    internal static string RequireOrdinal(
        string value,
        string parameterName,
        bool allowZero,
        out long ordinal)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            || ordinal < (allowZero ? 0 : 1)
            || !string.Equals(value, ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                allowZero
                    ? "The value must be a canonical nonnegative 64-bit integer string."
                    : "The value must be a canonical positive 64-bit integer string.",
                parameterName);
        }

        return value;
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A materialization timestamp must be expressed in UTC.", parameterName);
        }
    }

    internal static ImmutableArray<DocumentValidationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        string parameterName)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        for (var index = 0; index < diagnostics.Length; index++)
        {
            if (diagnostics[index] is null)
            {
                throw new ArgumentException("Materialization diagnostics cannot contain null entries.", parameterName);
            }

            RequireCompleteDiagnostic(diagnostics[index], parameterName);
        }

        return DocumentValidationDiagnostics.Normalize(diagnostics);
    }

    internal static DocumentValidationResult NormalizeValidation(
        DocumentValidationResult validation,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(validation, parameterName);
        var diagnostics = NormalizeDiagnostics(validation.Diagnostics, parameterName);
        return diagnostics == validation.Diagnostics
            ? validation
            : diagnostics.IsDefaultOrEmpty
                ? DocumentValidationResult.Valid
                : new DocumentValidationResult(diagnostics);
    }

    static void RequireCompleteDiagnostic(
        DocumentValidationDiagnostic diagnostic,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.Code)
            || !Enum.IsDefined(diagnostic.Severity)
            || string.IsNullOrWhiteSpace(diagnostic.Message)
            || (string.IsNullOrWhiteSpace(diagnostic.Location)
                && string.IsNullOrWhiteSpace(diagnostic.SchemaLocation))
            || diagnostic.Evidence is not { } evidence
            || string.IsNullOrWhiteSpace(evidence.Stage)
            || string.IsNullOrWhiteSpace(evidence.Subject)
            || evidence.SourceReferences.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(evidence.Expected)
            || string.IsNullOrWhiteSpace(evidence.Observed))
        {
            throw new ArgumentException(
                "Every materialization diagnostic requires a stable code, severity, message, semantic location, stage, subject, source references, expected value, and observed value.",
                parameterName);
        }
    }
}

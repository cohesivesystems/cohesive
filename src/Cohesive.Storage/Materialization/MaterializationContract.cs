using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Shared validation and normalization for materialization wire and runtime contracts.</summary>
internal static class MaterializationContract
{
    internal static DocumentValidationDiagnostic CreateDiagnostic(
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

        var isCanonical = true;
        for (var index = 0; index < diagnostics.Length; index++)
        {
            if (diagnostics[index] is null)
            {
                throw new ArgumentException("Materialization diagnostics cannot contain null entries.", parameterName);
            }

            RequireCompleteDiagnostic(diagnostics[index], parameterName);
            if (index > 0
                && DocumentValidationDiagnosticComparer.Ordinal.Compare(diagnostics[index - 1], diagnostics[index]) > 0)
            {
                isCanonical = false;
            }
        }
        if (isCanonical)
        {
            return diagnostics;
        }

        var normalized = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(diagnostics.Length);
        normalized.AddRange(diagnostics);
        normalized.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return normalized.MoveToImmutable();
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

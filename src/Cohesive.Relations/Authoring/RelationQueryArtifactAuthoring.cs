using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Authoring;

/// <summary>One structured, actionable diagnostic produced while authoring a derived artifact.</summary>
public sealed record RelationQueryArtifactAuthoringDiagnostic
{
    /// <summary>Creates an artifact-authoring diagnostic.</summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="severity">Effective diagnostic severity.</param>
    /// <param name="message">Human-readable explanation of the problem.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="semanticPath">Affected semantic path, or <see langword="null"/>.</param>
    /// <param name="setting">Affected configuration setting, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A required string is empty, a supplied input is default, a supplied path is empty, or
    /// <paramref name="setting"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    public RelationQueryArtifactAuthoringDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryInputId? input = null,
        FieldPath? semanticPath = null,
        string? setting = null)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        }

        Message = Guard.RequireNotNullOrWhiteSpace(message);
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
        {
            throw new ArgumentException("A diagnostic input identity cannot be default.", nameof(input));
        }

        if (semanticPath is { Segments.IsDefaultOrEmpty: true })
        {
            throw new ArgumentException("A diagnostic semantic path cannot be empty.", nameof(semanticPath));
        }

        if (setting is not null && string.IsNullOrWhiteSpace(setting))
        {
            throw new ArgumentException("A diagnostic setting cannot be empty or white space.", nameof(setting));
        }

        Severity = severity;
        Input = input;
        SemanticPath = semanticPath;
        Setting = setting;
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Effective diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Human-readable explanation of the problem.</summary>
    public string Message { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected semantic path, or <see langword="null"/>.</summary>
    public FieldPath? SemanticPath { get; }

    /// <summary>Affected configuration setting, or <see langword="null"/>.</summary>
    public string? Setting { get; }
}

/// <summary>Fail-closed result from authoring one immutable derived artifact.</summary>
/// <typeparam name="TArtifact">Type of successfully authored artifact.</typeparam>
public sealed class RelationQueryArtifactAuthoringResult<TArtifact>
    where TArtifact : class
{
    readonly TArtifact? value;

    /// <summary>Creates an artifact-authoring result.</summary>
    /// <param name="value">Successfully authored artifact, or <see langword="null"/> on failure.</param>
    /// <param name="diagnostics">Normalized structured diagnostics produced by authoring.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="diagnostics"/> contains a null entry; a failed result has no error diagnostic; or a
    /// successful result contains an error diagnostic.
    /// </exception>
    public RelationQueryArtifactAuthoringResult(
        TArtifact? value,
        ImmutableArray<RelationQueryArtifactAuthoringDiagnostic> diagnostics)
    {
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Artifact-authoring diagnostics cannot contain null entries.", nameof(diagnostics));
        }

        Diagnostics =
        [
            .. normalized
                .Distinct()
                .OrderBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SemanticPath?.ToString() ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Setting ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];
        var hasErrors = Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (value is null && !hasErrors)
        {
            throw new ArgumentException("An unsuccessful authoring result requires an error diagnostic.", nameof(diagnostics));
        }

        if (value is not null && hasErrors)
        {
            throw new ArgumentException("A successful authoring result cannot contain an error diagnostic.", nameof(diagnostics));
        }

        this.value = value;
    }

    /// <summary>Normalized diagnostics produced while authoring the artifact.</summary>
    public ImmutableArray<RelationQueryArtifactAuthoringDiagnostic> Diagnostics { get; }

    /// <summary>Whether a complete immutable artifact was produced without errors.</summary>
    public bool IsSuccess => value is not null;

    /// <summary>Attempts to obtain the successfully authored artifact.</summary>
    /// <param name="artifact">Receives the artifact on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when authoring succeeded; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue([NotNullWhen(true)] out TArtifact? artifact)
    {
        artifact = value;
        return artifact is not null;
    }

    /// <summary>Returns the authored artifact or throws the structured authoring failure.</summary>
    /// <returns>The immutable authored artifact.</returns>
    /// <exception cref="RelationQueryArtifactAuthoringException">Authoring produced one or more errors.</exception>
    public TArtifact RequireValue() =>
        value ?? throw new RelationQueryArtifactAuthoringException(Diagnostics);
}

/// <summary>Exception carrying structured diagnostics for failed derived-artifact authoring.</summary>
public sealed class RelationQueryArtifactAuthoringException : InvalidOperationException
{
    /// <summary>Creates an artifact-authoring exception.</summary>
    /// <param name="diagnostics">Diagnostics explaining why no complete artifact was produced.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="diagnostics"/> is empty, contains a null entry, or contains no error diagnostic.
    /// </exception>
    public RelationQueryArtifactAuthoringException(
        ImmutableArray<RelationQueryArtifactAuthoringDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Structured diagnostics explaining the failed authoring operation.</summary>
    public ImmutableArray<RelationQueryArtifactAuthoringDiagnostic> Diagnostics { get; }

    static string CreateMessage(ImmutableArray<RelationQueryArtifactAuthoringDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty
            || diagnostics.Any(static diagnostic => diagnostic is null)
            || !diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new ArgumentException("At least one error diagnostic is required.", nameof(diagnostics));
        }

        var first = diagnostics.First(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return first.Setting is { } setting
            ? $"Relation/query artifact authoring failed with {first.Code} at setting '{setting}': {first.Message}"
            : $"Relation/query artifact authoring failed with {first.Code}: {first.Message}";
    }
}

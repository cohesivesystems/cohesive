using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Whether an execution interpretation can currently admit or continue work.</summary>
public enum ExecutionReadinessStatus
{
    /// <summary>Available evidence does not establish readiness.</summary>
    Unknown = 0,

    /// <summary>The interpretation can currently admit or continue work within its declared contract.</summary>
    Ready = 1,

    /// <summary>The interpretation cannot currently admit or continue work.</summary>
    NotReady = 2
}

/// <summary>Attributable health and readiness projected from existing runtime evidence.</summary>
/// <remarks>
/// This is an immutable observation, not a mutable health authority. Its values are derived from the supplied
/// runtime artifact at <see cref="ObservedAtUtc"/> and retain the producer that made the observation.
/// </remarks>
public sealed record ExecutionHealthObservation
{
    /// <summary>Creates one attributable execution-health observation.</summary>
    /// <param name="health">Current operational health.</param>
    /// <param name="readiness">Current ability to admit or continue work.</param>
    /// <param name="observedAtUtc">UTC time of the authoritative source observation.</param>
    /// <param name="provenance">Producer and source attribution for the projection.</param>
    /// <param name="evidenceReferences">Optional non-sensitive references to contributing runtime evidence.</param>
    /// <param name="diagnostics">Optional structured health diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="health"/> or <paramref name="readiness"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="observedAtUtc"/> is not UTC, evidence references contain empty or duplicate entries, or
    /// diagnostics contain a null entry.
    /// </exception>
    [JsonConstructor]
    public ExecutionHealthObservation(
        ExecutionHealthStatus health,
        ExecutionReadinessStatus readiness,
        DateTimeOffset observedAtUtc,
        ExecutionProvenance provenance,
        ImmutableArray<string> evidenceReferences = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(health))
        {
            throw new ArgumentOutOfRangeException(nameof(health), health, "Unsupported execution health.");
        }

        if (!Enum.IsDefined(readiness))
        {
            throw new ArgumentOutOfRangeException(nameof(readiness), readiness, "Unsupported execution readiness.");
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Execution-health observations must use UTC.", nameof(observedAtUtc));
        }

        Health = health;
        Readiness = readiness;
        ObservedAtUtc = observedAtUtc;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        EvidenceReferences = NormalizeReferences(evidenceReferences);
        Diagnostics = NormalizeDiagnostics(diagnostics);
    }

    /// <summary>Current operational health.</summary>
    public ExecutionHealthStatus Health { get; }

    /// <summary>Current ability to admit or continue work.</summary>
    public ExecutionReadinessStatus Readiness { get; }

    /// <summary>UTC time of the authoritative source observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Producer and source attribution for this projection.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Non-sensitive contributing evidence references in deterministic order.</summary>
    public ImmutableArray<string> EvidenceReferences { get; }

    /// <summary>Structured health diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    static ImmutableArray<string> NormalizeReferences(ImmutableArray<string> evidenceReferences)
    {
        if (evidenceReferences.IsDefaultOrEmpty)
        {
            return [];
        }

        if (evidenceReferences.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Health evidence references cannot be empty.", nameof(evidenceReferences));
        }

        var normalized = evidenceReferences
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalized.Length != evidenceReferences.Length)
        {
            throw new ArgumentException("Health evidence references cannot be duplicated.", nameof(evidenceReferences));
        }

        return normalized.SequenceEqual(evidenceReferences) ? evidenceReferences : normalized;
    }

    static ImmutableArray<DocumentValidationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        if (diagnostics.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Health diagnostics cannot contain null entries.", nameof(diagnostics));
        }

        return DocumentValidationDiagnostics.Normalize(diagnostics);
    }
}

/// <summary>Projects common execution status into attributable health without creating parallel mutable state.</summary>
public static class ExecutionHealthProjector
{
    /// <summary>Projects health and readiness from one safe execution-status observation.</summary>
    /// <param name="status">Existing safe execution-status authority.</param>
    /// <param name="provenance">Producer and source attribution for the projection.</param>
    /// <param name="evidenceReferences">Optional non-sensitive references to contributing runtime evidence.</param>
    /// <param name="diagnostics">Optional structured health diagnostics.</param>
    /// <returns>An immutable health observation at <see cref="ExecutionStatus.UpdatedAtUtc"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="status"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An evidence or diagnostic collection is malformed.</exception>
    public static ExecutionHealthObservation Project(
        ExecutionStatus status,
        ExecutionProvenance provenance,
        ImmutableArray<string> evidenceReferences = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(provenance);
        return new(
            status.Runtime.Health,
            GetReadiness(status),
            status.UpdatedAtUtc,
            provenance,
            evidenceReferences,
            diagnostics);
    }

    internal static ExecutionReadinessStatus GetReadiness(ExecutionStatus status)
    {
        if (status.Runtime.Health == ExecutionHealthStatus.Unknown)
        {
            return ExecutionReadinessStatus.Unknown;
        }

        if (status.Runtime.Health == ExecutionHealthStatus.Unhealthy
            || status.ControlMode != ProcessControlMode.Running
            || status.TerminalOutcome.Kind != ExecutionTerminalOutcomeKind.None)
        {
            return ExecutionReadinessStatus.NotReady;
        }
        return ExecutionReadinessStatus.Ready;
    }
}

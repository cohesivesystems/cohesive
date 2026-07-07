using System.Collections.Immutable;

namespace Cohesive.Relations.Model;

/// <summary>
/// Mapping metadata.
/// </summary>
public sealed record MappingMetadata
{
    /// <summary>
    /// Creates mapping metadata.
    /// </summary>
    public MappingMetadata(
        bool allowCodegen,
        bool deterministic,
        bool transportMapping,
        MappingExecutionPreference executionPreference,
        ImmutableDictionary<string, string>? hints = null
    )
    {
        AllowCodegen = allowCodegen;
        Deterministic = deterministic;
        TransportMapping = transportMapping;
        ExecutionPreference = executionPreference;
        Hints = hints ?? ImmutableDictionary<string, string>.Empty;
    }

    /// <summary>
    /// True when code-generation is allowed.
    /// </summary>
    public bool AllowCodegen { get; init; }

    /// <summary>
    /// True when mapping semantics are deterministic.
    /// </summary>
    public bool Deterministic { get; init; }

    /// <summary>
    /// True when mapping is intended for transport shapes.
    /// </summary>
    public bool TransportMapping { get; init; }

    /// <summary>
    /// Preferred mapping execution strategy.
    /// </summary>
    public MappingExecutionPreference ExecutionPreference { get; init; }

    /// <summary>
    /// Optional implementation hints.
    /// </summary>
    public ImmutableDictionary<string, string> Hints { get; init; }

    /// <summary>
    /// Deterministic default metadata.
    /// </summary>
    public static MappingMetadata Default { get; } = new(
        allowCodegen: false,
        deterministic: true,
        transportMapping: false,
        executionPreference: MappingExecutionPreference.InMemory);
}
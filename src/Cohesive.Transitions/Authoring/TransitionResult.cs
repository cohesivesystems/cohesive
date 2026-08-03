using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Entity state transition result including state delta and emitted effects.
/// </summary>
/// <remarks>
/// Compatibility result retained through ARI-218. Canonical execution returns <c>TransitionDecision</c> and projects
/// state only in an explicit commit interpretation.
/// </remarks>
public sealed record TransitionResult
{
    /// <summary>
    /// Creates a transition result.
    /// </summary>
    public TransitionResult(
        string TransitionName,
        EntityState OldState,
        EntityState NewState,
        IReadOnlyList<EffectRequest> Effects,
        long NewVersion,
        IReadOnlyList<string>? ReadFields = null,
        IReadOnlyList<string>? WriteFields = null,
        IReadOnlyList<string>? ChangedFields = null
        )
    {
        this.TransitionName = Guard.RequireNotNullOrWhiteSpace(TransitionName);
        this.OldState = Guard.RequireNotNull(OldState);
        this.NewState = Guard.RequireNotNull(NewState);
        this.Effects = Guard.RequireNotNull(Effects);
        this.NewVersion = NewVersion;
        this.ReadFields = NormalizeFieldNames(ReadFields);
        this.WriteFields = NormalizeFieldNames(WriteFields);
        this.ChangedFields = NormalizeFieldNames(ChangedFields);
    }

    /// <summary>
    /// Transition name.
    /// </summary>
    public string TransitionName { get; init; }

    /// <summary>
    /// Entity state before transition execution.
    /// </summary>
    public EntityState OldState { get; init; }

    /// <summary>
    /// Entity state after transition execution.
    /// </summary>
    public EntityState NewState { get; init; }

    /// <summary>
    /// Effect requests emitted by this transition.
    /// </summary>
    public IReadOnlyList<EffectRequest> Effects { get; init; }

    /// <summary>
    /// Entity version after transition execution.
    /// </summary>
    public long NewVersion { get; init; }

    /// <summary>
    /// Transition read-set field names resolved at runtime.
    /// </summary>
    public IReadOnlyList<string> ReadFields { get; init; }

    /// <summary>
    /// Transition write-set field names resolved at runtime.
    /// </summary>
    public IReadOnlyList<string> WriteFields { get; init; }

    /// <summary>
    /// Fields whose values changed between <see cref="OldState"/> and <see cref="NewState"/>.
    /// </summary>
    public IReadOnlyList<string> ChangedFields { get; init; }

    static IReadOnlyList<string> NormalizeFieldNames(IReadOnlyList<string>? fields)
    {
        return fields is null
            ? []
            : [.. fields
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)];
    }
}

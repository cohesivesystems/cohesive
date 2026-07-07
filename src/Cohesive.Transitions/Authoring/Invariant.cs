using Cohesive.Prelude;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Named predicate that must hold for valid entity state.
/// </summary>
/// <param name="name">Invariant name</param>
/// <param name="predicate">Invariant predicate</param>
sealed class Invariant(string name, Func<bool> predicate)
{
    readonly Func<bool> predicate = Guard.RequireNotNull(predicate);

    /// <summary>
    /// Invariant name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Evaluates the invariant predicate.
    /// </summary>
    public bool Holds() => predicate();
}

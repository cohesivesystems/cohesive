using Cohesive.Assurance;
using Cohesive.Execution;

namespace Cohesive.Examples.ReplayAssurance;

/// <summary>Shared reservation claim used by native observations and the bounded model interpretation.</summary>
public static class ReservationClaim
{
    /// <summary>Binds effect uniqueness to the exact compiled reservation transition.</summary>
    /// <param name="transition">Canonical transition revision and content identity.</param>
    /// <returns>The demo's single authoritative claim.</returns>
    /// <exception cref="ArgumentNullException">The transition is null.</exception>
    public static EffectUniquenessClaim Create(ExecutionDefinitionReference transition) =>
        new("reservation/at-most-one-commit", "1", transition);
}

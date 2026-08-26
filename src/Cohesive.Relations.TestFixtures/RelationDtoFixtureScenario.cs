using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Observation = Cohesive.Model.Observation;

namespace Cohesive.Relations.TestFixtures;

/// <summary>Deterministic canonical execution and equivalent observation inputs for one DTO-mapping scenario.</summary>
/// <typeparam name="TOutput">Expected CLR output type.</typeparam>
public sealed class RelationDtoFixtureScenario<TOutput>
{
    /// <summary>Creates a reusable DTO-mapping scenario.</summary>
    /// <param name="plan">Canonical compiled relation plan.</param>
    /// <param name="evidence">Runtime evidence interpreted for the scenario.</param>
    /// <param name="execution">Canonical interpretation of <paramref name="evidence"/>.</param>
    /// <param name="observations">
    /// Validated identity-free semantic equivalents of complete canonical output rows. Incomplete or suppressed
    /// rows remain available only through <paramref name="execution"/> because they are not complete observations.
    /// </param>
    /// <param name="expected">Expected CLR outputs for successful scenarios.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="evidence"/>, or <paramref name="execution"/> is
    /// <see langword="null"/>.
    /// </exception>
    public RelationDtoFixtureScenario(
        CompiledRelationQueryPlan plan,
        RelationQueryRuntimeEvidence evidence,
        RelationQueryExecutionResult execution,
        ImmutableArray<Observation> observations,
        ImmutableArray<TOutput> expected)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        Observations = observations.IsDefault ? [] : observations;
        Expected = expected.IsDefault ? [] : expected;
    }

    /// <summary>Canonical compiled relation plan.</summary>
    public CompiledRelationQueryPlan Plan { get; }

    /// <summary>Runtime evidence interpreted for the scenario.</summary>
    public RelationQueryRuntimeEvidence Evidence { get; }

    /// <summary>Canonical interpretation result consumed by relation-aware mappers.</summary>
    public RelationQueryExecutionResult Execution { get; }

    /// <summary>Validated identity-free semantic equivalents of complete canonical output rows.</summary>
    public ImmutableArray<Observation> Observations { get; }

    /// <summary>Expected CLR outputs for a successful scenario.</summary>
    public ImmutableArray<TOutput> Expected { get; }
}

/// <summary>Data variation used to exercise successful and diagnostic mapping paths.</summary>
public enum RelationDtoFixtureVariant
{
    /// <summary>Every required source value is present and type-compatible.</summary>
    Complete = 0,

    /// <summary>The required Customer traversal completes without a matching customer.</summary>
    MissingCustomer = 1,

    /// <summary>The Customer name is represented by an integer instead of its declared string type.</summary>
    InvalidCustomerName = 2
}

using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Definition-bound materialization impact plan whose complete semantic content has been reproduced by the
/// authoritative compilers.
/// </summary>
public sealed class MaterializationImpactPlanLinkage
{
    internal MaterializationImpactPlanLinkage(
        MaterializationImpactPlan plan,
        MaterializationDefinition definition,
        CompiledRelationQueryPlan relationPlan)
    {
        Plan = Guard.RequireNotNull(plan);
        Definition = Guard.RequireNotNull(definition);
        RelationPlan = Guard.RequireNotNull(relationPlan);
    }

    /// <summary>Fingerprint-verified materialization impact plan.</summary>
    public MaterializationImpactPlan Plan { get; }

    /// <summary>Exact canonical materialization definition reproduced by the plan.</summary>
    public MaterializationDefinition Definition { get; }

    /// <summary>Exact canonical Relations plan that owns every dependency and relationship reference.</summary>
    public CompiledRelationQueryPlan RelationPlan { get; }
}

/// <summary>
/// Reproduces a persisted impact plan from its canonical materialization definition before interpretation.
/// </summary>
public static class MaterializationImpactPlanLinker
{
    /// <summary>Links one impact plan to the exact materialization and Relations semantics that produced it.</summary>
    /// <param name="plan">Persisted or newly compiled impact plan.</param>
    /// <param name="definition">Canonical materialization definition claimed by the plan.</param>
    /// <returns>A definition-bound plan and its reproduced canonical Relations plan.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The plan is foreign, stale, cannot be reproduced, or differs from deterministic impact recompilation.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical content cannot be fingerprinted.</exception>
    public static MaterializationImpactPlanLinkage Link(
        MaterializationImpactPlan plan,
        MaterializationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(definition);
        var document = MaterializationDocument.FromDefinition(definition);
        if (plan.Materialization != definition.Id
            || !Equals(plan.DefinitionFingerprint, document.DefinitionFingerprint))
        {
            throw new ArgumentException(
                "The impact plan does not belong to the supplied materialization definition.",
                nameof(definition));
        }

        var relationCompilation = definition.Relation.Compile();
        if (!relationCompilation.IsSuccessful || relationCompilation.Plan is not { } relationPlan)
        {
            throw new ArgumentException(
                "The supplied materialization cannot reproduce its canonical Relations plan.",
                nameof(definition));
        }

        var impactCompilation = MaterializationImpactPlanCompiler.Compile(document, plan.Policy);
        if (!impactCompilation.IsSuccessful || impactCompilation.Plan is not { } reproduced)
        {
            throw new ArgumentException(
                "The supplied materialization cannot reproduce the persisted impact plan.",
                nameof(plan));
        }

        if (!Equals(plan.Fingerprint, reproduced.Fingerprint))
        {
            throw new ArgumentException(
                "The persisted impact plan differs from deterministic recompilation of its canonical definition.",
                nameof(plan));
        }

        return new(
            plan: plan,
            definition: definition,
            relationPlan: relationPlan);
    }
}

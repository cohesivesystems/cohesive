namespace Cohesive.Storage.Materialization;

/// <summary>Admitted materialization declaration and impact preparation, independent of native resources.</summary>
public sealed class PreparedMaterialization
{
    internal PreparedMaterialization(MaterializationDocument document, MaterializationImpactPlanningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Document = document;
        var impact = MaterializationImpactPlanCompiler.Compile(document, policy);
        if (impact.Plan is null) throw new ArgumentException(string.Join("; ", impact.Diagnostics), nameof(policy));
        Impact = impact.Plan;
    }

    /// <summary>Canonical requirements and retained relation authority.</summary>
    public MaterializationDocument Document { get; }
    /// <summary>Prepared contributor-to-root change routing.</summary>
    public MaterializationImpactPlan Impact { get; }
}

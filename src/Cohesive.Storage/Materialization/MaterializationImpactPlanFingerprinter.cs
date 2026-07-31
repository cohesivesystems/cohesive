using System.Security.Cryptography;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Computes deterministic content fingerprints for portable materialization impact plans.</summary>
public static class MaterializationImpactPlanFingerprinter
{
    /// <summary>Digest algorithm used by impact-plan fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by the v1 impact-plan fence.</summary>
    public const string Canonicalization = "cohesive-materialization-impact-plan/v1-c14n/v1";

    /// <summary>Computes a deterministic fingerprint of every execution-affecting plan field.</summary>
    /// <param name="plan">Normalized impact plan to fingerprint.</param>
    /// <returns>Versioned SHA-256 fingerprint excluding the self-referential fingerprint field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Plan content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Plan content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Plan content has no canonical JSON representation.</exception>
    public static MaterializationImpactPlanFingerprint Compute(MaterializationImpactPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                SchemaVersion: plan.SchemaVersion,
                Materialization: plan.Materialization,
                DefinitionFingerprint: plan.DefinitionFingerprint,
                RelationPlan: plan.RelationPlan,
                Output: plan.Output,
                Policy: plan.Policy,
                Routes: plan.Routes),
            RelationQueryJsonSerializer.CreateOptions());
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        MaterializationId Materialization,
        Cohesive.Execution.ExecutionDefinitionFingerprint DefinitionFingerprint,
        Cohesive.Relations.Compilation.RelationQueryCompiledPlanReference RelationPlan,
        Cohesive.Relations.Compilation.RelationQueryOutputReference Output,
        MaterializationImpactPlanningPolicy Policy,
        System.Collections.Immutable.ImmutableArray<MaterializationImpactRoute> Routes);
}

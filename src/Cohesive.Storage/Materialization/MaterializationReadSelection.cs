using System.Text.Json;

namespace Cohesive.Storage.Materialization;

/// <summary>Admission of trusted local comparison artifacts before selecting a materialized read generation.</summary>
public static class MaterializationReadSelection
{
    /// <summary>Loads and validates comparison evidence without opening native resources.</summary>
    /// <param name="path">Trusted operator artifact path; not a signed attestation.</param>
    /// <param name="definition">Exact materialization to serve.</param><param name="cancellationToken">Cancels file IO.</param>
    /// <returns>Fresh comparison evidence for the definition; native placement must still be checked during binding.</returns>
    public static async Task<MaterializationShadowComparison> LoadAsync(string path, PreparedMaterialization definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(definition);
        var comparison = JsonSerializer.Deserialize<MaterializationShadowComparison>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("Read comparison evidence is missing.");
        Validate(comparison, definition.Document, DateTimeOffset.UtcNow);
        return comparison;
    }

    /// <summary>Checks comparison success, definition affinity and startup freshness.</summary>
    /// <param name="comparison">Trusted local operator evidence.</param><param name="definition">Selected declaration.</param>
    /// <param name="now">Admission clock.</param>
    public static void Validate(MaterializationShadowComparison comparison, MaterializationDocument definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(comparison); ArgumentNullException.ThrowIfNull(definition);
        if (!comparison.Matches || comparison.LiveCount < 0 || comparison.LiveCount != comparison.ShadowCount
            || comparison.ObservedAtUtc > now || comparison.Convergence.Materialization != definition.Definition.Id
            || comparison.Convergence.DefinitionFingerprint != definition.DefinitionFingerprint
            || !comparison.Convergence.ValidateFreshness(now).IsValid)
            throw new InvalidOperationException("Read selection requires fresh matching comparison evidence for this exact definition.");
    }
}

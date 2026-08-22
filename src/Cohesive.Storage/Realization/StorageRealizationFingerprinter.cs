using System.Security.Cryptography;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Realization;

/// <summary>Computes deterministic portable-content fences for Storage Realization semantics and plans.</summary>
public static class StorageRealizationFingerprinter
{
    /// <summary>Cryptographic hash algorithm used by the current profiles.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile for canonical semantic storage structures.</summary>
    public const string StructureCanonicalization = "cohesive-storage-structure/v1-c14n/v1";

    /// <summary>Canonicalization profile for target-specific storage realizations.</summary>
    public const string TargetCanonicalization = "cohesive-storage-target-realization/v1-c14n/v1";

    /// <summary>Computes the complete canonical semantic-structure fingerprint.</summary>
    /// <param name="structure">Canonical semantic storage structure.</param>
    /// <returns>Versioned SHA-256 metadata fencing every structure field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="structure"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Content has no canonical JSON representation.</exception>
    public static ExecutionDefinitionFingerprint ComputeStructure(StorageStructureDefinition structure) =>
        Compute(Guard.RequireNotNull(structure), StructureCanonicalization);

    /// <summary>Computes the complete target-realization fingerprint.</summary>
    /// <param name="realization">Target-specific realization linked to one structure fingerprint.</param>
    /// <returns>Versioned SHA-256 metadata fencing every realization and evidence field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="realization"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Content has no canonical JSON representation.</exception>
    public static ExecutionDefinitionFingerprint ComputeTarget(StorageTargetRealization realization) =>
        Compute(Guard.RequireNotNull(realization), TargetCanonicalization);

    static ExecutionDefinitionFingerprint Compute<T>(T value, string canonicalization)
        where T : class
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(value, RelationQueryJsonSerializer.CreateOptions());
        return new(
            Algorithm,
            canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }
}

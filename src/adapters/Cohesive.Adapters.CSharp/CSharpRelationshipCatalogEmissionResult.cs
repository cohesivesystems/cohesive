using System.Collections.Immutable;
using Cohesive.CodeGen;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.CSharp;

/// <summary>
/// Result of validating and projecting an exact canonical relationship catalog into C# source.
/// </summary>
public sealed class CSharpRelationshipCatalogEmissionResult
{
    internal CSharpRelationshipCatalogEmissionResult(
        RelationshipCatalogFingerprint? catalogFingerprint,
        DocumentValidationResult validation,
        CodeEmission? emission)
    {
        CatalogFingerprint = catalogFingerprint;
        Validation = validation;
        Emission = emission;
    }

    /// <summary>
    /// Fingerprint of the exact relationship catalog supplied to the emitter, or
    /// <see langword="null"/> when catalog validation failed before fingerprinting.
    /// </summary>
    public RelationshipCatalogFingerprint? CatalogFingerprint { get; }

    /// <summary>
    /// Structured catalog and C# symbol validation result produced before source emission.
    /// </summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Validation diagnostics in deterministic validation order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>
    /// Generated C# documents, or <see langword="null"/> when validation reports an error.
    /// </summary>
    public CodeEmission? Emission { get; }

    /// <summary>
    /// Whether validation succeeded and a C# emission was produced.
    /// </summary>
    public bool Succeeded => Validation.IsValid && Emission is not null;
}

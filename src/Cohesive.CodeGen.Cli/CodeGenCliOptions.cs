using System.Collections.Immutable;
using Cohesive.Adapters.TypeScript;

namespace Cohesive.CodeGen.Cli;

/// <summary>Semantic representation used when deriving generated contract shapes.</summary>
public enum ContractShapeProjection
{
    /// <summary>Preserve CLR property names, wrappers, and enum values.</summary>
    Clr = 0,

    /// <summary>Project canonical JSON names, scalar wrappers, and string enum values.</summary>
    CanonicalJson = 1
}

/// <summary>
/// Parsed command-line options for the codegen CLI.
/// </summary>
public sealed record CodeGenCliOptions
{
    /// <summary>
    /// Path to the compiled contracts assembly.
    /// </summary>
    public required string ContractsAssemblyPath { get; init; }

    /// <summary>
    /// Output directory for generated artifacts.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Logical module name used for generated filenames.
    /// </summary>
    public required string ModuleName { get; init; }

    /// <summary>
    /// Artifact kinds to emit.
    /// </summary>
    public required ImmutableArray<CodeGenEmitKind> EmitKinds { get; init; }

    /// <summary>
    /// TypeScript modules that own declarations for external shape namespaces.
    /// </summary>
    public ImmutableArray<TypeScriptExternalTypeModule> ExternalTypeScriptShapeModules { get; init; } = [];

    /// <summary>Runtime TypeScript catalogs derived from named closed-union discriminator cases.</summary>
    public ImmutableArray<TypeScriptUnionDiscriminatorCatalog> TypeScriptUnionDiscriminatorCatalogs { get; init; } = [];

    /// <summary>Representation used to derive contract shapes.</summary>
    public ContractShapeProjection ShapeProjection { get; init; } = ContractShapeProjection.Clr;
}

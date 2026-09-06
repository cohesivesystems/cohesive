using Cohesive.Cli;
using Cohesive.Configuration;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;

namespace Cohesive.Simulation.Cli;

sealed record WorldManifestCliOptions
{
    [ConfigurationParameter(
        "world",
        Description = "Portable core world-definition JSON path, or '-' for standard input.")]
    public string WorldPath { get; init; } = string.Empty;

    [ConfigurationParameter(
        "relationship-world",
        Description = "Portable relationship-world JSON path, or '-' for standard input.")]
    public string RelationshipWorldPath { get; init; } = string.Empty;

    [ConfigurationParameter(
        "out",
        Description = "World-artifact manifest JSON path, or '-' for standard output.")]
    public string OutputPath { get; init; } = CommandIo.StandardStreamPath;

    [ConfigurationParameter(
        "seed",
        Description = "Deterministic signed 64-bit root seed.",
        Required = true)]
    public long RootSeed { get; init; }

    public bool IsRelationshipWorld => !string.IsNullOrEmpty(RelationshipWorldPath);

    public string InputPath => IsRelationshipWorld ? RelationshipWorldPath : WorldPath;
}

sealed record WorldProvisionCliOptions
{
    [ConfigurationParameter(
        "manifest",
        Description = "Verified world-artifact manifest JSON path, or '-' for standard input.",
        Required = true)]
    public string ManifestPath { get; init; } = string.Empty;

    [ConfigurationParameter(
        "out",
        Description = "Verified-manifest JSON Lines output path, or '-' for standard output.")]
    public string OutputPath { get; init; } = CommandIo.StandardStreamPath;

    [ConfigurationParameter(
        "target",
        Description = "Stable logical identity of this JSON Lines destination.",
        Required = true)]
    public string TargetId { get; init; } = string.Empty;

    [ConfigurationParameter(
        "batch-size",
        Description = "Positive provisioning batch size.")]
    public int BatchSize { get; init; } = WorldProvisioningOptions.DefaultBatchSize;
}

sealed record WorldVerifyCliOptions
{
    [ConfigurationParameter(
        "manifest",
        Description = "Verified world-artifact manifest JSON path, or '-' for standard input.",
        Required = true)]
    public string ManifestPath { get; init; } = string.Empty;

    [ConfigurationParameter(
        "jsonl",
        Description = "World JSON Lines path to verify, or '-' for standard input.",
        Required = true)]
    public string JsonLinesPath { get; init; } = string.Empty;
}

sealed record CatalogCliOptions;

sealed record CatalogVerifyCliOptions
{
    [ConfigurationParameter(
        "catalog",
        Description = "Generation-catalog document JSON path, or '-' for standard input.",
        Required = true)]
    public string CatalogPath { get; init; } = string.Empty;
}

sealed record WorldVerifyCliEvidence(
    string ArtifactId,
    string ArtifactManifestFingerprint,
    string WorldId,
    string WorldRevision,
    string WorldFingerprint,
    string Interpreter,
    string EntropyAlgorithm,
    string? TargetId,
    string? RunId,
    int? BatchSize,
    long ItemCount);

sealed record CliVerificationReport<TVerification>(
    string SchemaVersion,
    bool IsValid,
    TVerification? Verification,
    IReadOnlyList<DocumentValidationDiagnostic> Diagnostics)
    where TVerification : class;

static class CliVerificationReportSchemas
{
    public const string WorldArtifact = "cohesive-simulation-cli-verification/v1";

    public const string GenerationCatalog = "cohesive-simulation-cli-catalog-verification/v1";
}

sealed record CatalogVerifyCliEvidence(
    string CatalogSchemaVersion,
    string CatalogId,
    string CatalogRevision,
    string CatalogFingerprint,
    TypeRef ValueType,
    int EntryCount,
    GenerationCatalogProvenance Provenance);

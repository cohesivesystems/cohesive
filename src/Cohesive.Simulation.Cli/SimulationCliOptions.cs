using Cohesive.Configuration;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Provisioning;

namespace Cohesive.Simulation.Cli;

static class SimulationCliPaths
{
    public const string StandardStream = "-";
}

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
    public string OutputPath { get; init; } = SimulationCliPaths.StandardStream;

    [ConfigurationParameter(
        "seed",
        Description = "Deterministic signed 64-bit root seed.",
        Required = true)]
    public long RootSeed { get; init; }

    public bool IsRelationshipWorld => !string.IsNullOrEmpty(RelationshipWorldPath);

    public string InputPath => IsRelationshipWorld ? RelationshipWorldPath : WorldPath;

    public bool ReadsStandardInput => string.Equals(
        InputPath,
        SimulationCliPaths.StandardStream,
        StringComparison.Ordinal);

    public bool WritesStandardOutput => string.Equals(
        OutputPath,
        SimulationCliPaths.StandardStream,
        StringComparison.Ordinal);
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
    public string OutputPath { get; init; } = SimulationCliPaths.StandardStream;

    [ConfigurationParameter(
        "target",
        Description = "Stable logical identity of this JSON Lines destination.",
        Required = true)]
    public string TargetId { get; init; } = string.Empty;

    [ConfigurationParameter(
        "batch-size",
        Description = "Positive provisioning batch size.")]
    public int BatchSize { get; init; } = WorldProvisioningOptions.DefaultBatchSize;

    public bool ReadsStandardInput => string.Equals(
        ManifestPath,
        SimulationCliPaths.StandardStream,
        StringComparison.Ordinal);

    public bool WritesStandardOutput => string.Equals(
        OutputPath,
        SimulationCliPaths.StandardStream,
        StringComparison.Ordinal);
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

    public bool ReadsManifestStandardInput => string.Equals(
        ManifestPath,
        SimulationCliPaths.StandardStream,
        StringComparison.Ordinal);

    public bool ReadsJsonLinesStandardInput => string.Equals(
        JsonLinesPath,
        SimulationCliPaths.StandardStream,
        StringComparison.Ordinal);
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

sealed record WorldVerifyCliReport(
    string SchemaVersion,
    bool IsValid,
    WorldVerifyCliEvidence? Verification,
    IReadOnlyList<DocumentValidationDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "cohesive-simulation-cli-verification/v1";
}

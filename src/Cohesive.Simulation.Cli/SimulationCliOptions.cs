using Cohesive.Configuration;
using Cohesive.Simulation.Provisioning;

namespace Cohesive.Simulation.Cli;

sealed record SimulationCliOptions
{
    public const string StandardStreamPath = "-";

    [ConfigurationParameter(
        "world",
        Description = "Portable world-definition JSON path, or '-' for standard input.",
        Required = true)]
    public string WorldPath { get; init; } = string.Empty;

    [ConfigurationParameter(
        "out",
        Description = "JSON Lines output path, or '-' for standard output.")]
    public string OutputPath { get; init; } = StandardStreamPath;

    [ConfigurationParameter(
        "target",
        Description = "Stable logical identity of this JSON Lines destination.",
        Required = true)]
    public string TargetId { get; init; } = string.Empty;

    [ConfigurationParameter(
        "seed",
        Description = "Deterministic signed 64-bit root seed.",
        Required = true)]
    public long RootSeed { get; init; }

    [ConfigurationParameter(
        "batch-size",
        Description = "Positive provisioning batch size.")]
    public int BatchSize { get; init; } = WorldProvisioningOptions.DefaultBatchSize;

    public bool ReadsStandardInput => string.Equals(
        WorldPath,
        StandardStreamPath,
        StringComparison.Ordinal);

    public bool WritesStandardOutput => string.Equals(
        OutputPath,
        StandardStreamPath,
        StringComparison.Ordinal);
}

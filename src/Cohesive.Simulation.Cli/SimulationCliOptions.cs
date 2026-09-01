using Cohesive.Simulation.Provisioning;

namespace Cohesive.Simulation.Cli;

sealed record SimulationCliOptions(
    string WorldPath,
    string OutputPath,
    string TargetId,
    long RootSeed,
    int BatchSize = WorldProvisioningOptions.DefaultBatchSize)
{
    public const string StandardStreamPath = "-";

    public bool ReadsStandardInput => string.Equals(
        WorldPath,
        StandardStreamPath,
        StringComparison.Ordinal);

    public bool WritesStandardOutput => string.Equals(
        OutputPath,
        StandardStreamPath,
        StringComparison.Ordinal);
}

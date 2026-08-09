using Cohesive.Execution;

namespace Cohesive.Processes.Distribution;

/// <summary>Stable authority, schema, and wire identities for portable Process distribution.</summary>
public static class ProcessDistributionWireNames
{
    /// <summary>Canonical semantic authority that owns portable Process distribution.</summary>
    public const string SemanticAuthority = "cohesive.processes.distribution";

    /// <summary>Current portable distribution document schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-distribution/v1");
}

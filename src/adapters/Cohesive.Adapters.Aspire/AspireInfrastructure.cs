using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Aspire;

/// <summary>Adapter-owned infrastructure contracts for native Aspire project execution.</summary>
public static class AspireInfrastructure
{
    /// <summary>Execution of a .NET project; no durability, scheduling or live readiness is implied.</summary>
    public static InfrastructureCapabilityId DotNetProjectExecution { get; } = new("aspire/dotnet-project-execution/v1");

    internal static InfrastructureTargetImplementation DotNetProject { get; } = InfrastructureTargetImplementation.WorkloadImplementation(
        new("aspire/dotnet-project/v1"), new InfrastructureCapabilityEvidence(new("aspire/dotnet-project/execution/v1"),
            DotNetProjectExecution, CapabilityRealizationKind.Native,
            sourceReferences: [SourceReference.Create("adapter", "cohesive.adapters.aspire/project-metadata/v1")]));
}

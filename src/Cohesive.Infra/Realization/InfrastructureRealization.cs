using System.Text.Json.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>
/// First-slice realization candidate for one exact infrastructure definition.
/// </summary>
/// <remarks>
/// The record combines target-profile capability planning with a validated lifecycle ownership partition. It is not
/// deployment authority: this slice does not yet bind reusable target strategies to selected physical instances,
/// elaborate every binding contract, or retain backend preview and deployment receipts.
/// </remarks>
public sealed record InfrastructureRealization
{
    /// <summary>Creates an infrastructure realization.</summary>
    /// <param name="capabilityClosure">Graph-wide capability-closure report.</param>
    /// <param name="lifecycle">Physical resource identities and lifecycle ownership partition.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="capabilityClosure"/> or <paramref name="lifecycle"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The inputs do not reference the same exact infrastructure definition.</exception>
    [JsonConstructor]
    public InfrastructureRealization(
        InfrastructureCapabilityClosureReport capabilityClosure,
        InfrastructureLifecyclePlan lifecycle)
    {
        CapabilityClosure = Guard.RequireNotNull(capabilityClosure);
        Lifecycle = Guard.RequireNotNull(lifecycle);
        if (CapabilityClosure.Definition != Lifecycle.Definition)
        {
            throw new ArgumentException(
                "Infrastructure capability closure and lifecycle ownership must reference the same exact definition.",
                nameof(lifecycle));
        }
    }

    /// <summary>Graph-wide capability-closure report.</summary>
    public InfrastructureCapabilityClosureReport CapabilityClosure { get; }

    /// <summary>Physical resource identities and lifecycle ownership partition.</summary>
    public InfrastructureLifecyclePlan Lifecycle { get; }

}

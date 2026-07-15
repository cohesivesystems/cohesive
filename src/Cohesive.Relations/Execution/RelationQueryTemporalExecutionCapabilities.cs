using System.Collections.Immutable;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Immutable declaration of valid-time join semantics supported by an execution target.
/// </summary>
public sealed class RelationQueryTemporalExecutionCapabilityProfile
{
    readonly ImmutableHashSet<RelationQueryTemporalExecutionCapability> supportedCapabilitySet;

    /// <summary>An execution profile that supports no temporal-join semantics.</summary>
    public static RelationQueryTemporalExecutionCapabilityProfile None { get; } = new();

    /// <summary>An execution profile that supports every canonical temporal-join semantic.</summary>
    public static RelationQueryTemporalExecutionCapabilityProfile All { get; } =
        new(
        [
            RelationQueryTemporalExecutionCapability.PointInInterval,
            RelationQueryTemporalExecutionCapability.IntervalOverlap,
            RelationQueryTemporalExecutionCapability.InclusiveBoundary,
            RelationQueryTemporalExecutionCapability.ExclusiveBoundary,
            RelationQueryTemporalExecutionCapability.UnboundedBoundary,
            RelationQueryTemporalExecutionCapability.NullAsUnbounded,
            RelationQueryTemporalExecutionCapability.DateDomain,
            RelationQueryTemporalExecutionCapability.DateTimeDomain,
            RelationQueryTemporalExecutionCapability.InstantDomain,
            RelationQueryTemporalExecutionCapability.PreserveAllMatches,
            RelationQueryTemporalExecutionCapability.InnerJoin,
            RelationQueryTemporalExecutionCapability.LeftOuterJoin,
            RelationQueryTemporalExecutionCapability.RightOuterJoin,
            RelationQueryTemporalExecutionCapability.FullOuterJoin,
            RelationQueryTemporalExecutionCapability.ValidateIntervals,
            RelationQueryTemporalExecutionCapability.InconclusiveEvidence
        ]);

    /// <summary>Creates a temporal execution capability profile.</summary>
    /// <param name="supportedCapabilities">
    /// Temporal-join semantics supported by the target, or <see langword="null"/> for an empty profile.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="supportedCapabilities"/> contains an unsupported capability value.
    /// </exception>
    public RelationQueryTemporalExecutionCapabilityProfile(
        IEnumerable<RelationQueryTemporalExecutionCapability>? supportedCapabilities = null)
    {
        var capabilities = supportedCapabilities is null
            ? []
            : supportedCapabilities.ToImmutableArray();
        var unsupported = capabilities
            .Where(static capability => !Enum.IsDefined(capability))
            .Select(static capability => (RelationQueryTemporalExecutionCapability?)capability)
            .FirstOrDefault();
        if (unsupported is { } unsupportedValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(supportedCapabilities),
                unsupportedValue,
                "Unsupported temporal execution capability.");
        }

        SupportedCapabilities =
        [
            .. capabilities
                .Distinct()
                .OrderBy(static capability => (int)capability)
        ];
        supportedCapabilitySet = SupportedCapabilities.ToImmutableHashSet();
    }

    /// <summary>Supported temporal-join semantics in stable capability order.</summary>
    public ImmutableArray<RelationQueryTemporalExecutionCapability> SupportedCapabilities { get; }

    /// <summary>Tests whether this profile supports one temporal-join semantic.</summary>
    /// <param name="capability">Temporal execution capability to test.</param>
    /// <returns><see langword="true"/> when the capability is supported; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is unsupported.</exception>
    public bool Supports(RelationQueryTemporalExecutionCapability capability)
    {
        if (!Enum.IsDefined(capability))
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported temporal execution capability.");

        return supportedCapabilitySet.Contains(capability);
    }
}

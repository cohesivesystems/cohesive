using System.Text.Json.Serialization;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Initial position used only when a managed Cosmos processor has no durable lease checkpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CosmosManagedMaterializationInitialPosition
{
    /// <summary>Start at the beginning of the retained latest-version change feed.</summary>
    Beginning = 0,

    /// <summary>Start at the current provider boundary and observe only subsequent changes.</summary>
    Current = 1,

    /// <summary>Start at an explicit UTC provider time supplied by the policy.</summary>
    AtTime = 2
}

/// <summary>Explicit execution policy for one Cosmos managed materialization change processor.</summary>
/// <remarks>
/// <see cref="MaximumProviderPageItems"/> and <see cref="MaximumLagStateItems"/> are SDK request hints, not hard
/// semantic bounds. Cosmos may deliver a larger transactional batch. Consequently the managed source advertises no
/// hard callback item or byte limit.
/// </remarks>
public sealed record CosmosManagedMaterializationChangeSourcePolicy
{
    /// <summary>Conventional SDK change-feed polling interval.</summary>
    public static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Conventional SDK maximum-item hint for one latest-version callback.</summary>
    public const int DefaultMaximumProviderPageItems = 1_000;

    /// <summary>Conventional maximum estimator states requested per SDK response.</summary>
    public const int DefaultMaximumLagStateItems = 100;

    /// <summary>Conventional maximum encoded managed-position characters.</summary>
    public const int DefaultMaximumPositionCharacters = 4 * 1024 * 1024;

    /// <summary>Creates explicit managed processor policy.</summary>
    /// <param name="processorName">
    /// Stable caller deployment seed. The adapter combines it with source, plan, placement, and binding identity to
    /// form a lease-store- and initial-boundary-specific namespace, then adds the managed request's materialization,
    /// definition fingerprint, and generation to the effective processor name. Distinct executions therefore cannot
    /// co-own the same leases.
    /// </param>
    /// <param name="instanceName">
    /// Ephemeral worker-owner name. It controls lease ownership only and never participates in semantic scope,
    /// positions, change identities, or delivery identities.
    /// </param>
    /// <param name="initialPosition">Initial position used only before the first durable provider checkpoint.</param>
    /// <param name="initialTimeUtc">
    /// Explicit UTC start time required only when <paramref name="initialPosition"/> is
    /// <see cref="CosmosManagedMaterializationInitialPosition.AtTime"/>.
    /// </param>
    /// <param name="pollInterval">Positive SDK polling interval.</param>
    /// <param name="maximumProviderPageItems">
    /// Positive SDK callback item-count hint; Cosmos may exceed it for one transaction.
    /// </param>
    /// <param name="maximumLagStateItems">Positive estimator response item-count hint.</param>
    /// <param name="maximumPositionCharacters">Positive encoded managed-position size bound.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="processorName"/> or <paramref name="instanceName"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="processorName"/> or <paramref name="instanceName"/> is empty, the initial-time presence
    /// conflicts with <paramref name="initialPosition"/>, or <paramref name="initialTimeUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="initialPosition"/> is unsupported, a duration is not positive, or a numeric bound is not
    /// positive.
    /// </exception>
    public CosmosManagedMaterializationChangeSourcePolicy(
        string processorName,
        string instanceName,
        CosmosManagedMaterializationInitialPosition initialPosition = CosmosManagedMaterializationInitialPosition.Current,
        DateTimeOffset? initialTimeUtc = null,
        TimeSpan? pollInterval = null,
        int maximumProviderPageItems = DefaultMaximumProviderPageItems,
        int maximumLagStateItems = DefaultMaximumLagStateItems,
        int maximumPositionCharacters = DefaultMaximumPositionCharacters)
    {
        ProcessorName = Guard.RequireNotNullOrWhiteSpace(processorName);
        InstanceName = Guard.RequireNotNullOrWhiteSpace(instanceName);
        if (!Enum.IsDefined(initialPosition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialPosition),
                initialPosition,
                "Unsupported managed Cosmos initial position.");
        }

        if ((initialPosition == CosmosManagedMaterializationInitialPosition.AtTime) != initialTimeUtc.HasValue)
        {
            throw new ArgumentException(
                "A managed Cosmos AtTime initial position requires exactly one UTC initial time; other initial positions must omit it.",
                nameof(initialTimeUtc));
        }

        if (initialTimeUtc is { Offset: not { Ticks: 0 } })
        {
            throw new ArgumentException(
                "A managed Cosmos initial time must be UTC.",
                nameof(initialTimeUtc));
        }

        var effectivePollInterval = pollInterval ?? DefaultPollInterval;
        if (effectivePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                effectivePollInterval,
                "A managed Cosmos polling interval must be positive.");
        }

        if (maximumProviderPageItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumProviderPageItems),
                maximumProviderPageItems,
                "A managed Cosmos page-size hint must be positive.");
        }

        if (maximumLagStateItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLagStateItems),
                maximumLagStateItems,
                "A managed Cosmos lag-state page-size hint must be positive.");
        }

        if (maximumPositionCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPositionCharacters),
                maximumPositionCharacters,
                "A managed Cosmos position-size bound must be positive.");
        }

        InitialPosition = initialPosition;
        InitialTimeUtc = initialTimeUtc;
        PollInterval = effectivePollInterval;
        MaximumProviderPageItems = maximumProviderPageItems;
        MaximumLagStateItems = maximumLagStateItems;
        MaximumPositionCharacters = maximumPositionCharacters;
    }

    /// <summary>Stable caller deployment seed used to derive the binding and request-specific processor name.</summary>
    public string ProcessorName { get; }

    /// <summary>Ephemeral worker-owner name excluded from all semantic identities.</summary>
    public string InstanceName { get; }

    /// <summary>Initial position used only when no provider lease checkpoint exists.</summary>
    public CosmosManagedMaterializationInitialPosition InitialPosition { get; }

    /// <summary>Explicit UTC initial time when <see cref="InitialPosition"/> is <c>AtTime</c>.</summary>
    public DateTimeOffset? InitialTimeUtc { get; }

    /// <summary>SDK polling interval.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>SDK callback item-count hint, not a hard semantic bound.</summary>
    public int MaximumProviderPageItems { get; }

    /// <summary>SDK estimator response item-count hint.</summary>
    public int MaximumLagStateItems { get; }

    /// <summary>Maximum encoded managed-position characters accepted or produced.</summary>
    public int MaximumPositionCharacters { get; }
}

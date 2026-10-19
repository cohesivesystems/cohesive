using JasperFx.Events;
using Marten;

namespace Cohesive.Adapters.Marten;

/// <summary>Native Marten setup for Cohesive's retained entity actions.</summary>
public static class MartenEntityHistoryConfigurationExtensions
{
    /// <summary>Registers the lossless action envelope and string stream identities without migrating a database.</summary>
    /// <param name="options">Caller-owned native options, before constructing the store.</param>
    /// <returns>The same options for further native configuration.</returns>
    public static StoreOptions UseCohesiveEntityHistory(this StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Events.StreamIdentity = StreamIdentity.AsString;
        options.Events.AddEventType<EntityStateEvent>();
        return options;
    }
}

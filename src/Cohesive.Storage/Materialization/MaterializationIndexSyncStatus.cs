using Cohesive.Execution;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable Storage-owned wire coordinates for the index-synchronization runtime-status extension.</summary>
public static class MaterializationIndexSyncStatusWireNames
{
    /// <summary>Stable authority and extension identity for index-synchronization status.</summary>
    public const string SemanticAuthority = "cohesive.storage.index-sync.status";

    /// <summary>Exact v1 portable payload schema version.</summary>
    public const string CurrentSchemaVersion = "index-sync-status/v1";

    /// <summary>Gets the typed execution-extension identity derived from <see cref="SemanticAuthority"/>.</summary>
    public static ExecutionExtensionId ExtensionId { get; } = new(SemanticAuthority);

    /// <summary>Gets the typed extension schema version derived from <see cref="CurrentSchemaVersion"/>.</summary>
    public static ExecutionExtensionSchemaVersion SchemaVersion { get; } = new(CurrentSchemaVersion);

    /// <summary>Projects the canonical status semantic path for one exact backend-pool IR declaration.</summary>
    /// <param name="definition">Canonical backend-pool definition owning the status instance.</param>
    /// <returns>
    /// A stable path derived from the backend IR's materialization and pool identities, never from adapter names.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static ExecutionSemanticPath PoolStatusPath(MaterializationBackendPoolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(
        [
            "materializations",
            definition.MaterializationId.Value,
            "backendPools",
            definition.Id.Value,
            "indexSyncStatus"
        ]);
    }
}

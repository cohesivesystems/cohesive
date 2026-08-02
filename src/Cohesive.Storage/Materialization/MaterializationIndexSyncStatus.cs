using Cohesive.Execution;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable Storage-owned wire coordinates for the index-synchronization runtime-status extension.</summary>
public static class MaterializationIndexSyncStatusWireNames
{
    /// <summary>Stable authority and extension identity for index-synchronization status.</summary>
    public const string SemanticAuthority = "cohesive.storage.index-sync.status";

    /// <summary>
    /// Exact v3 portable payload schema version, including attributable adaptive Control state and placement scope.
    /// </summary>
    public const string CurrentSchemaVersion = "index-sync-status/v3";

    /// <summary>Gets the typed execution-extension identity derived from <see cref="SemanticAuthority"/>.</summary>
    public static ExecutionExtensionId ExtensionId { get; } = new(SemanticAuthority);

    /// <summary>Gets the typed extension schema version derived from <see cref="CurrentSchemaVersion"/>.</summary>
    public static ExecutionExtensionSchemaVersion SchemaVersion { get; } = new(CurrentSchemaVersion);

    /// <summary>Projects the canonical status semantic path for one exact placement authority.</summary>
    /// <param name="placementSlice">Exact placement slice owning the status instance.</param>
    /// <returns>
    /// A stable path derived from the placement IR's materialization, pool, slice identity, and content fingerprint,
    /// never from adapter names.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="placementSlice"/> is <see langword="null"/>.</exception>
    public static ExecutionSemanticPath PlacementStatusPath(MaterializationPlacementSliceReference placementSlice)
    {
        ArgumentNullException.ThrowIfNull(placementSlice);
        return new(
        [
            "materializations",
            placementSlice.Materialization.Materialization.Value,
            "backendPools",
            placementSlice.Pool.Pool.Value,
            "placementSlices",
            placementSlice.Id.Value,
            "fingerprints",
            placementSlice.Fingerprint.Algorithm,
            placementSlice.Fingerprint.Canonicalization,
            placementSlice.Fingerprint.Value,
            "indexSyncStatus"
        ]);
    }
}

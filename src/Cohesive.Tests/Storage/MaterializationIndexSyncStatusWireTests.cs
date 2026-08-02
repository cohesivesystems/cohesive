using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationIndexSyncStatusWireTests
{
    [Fact]
    public void WireCoordinates_DeriveTypedIdentityAndExactPlacementPathFromCanonicalIr()
    {
        var member = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [member],
            defaultTarget: member.Id,
            poolId: "pool/search");
        var document = MaterializationBackendPoolDocument.FromDefinition(definition);
        var placementSlice = MaterializationPlacementSliceReference.Create(
            materialization: new(
                MaterializationDefinitionReference.CurrentSchemaVersion,
                definition.MaterializationId,
                definition.DefinitionFingerprint),
            membership: new(
                algorithm: "sha256",
                canonicalization: "tests/index-sync-status-wire-membership/v1",
                value: new string('d', 64)),
            pool: MaterializationBackendPoolReference.FromDocument(document),
            target: member.Id,
            subjects: [new("placement/status")]);

        var path = MaterializationIndexSyncStatusWireNames.PlacementStatusPath(placementSlice);

        Assert.Equal(
            MaterializationIndexSyncStatusWireNames.SemanticAuthority,
            MaterializationIndexSyncStatusWireNames.ExtensionId.Value);
        Assert.Equal(
            MaterializationIndexSyncStatusWireNames.CurrentSchemaVersion,
            MaterializationIndexSyncStatusWireNames.SchemaVersion.Value);
        Assert.True(path.Segments.SequenceEqual(
        [
            "materializations",
            "materialization/backend-pool",
            "backendPools",
            "pool/search",
            "placementSlices",
            placementSlice.Id.Value,
            "fingerprints",
            placementSlice.Fingerprint.Algorithm,
            placementSlice.Fingerprint.Canonicalization,
            placementSlice.Fingerprint.Value,
            "indexSyncStatus"
        ]));
    }
}

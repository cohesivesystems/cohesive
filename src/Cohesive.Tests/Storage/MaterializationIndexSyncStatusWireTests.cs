using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationIndexSyncStatusWireTests
{
    [Fact]
    public void WireCoordinates_DeriveTypedIdentityAndPoolPathFromCanonicalBackendIr()
    {
        var member = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [member],
            defaultTarget: member.Id,
            poolId: "pool/search");

        var path = MaterializationIndexSyncStatusWireNames.PoolStatusPath(definition);

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
            "indexSyncStatus"
        ]));
    }
}

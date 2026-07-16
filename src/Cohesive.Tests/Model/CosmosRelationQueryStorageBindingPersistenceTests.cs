using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Model;

public sealed class CosmosRelationQueryStorageBindingPersistenceTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void JsonRoundTrip_RehydratesEquivalentNormalizedBinding()
    {
        var binding = CreateBinding();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<CosmosRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.SchemaVersion, rehydrated.SchemaVersion);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.Id, rehydrated.Id);
        Assert.Equal(binding.Source, rehydrated.Source);
        Assert.Equal(binding.PlacementBinding, rehydrated.PlacementBinding);
        Assert.Equal(binding.Target, rehydrated.Target);
        Assert.Equal(binding.TargetProfile, rehydrated.TargetProfile);
        Assert.Equal(binding.ContainerName, rehydrated.ContainerName);
        Assert.Equal(binding.RootAlias, rehydrated.RootAlias);
        Assert.Equal(binding.DocumentRoot, rehydrated.DocumentRoot);
        Assert.Equal(binding.IdentityPath, rehydrated.IdentityPath);
        Assert.Equal(binding.PartitionPath, rehydrated.PartitionPath);
        Assert.Equal(binding.MaximumInputRows, rehydrated.MaximumInputRows);
        Assert.Equal(binding.MissingValueEncoding, rehydrated.MissingValueEncoding);
        Assert.Equal(binding.NullValueEncoding, rehydrated.NullValueEncoding);
        Assert.Equal(binding.Origin, rehydrated.Origin);
        Assert.Equal(binding.ConventionSetVersion, rehydrated.ConventionSetVersion);
        Assert.Equal(binding.Fields.ToArray(), rehydrated.Fields.ToArray());
        Assert.Equal(binding.StableUniqueOrderingPaths.ToArray(), rehydrated.StableUniqueOrderingPaths.ToArray());
        Assert.Equal(binding.ExactOrderingPaths.ToArray(), rehydrated.ExactOrderingPaths.ToArray());
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));
    }

    [Fact]
    public void JsonRehydration_NormalizesEquivalentPersistedFactOrderingBeforeVerification()
    {
        var binding = CreateBinding();
        var document = SerializeToObject(binding);
        Reverse(document["fields"]!.AsArray());
        Reverse(document["stableUniqueOrderingPaths"]!.AsArray());
        Reverse(document["exactOrderingPaths"]!.AsArray());

        var rehydrated = Deserialize(document);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.Fields.ToArray(), rehydrated.Fields.ToArray());
        Assert.Equal(binding.StableUniqueOrderingPaths.ToArray(), rehydrated.StableUniqueOrderingPaths.ToArray());
        Assert.Equal(binding.ExactOrderingPaths.ToArray(), rehydrated.ExactOrderingPaths.ToArray());
    }

    [Fact]
    public void JsonRehydration_RejectsUnsupportedPersistedSchemaVersion()
    {
        var document = SerializeToObject(CreateBinding());
        document["schemaVersion"] = "cohesive.relations.cosmos-binding/v0";

        var exception = Assert.Throws<ArgumentException>(() => Deserialize(document));

        Assert.Contains("Unsupported Cosmos relation/query storage-binding schema version", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRehydration_RejectsFactsTamperedAfterFingerprinting()
    {
        var document = SerializeToObject(CreateBinding());
        document["containerName"] = "tampered-loads";

        var exception = Assert.Throws<ArgumentException>(() => Deserialize(document));

        Assert.Contains("fingerprint does not match normalized content", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRehydration_RequiresPersistedFingerprint()
    {
        var document = SerializeToObject(CreateBinding());
        document.Remove("fingerprint");

        var exception = Assert.Throws<ArgumentNullException>(() => Deserialize(document));

        Assert.Contains("fingerprint", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    static CosmosRelationQueryStorageBinding CreateBinding() => new(
        id: new("load-search/v1"),
        source: new RelationQuerySourceInstanceId("loads-source"),
        placementBinding: new RelationQuerySourcePlacementBindingId("loads-placement"),
        target: new RelationQueryTargetId("cosmos"),
        targetProfile: new RelationQueryTargetProfileId("cosmos-query/v1"),
        containerName: "loads",
        rootAlias: "c",
        identityPath: FieldPath.Parse("id"),
        fields:
        [
            new(new RelationQueryInputId("field:status"), FieldPath.Parse("status")),
            new(new RelationQueryInputId("field:id"), FieldPath.Parse("id")),
            new(
                new RelationQueryInputId("field:item-name"),
                new FieldPath(
                [
                    FieldPathSegment.ForField("items"),
                    FieldPathSegment.Element(),
                    FieldPathSegment.ForField("name")
                ]))
        ],
        documentRoot: FieldPath.Parse("payload"),
        partitionPath: FieldPath.Parse("tenantId"),
        stableUniqueOrderingPaths:
        [
            FieldPath.Parse("status"),
            FieldPath.Parse("id")
        ],
        exactOrderingPaths:
        [
            FieldPath.Parse("status"),
            FieldPath.Parse("id")
        ],
        maximumInputRows: 10_000,
        origin: CosmosRelationQueryBindingOrigin.Convention,
        conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet);

    static JsonObject SerializeToObject(CosmosRelationQueryStorageBinding binding) =>
        JsonNode.Parse(JsonSerializer.Serialize(binding, JsonOptions))!.AsObject();

    static CosmosRelationQueryStorageBinding? Deserialize(JsonObject document) =>
        JsonSerializer.Deserialize<CosmosRelationQueryStorageBinding>(document.ToJsonString(), JsonOptions);

    static void Reverse(JsonArray values)
    {
        var reversed = values.Select(static value => value?.DeepClone()).Reverse().ToArray();
        values.Clear();
        foreach (var value in reversed)
            values.Add(value);
    }
}

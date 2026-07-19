using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Cosmos;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Model;

public sealed class CosmosRelationQueryStorageBindingPersistenceTests
{
    static readonly Uri AccountEndpoint = new("https://localhost:8081/");
    const string DatabaseName = "operations";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly RelationQueryPlanComponentFingerprint CompiledPlanFingerprint = new(
        "sha256",
        "tests/compiled-plan/v1",
        "compiled-plan");
    static readonly RelationQuerySourcePlacementFingerprint PlacementFingerprint = new(
        "sha256",
        "tests/source-placement/v1",
        "source-placement");

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
        Assert.Equal(binding.AccountEndpoint, rehydrated.AccountEndpoint);
        Assert.Equal(binding.DatabaseName, rehydrated.DatabaseName);
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
        Assert.Equal(binding.ConfigurationDecisions.ToArray(), rehydrated.ConfigurationDecisions.ToArray());
        Assert.Equal(binding.CompiledPlanFingerprint, rehydrated.CompiledPlanFingerprint);
        Assert.Equal(binding.PlacementFingerprint, rehydrated.PlacementFingerprint);
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
        var collectionField = document["fields"]!.AsArray()
            .Select(static field => field!.AsObject())
            .Single(static field => field["input"]!.GetValue<string>() == "field:stops");
        Reverse(collectionField["collectionScope"]!["childFields"]!.AsArray());
        Reverse(document["stableUniqueOrderingPaths"]!.AsArray());
        Reverse(document["exactOrderingPaths"]!.AsArray());
        Reverse(document["configurationDecisions"]!.AsArray());
        document["accountEndpoint"] = "HTTPS://LOCALHOST:8081";

        var rehydrated = Deserialize(document);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(binding.Fields.ToArray(), rehydrated.Fields.ToArray());
        Assert.Equal(binding.StableUniqueOrderingPaths.ToArray(), rehydrated.StableUniqueOrderingPaths.ToArray());
        Assert.Equal(binding.ExactOrderingPaths.ToArray(), rehydrated.ExactOrderingPaths.ToArray());
        Assert.Equal(binding.ConfigurationDecisions.ToArray(), rehydrated.ConfigurationDecisions.ToArray());
    }

    [Fact]
    public void BindingFingerprint_ChangesWithConfigurationDecisionProvenance()
    {
        var convention = CreateBinding();
        var explicitBinding = CreateBinding(RelationQueryConfigurationValueOrigin.Explicit);

        Assert.NotEqual(convention.Fingerprint, explicitBinding.Fingerprint);
    }

    [Fact]
    public void BindingAffinity_IsPairedFingerprintContentWhileOmissionRemainsAnUnverifiedEscapeHatch()
    {
        var verified = CreateBinding();
        var unverified = CreateBinding(includeAffinity: false);
        var missingPlacement = SerializeToObject(verified);
        missingPlacement.Remove("placementFingerprint");

        Assert.Equal(CompiledPlanFingerprint, verified.CompiledPlanFingerprint);
        Assert.Equal(PlacementFingerprint, verified.PlacementFingerprint);
        Assert.Null(unverified.CompiledPlanFingerprint);
        Assert.Null(unverified.PlacementFingerprint);
        Assert.NotEqual(verified.Fingerprint, unverified.Fingerprint);
        var exception = Assert.Throws<ArgumentException>(() => Deserialize(missingPlacement));
        Assert.Contains("must be supplied together", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRehydration_RejectsForeignSettingsAndConventionOriginWithConsumerProvenance()
    {
        var foreign = SerializeToObject(CreateBinding());
        foreign["configurationDecisions"]!.AsArray()[0]!["setting"] = "elasticOnlySetting";
        var foreignException = Assert.Throws<ArgumentException>(() => Deserialize(foreign));

        var convention = SerializeToObject(CreateBinding());
        convention["origin"] = (int)CosmosRelationQueryBindingOrigin.Convention;
        var originException = Assert.Throws<ArgumentException>(() => Deserialize(convention));

        Assert.Contains("does not belong", foreignException.ToString(), StringComparison.Ordinal);
        Assert.Contains("cannot retain explicit", originException.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRehydration_RejectsUnsupportedPersistedSchemaVersion()
    {
        var document = SerializeToObject(CreateBinding());
        document["schemaVersion"] = "cohesive.relations.cosmos-binding/v0";

        var exception = Assert.Throws<ArgumentException>(() => Deserialize(document));

        Assert.Contains("Unsupported Cosmos relation/query storage-binding schema version", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accountEndpoint")]
    [InlineData("databaseName")]
    [InlineData("containerName")]
    public void JsonRehydration_RequiresCompletePhysicalAffinity(string setting)
    {
        var document = SerializeToObject(CreateBinding());
        document.Remove(setting);

        Assert.ThrowsAny<ArgumentException>(() => Deserialize(document));
    }

    [Theory]
    [InlineData("accountEndpoint", "https://other.documents.azure.com/")]
    [InlineData("databaseName", "tampered-operations")]
    [InlineData("containerName", "tampered-loads")]
    public void JsonRehydration_RejectsPhysicalAffinityTamperedAfterFingerprinting(string setting, string value)
    {
        var document = SerializeToObject(CreateBinding());
        document[setting] = value;

        var exception = Assert.Throws<ArgumentException>(() => Deserialize(document));

        Assert.Contains("fingerprint does not match normalized content", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///tmp/cosmos")]
    [InlineData("urn:cosmos:account")]
    public void JsonRehydration_RejectsUnsupportedAccountEndpoint(string endpoint)
    {
        var document = SerializeToObject(CreateBinding());
        document["accountEndpoint"] = endpoint;

        var exception = Assert.Throws<ArgumentException>(() => Deserialize(document));

        Assert.Contains("HTTP or HTTPS", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRehydration_RequiresPersistedFingerprint()
    {
        var document = SerializeToObject(CreateBinding());
        document.Remove("fingerprint");

        var exception = Assert.Throws<ArgumentNullException>(() => Deserialize(document));

        Assert.Contains("fingerprint", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    static CosmosRelationQueryStorageBinding CreateBinding(
        RelationQueryConfigurationValueOrigin fieldOrigin = RelationQueryConfigurationValueOrigin.AdapterConvention,
        bool includeAffinity = true) => new(
        id: new("load-search/v1"),
        source: new RelationQuerySourceInstanceId("loads-source"),
        placementBinding: new RelationQuerySourcePlacementBindingId("loads-placement"),
        target: new RelationQueryTargetId("cosmos"),
        targetProfile: new RelationQueryTargetProfileId("cosmos-query/v1"),
        accountEndpoint: AccountEndpoint,
        databaseName: DatabaseName,
        containerName: "loads",
        rootAlias: "c",
        identityPath: FieldPath.Parse("id"),
        fields:
        [
            new(new RelationQueryInputId("field:status"), FieldPath.Parse("status")),
            new(new RelationQueryInputId("field:id"), FieldPath.Parse("id")),
            new(
                new RelationQueryInputId("field:stops"),
                FieldPath.Parse("stops"),
                CollectionScope()),
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
        origin: CosmosRelationQueryBindingOrigin.Explicit,
        conventionSetVersion: CosmosRelationQueryStorageBinding.SemanticPathConventionSet,
        configurationDecisions:
        [
            new("field/field:id", fieldOrigin, "tests/cosmos-fields/v1"),
            new("rootAlias", RelationQueryConfigurationValueOrigin.AdapterConvention, CosmosRelationQueryStorageBinding.SemanticPathConventionSet),
            new("accountEndpoint", RelationQueryConfigurationValueOrigin.Explicit, "tests"),
            new("databaseName", RelationQueryConfigurationValueOrigin.Explicit, "tests"),
            new("containerName", RelationQueryConfigurationValueOrigin.Explicit, "tests")
        ],
        compiledPlanFingerprint: includeAffinity ? CompiledPlanFingerprint : null,
        placementFingerprint: includeAffinity ? PlacementFingerprint : null);

    static CosmosRelationQueryCollectionScopeEvidence CollectionScope() => new(
        "tests/cosmos-json-array/v1",
        CosmosRelationQueryCollectionElementScope.JsonArrayElement,
        CosmosRelationQueryCollectionCorrelationGuarantee.SameArrayElement,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryEmptyCollectionBehavior.NoElements,
        [
            CollectionChild("type", CosmosRelationQueryCollectionElementValueDomain.String),
            CollectionChild("location", CosmosRelationQueryCollectionElementValueDomain.String)
        ]);

    static CosmosRelationQueryCollectionElementFieldBinding CollectionChild(
        string field,
        CosmosRelationQueryCollectionElementValueDomain valueDomain) => new(
        FieldPath.Parse(field),
        FieldPath.Parse(field),
        valueDomain,
        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality,
        "tests/cosmos-json-scalar/v1",
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion);

    static JsonObject SerializeToObject(CosmosRelationQueryStorageBinding binding) =>
        JsonNode.Parse(JsonSerializer.Serialize(binding, JsonOptions))!.AsObject();

    static CosmosRelationQueryStorageBinding? Deserialize(JsonObject document) =>
        JsonSerializer.Deserialize<CosmosRelationQueryStorageBinding>(document.ToJsonString(), JsonOptions);

    static void Reverse(JsonArray values)
    {
        var reversed = values.Select(static value => value?.DeepClone()).Reverse().ToArray();
        values.Clear();
        foreach (var value in reversed)
        {
            values.Add(value);
        }
    }
}

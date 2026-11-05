using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Adapters.Mcp.Development;

namespace Cohesive.Semantics.Tests;

public sealed class ApiContractTests
{
    static ApiContractDocument Contract<TRequest, TResult>(string route = "/items") => ApiContractDocument.Lower(
        new("items", ApiOperationKind.Query, typeof(TRequest) == typeof(NoBody) ? typeof(void) : typeof(TRequest), typeof(TResult),
            new HttpBinding("POST", route, [], typeof(TRequest) == typeof(NoBody) ? null : new(typeof(TRequest))), id: new("items")),
        new(JsonSerializerDefaults.Web), "tests-web/v1");

    [Fact]
    public void PortableContractRoundTripsAndRejectsTampering()
    {
        var contract = Contract<NoBody, RequiredName>();
        var restored = ApiContractDocument.Read(contract.ToJson());
        Assert.Equal(contract.Fingerprint, restored.Fingerprint);
        Assert.Equal(contract.ToJson().GetRawText(), restored.ToJson().GetRawText());
        var changed = JsonNode.Parse(contract.ToJson().GetRawText())!;
        changed["definition"]!["http"]!["route"] = "/changed";
        Assert.Throws<JsonException>(() => ApiContractDocument.Read(JsonSerializer.SerializeToElement(changed)));
    }

    [Fact]
    public void ResponseFieldTypesAndNullabilityAreDirectional()
    {
        var before = Contract<NoBody, RequiredName>();
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.Compare(before, Contract<NoBody, NumericName>()).Verdict);
        var nullable = Contract<NoBody, NullableName>();
        var forward = ApiContractCompatibility.Compare(before, nullable);
        Assert.Equal(ApiCompatibilityVerdict.Breaking, forward.Verdict);
        Assert.Contains(forward.Diagnostics, d => d.Code == "api.compatibility.nullability" && d.Location!.EndsWith("/name"));
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(before, nullable,
            ApiCompatibilityDirection.ProposedClientToExistingServer).Verdict);
    }

    [Fact]
    public void RequiredRequestFieldAdditionBreaksOldClientsButOptionalAdditionDoesNot()
    {
        var before = Contract<RequiredName, RequiredName>();
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.Compare(before, Contract<RequiredExtra, RequiredName>()).Verdict);
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(before, Contract<OptionalExtra, RequiredName>()).Verdict);
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(before, Contract<RequiredExtra, RequiredName>(),
            ApiCompatibilityDirection.ProposedClientToExistingServer).Verdict);
    }

    [Fact]
    public void TolerantResponseReadersAcceptAddedFieldsButUnknownTypesNeverProveCompatibility()
    {
        var before = Contract<NoBody, RequiredName>();
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(before, Contract<NoBody, RequiredExtra>()).Verdict);
        var opaque = Contract<NoBody, OpaqueResponse>();
        Assert.Equal(ApiCompatibilityVerdict.Unknown, ApiContractCompatibility.Compare(opaque, opaque).Verdict);
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.Compare(before, Contract<NoBody, RequiredName>("/moved")).Verdict);
    }

    [Fact]
    public void QualifiedShapeMembersShareOnlyIdenticalContentAndKeepFieldImpactLocal()
    {
        var root = Path.Combine(Path.GetTempPath(), "shape-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "source.cs"), "source");
            var first = new Shape(new("first"), [new(new("value"), new ScalarTypeRef(ScalarTypeKind.Int32))]);
            var second = new Shape(new("second"), [new(new("other"), new ScalarTypeRef(ScalarTypeKind.String))]);
            var options = StrictDocumentJson.CreateOptions();
            JsonElement Graph(params Shape[] shapes) => JsonSerializer.SerializeToElement(ShapeGraphDocument.FromGraph(new(new("shared"), [.. shapes])), options);
            SemanticWorkspaceExport.WriteDocuments(root, "bundle.json", ["source.cs"], "tests", _ => [Graph(first), Graph(first, second)]);
            var workspace = DevelopmentMcpServer.CreateDefaultWorkspace(root, "bundle.json");
            var snapshot = workspace.Describe(limit: 100);
            var fieldId = ShapeWorkspaceProjection.FieldId(new(new("shared"), new("first")), "value");
            var impact = workspace.Impact(fieldId, snapshot.SnapshotId).Result;
            Assert.Contains(impact.Items, i => i.Consumer.DefinitionId.Value == "shape:shared/first");
            Assert.DoesNotContain(impact.Items, i => i.Consumer.DefinitionId.Value == "shape:shared/second");
            Assert.All(impact.Items.SelectMany(i => i.Path), edge => Assert.False(edge.IsExactMatch));
            var incompatible = new Shape(new("first"), [new(new("value"), new ScalarTypeRef(ScalarTypeKind.String))]);
            SemanticWorkspaceExport.WriteDocuments(root, "bundle.json", ["source.cs"], "tests", _ => [Graph(first), Graph(incompatible)]);
            Assert.Throws<JsonException>(() => workspace.Describe());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EndpointRemovalIsBreakingOnlyForAClientThatUsesIt()
    {
        var contract = Contract<NoBody, RequiredName>().ToJson();
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.CompareDocuments(contract, null).Verdict);
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.CompareDocuments(null, contract).Verdict);
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.CompareDocuments(null, contract,
            ApiCompatibilityDirection.ProposedClientToExistingServer).Verdict);
    }

    [Fact]
    public void UninterpretedSerializerPolicyAndChangedAuthorizationAreExplicitlyUnknown()
    {
        var source = new ApiOperation("items", ApiOperationKind.Query, typeof(void), typeof(RequiredName), new HttpBinding("GET", "/items", [], null));
        var restrictive = ApiContractDocument.Lower(source, new(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }, "closed-reader/v1");
        Assert.Equal(ApiCompatibilityVerdict.Unknown, ApiContractCompatibility.Compare(restrictive, restrictive).Verdict);
        var restrictedSource = new ApiOperation("items", ApiOperationKind.Query, typeof(void), typeof(RequiredName), source.Http,
            authorizationRequirements: [new("items.read")]);
        var plain = ApiContractDocument.Lower(source, new(JsonSerializerDefaults.Web), "web/v1");
        var restricted = ApiContractDocument.Lower(restrictedSource, new(JsonSerializerDefaults.Web), "web/v1");
        Assert.Contains(ApiContractCompatibility.Compare(plain, restricted).Diagnostics, d => d.Location == "/authorizationRequirements");
    }

    [Fact]
    public void EntityDefinitionRetainsItsCanonicalStateShapeAndQualifiedFieldDependencies()
    {
        var entity = new Cohesive.Transitions.Model.EntityDefinition(new("Order"), [new(new("id"), new ScalarTypeRef(ScalarTypeKind.String))]);
        var root = Path.Combine(Path.GetTempPath(), "entity-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "source.cs"), "source");
            SemanticWorkspaceExport.WriteDocuments(root, "bundle.json", ["source.cs"], "tests", _ =>
                [Cohesive.Transitions.Model.EntityStateDocument.Export(entity),
                 JsonSerializer.SerializeToElement(ShapeGraphDocument.FromGraph(entity.StateShape.Graph), StrictDocumentJson.CreateOptions())]);
            var workspace = DevelopmentMcpServer.CreateDefaultWorkspace(root, "bundle.json");
            var snapshot = workspace.Describe().SnapshotId;
            var path = Assert.Single(workspace.Impact(ShapeWorkspaceProjection.FieldId(entity.StateShape.QualifiedId, "id"), snapshot).Result.Items,
                i => i.Consumer.DefinitionId.Value == "entity:Order");
            Assert.Equal(2, path.Path.Length);
            Assert.All(path.Path, e => Assert.NotNull(e.Available));
            Assert.Equal("entity-state", workspace.Inspect("entity:Order", snapshot).Result.Subject.Kind);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public sealed record NoBody;
    public sealed record RequiredName([property: JsonRequired] string Name);
    public sealed record NumericName([property: JsonRequired] int Name);
    public sealed record NullableName([property: JsonRequired] string? Name);
    public sealed record RequiredExtra([property: JsonRequired] string Name, [property: JsonRequired] string Extra);
    public sealed record OptionalExtra([property: JsonRequired] string Name, string? Extra);
    public sealed record OpaqueResponse(object Value);
}

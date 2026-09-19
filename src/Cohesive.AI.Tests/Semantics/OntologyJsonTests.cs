using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.AI.Semantics;

namespace Cohesive.AI.Tests.Semantics;

public sealed class OntologyJsonTests
{
    static readonly OntologyRule[] Rules =
    [
        new RelationLawRule("custom", RelationLawFlags.Transitive | RelationLawFlags.Reflexive),
        new RelationDomainRule("custom", "shipment"),
        new RelationRangeRule("custom", "consignee"),
        new SubRelationRule("custom", StandardRelationTypeIds.SubConceptOf),
        new RelationCardinalityRule("custom", "shipment", min: 1, max: 2),
        new ScopedSymbolMeaningRule("N1.EntityIdentifierCode", "CN", "consignee"),
        new ScopedDefaultMeaningRule("N1.EntityIdentifierCode", "shipment"),
        new ScopedAllowedSymbolsRule("N1.EntityIdentifierCode", ["CN", "SH"])
    ];

    static Ontology Create(IEnumerable<OntologyRule>? rules = null) => new(
        concepts: new[]
        {
            new Concept("shipment", "Load", ["shipment", "load"], ImmutableDictionary<string, string>.Empty.Add("kind", "entity")),
            new Concept("consignee", "Consignee", ["ship to"])
        }.ToImmutableDictionary(c => c.ConceptId),
        relationTypes: ImmutableDictionary<string, RelationType>.Empty.Add("custom", new("custom", "Custom")),
        relations: [new("shipment", "consignee", "custom", weight: 0.75)],
        rules: [.. rules ?? Rules]);

    [Fact]
    public void RoundTrip_PreservesEveryRuleKindAndClosureMeaning()
    {
        var original = Create();
        var json = OntologyJson.SerializeCanonical(original);
        var restored = OntologyJson.Deserialize(json);
        Assert.Equal(json, OntologyJson.SerializeCanonical(restored));
        Assert.Equal(original.Concepts.OrderBy(p => p.Key), restored.Concepts.OrderBy(p => p.Key));
        Assert.Equal(original.RelationTypes.OrderBy(p => p.Key), restored.RelationTypes.OrderBy(p => p.Key));
        Assert.Equal(original.Relations.ToArray(), restored.Relations.ToArray());
        Assert.Equal(original.Rules.Select(r => r.GetType()), restored.Rules.Select(r => r.GetType()));
        foreach (var rule in original.Rules)
        {
            Assert.Contains(restored.Rules, candidate =>
                JsonSerializer.Serialize<OntologyRule>(candidate) == JsonSerializer.Serialize<OntologyRule>(rule));
        }
        Assert.True(OntologyClosure.Create(restored).TryGetScopedMeaning("N1.EntityIdentifierCode", "CN", out var concept));
        Assert.Equal("consignee", concept);
    }

    [Fact]
    public void OrdinarySerialization_PreservesPolymorphicRules()
    {
        var original = Create();
        var json = JsonSerializer.Serialize(original);
        Assert.Contains("scopedSymbolMeaning", json);
        var restored = JsonSerializer.Deserialize<Ontology>(json)!;
        Assert.Equal(OntologyJson.SerializeCanonical(original), OntologyJson.SerializeCanonical(restored));
    }

    [Fact]
    public void NestedDuplicateProperty_IsRejectedWithItsLocation()
    {
        var json = OntologyJson.SerializeCanonical(Create());
        json = json.Replace("\"symbol\":\"CN\"", "\"symbol\":\"CN\",\"symbol\":\"SH\"", StringComparison.Ordinal);
        var error = Assert.Throws<JsonException>(() => OntologyJson.Deserialize(json));
        Assert.Contains("DuplicateProperty", error.Message);
        Assert.EndsWith("/symbol", error.Path);
    }

    [Fact]
    public void Fixtures_CoverEveryConcreteOntologyRule()
    {
        var types = typeof(OntologyRule).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(OntologyRule).IsAssignableFrom(t))
            .OrderBy(t => t.FullName);
        Assert.Equal(types, Rules.Select(r => r.GetType()).OrderBy(t => t.FullName));
    }

    [Fact]
    public void EveryRulePayload_AffectsCanonicalContentHash()
    {
        OntologyRule[] changed =
        [
            new RelationLawRule("custom", RelationLawFlags.Symmetric),
            new RelationDomainRule("custom", "consignee"),
            new RelationRangeRule("custom", "shipment"),
            new SubRelationRule("custom", StandardRelationTypeIds.EquivalentTo),
            new RelationCardinalityRule("custom", "shipment", min: 1, max: 3),
            new ScopedSymbolMeaningRule("N1.EntityIdentifierCode", "CN", "shipment"),
            new ScopedDefaultMeaningRule("N1.EntityIdentifierCode", "consignee"),
            new ScopedAllowedSymbolsRule("N1.EntityIdentifierCode", ["CN", "SH", "BT"])
        ];
        for (var i = 0; i < Rules.Length; i++)
        {
            var before = SHA256.HashData(Encoding.UTF8.GetBytes(OntologyJson.SerializeCanonical(Create([Rules[i]]))));
            var after = SHA256.HashData(Encoding.UTF8.GetBytes(OntologyJson.SerializeCanonical(Create([changed[i]]))));
            Assert.False(before.SequenceEqual(after), Rules[i].GetType().Name);
        }
    }

    [Fact]
    public void CanonicalBytes_DoNotDependOnDictionaryInsertionOrRuleOrder()
    {
        var original = Create();
        var reordered = new Ontology(
            original.Concepts.Reverse().ToImmutableDictionary(p => p.Key, p => p.Value),
            original.RelationTypes.Reverse().ToImmutableDictionary(p => p.Key, p => p.Value),
            original.Relations,
            [.. original.Rules.Reverse()]);
        Assert.Equal(OntologyJson.SerializeCanonical(original), OntologyJson.SerializeCanonical(reordered));
        var indented = JsonNode.Parse(OntologyJson.SerializeCanonical(original))!.ToJsonString(new() { WriteIndented = true });
        Assert.Equal(OntologyJson.SerializeCanonical(original), OntologyJson.SerializeCanonical(OntologyJson.Deserialize(indented)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"concepts\":{},\"concepts\":{}}")]
    public void RejectsInvalidOrIncompleteWireDocuments(string json) =>
        Assert.Throws<JsonException>(() => OntologyJson.Deserialize(json));

    [Theory]
    [InlineData("unknownKind")]
    [InlineData("missingKind")]
    [InlineData("unknownMember")]
    [InlineData("discardedNullRule")]
    [InlineData("discardedSelfRelation")]
    [InlineData("nullRelation")]
    [InlineData("wrongConceptKey")]
    public void RejectsUnknownOrLossyRuleAndRelationContent(string mutation)
    {
        var node = JsonNode.Parse(OntologyJson.SerializeCanonical(Create()))!;
        switch (mutation)
        {
            case "nullRelation": node["relations"]!.AsArray().Add((JsonNode?)null); break;
            case "wrongConceptKey": node["concepts"]!["shipment"]!["conceptId"] = "other"; break;
            case "unknownKind": node["rules"]![0]!["kind"] = "futureRule"; break;
            case "missingKind": node["rules"]![0]!.AsObject().Remove("kind"); break;
            case "unknownMember": node["rules"]![0]!["futureProperty"] = true; break;
            case "discardedNullRule": node["rules"]!.AsArray().Add((JsonNode?)null); break;
            case "discardedSelfRelation":
                node["relations"]![0]!["targetConceptId"] = "shipment";
                break;
        }
        Assert.Throws<JsonException>(() => OntologyJson.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void UnresolvedReferences_ArePreservedForLaterImportResolution()
    {
        var slice = Create([new ScopedSymbolMeaningRule("scope", "value", "external")]);
        var restored = OntologyJson.Deserialize(OntologyJson.SerializeCanonical(slice));
        Assert.Contains(restored.Rules, rule => rule is ScopedSymbolMeaningRule { ConceptId: "external" });
        var error = Assert.Throws<InvalidOperationException>(() => OntologyValidator.Validate(restored));
        Assert.Contains("external", error.Message);
    }
}

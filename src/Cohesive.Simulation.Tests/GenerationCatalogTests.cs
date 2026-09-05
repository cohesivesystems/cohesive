using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class GenerationCatalogTests
{
    static readonly DefaultClrTypeRefMapper TypeMapper = new();

    [Fact]
    public void CatalogDocument_NormalizesAndRoundTripsExactProviderEvidence()
    {
        var document = PersonCatalog(
            entries:
            [
                Entry("profile/z", new PersonProfile("Grace", "Hopper", "grace@example.test"), weight: 2d),
                Entry("profile/a", new PersonProfile("Ada", "Lovelace", "ada@example.test"))
            ]);

        var json = GenerationCatalogJsonSerializer.Serialize(document);
        var restored = GenerationCatalogJsonSerializer.Deserialize(json);

        Assert.Equal(GenerationCatalogDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(document.Fingerprint, restored.Fingerprint);
        Assert.Equal(["profile/a", "profile/z"], restored.Definition.Entries.Select(static entry => entry.Id));
        Assert.Equal("en-US", restored.Definition.Provenance.Locale);
        Assert.Equal("35.6.3", restored.Definition.Provenance.ProviderVersion);
        Assert.Equal(
            ["nuget://Bogus/35.6.3", "repo://eng/catalog-import.json"],
            restored.Definition.Provenance.SourceReferences.Select(static source => source.Value));
        Assert.Equal(json, GenerationCatalogJsonSerializer.Serialize(restored));
    }

    [Fact]
    public void CatalogFingerprint_IncludesLocaleProviderAndExactValues()
    {
        var baseline = PersonCatalog();
        var otherLocale = PersonCatalog(locale: "fr-FR");
        var otherProviderVersion = PersonCatalog(providerVersion: "35.6.4");
        var otherValues = PersonCatalog(
            entries: [Entry("profile/a", new PersonProfile("Augusta", "King", "augusta@example.test"))]);

        Assert.NotEqual(baseline.Fingerprint, otherLocale.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, otherProviderVersion.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, otherValues.Fingerprint);
    }

    [Theory]
    [InlineData("fingerprint", "simulation.generation.catalog.document.contentInvalid")]
    [InlineData("schema", "simulation.generation.catalog.document.contentInvalid")]
    [InlineData("unknown", "simulation.generation.catalog.document.contentInvalid")]
    [InlineData("duplicate", "simulation.generation.catalog.document.duplicateProperty")]
    [InlineData("order", "simulation.generation.catalog.document.wireNonCanonical")]
    public void CatalogDocument_FailsClosedForInvalidOrNoncanonicalWireContent(
        string scenario,
        string expectedCode)
    {
        var json = GenerationCatalogJsonSerializer.Serialize(PersonCatalog());
        var invalid = scenario switch
        {
            "fingerprint" => Mutate(json, root => root["fingerprint"]!["value"] = new string('0', 64)),
            "schema" => Mutate(json, root => root["schemaVersion"] = "cohesive-simulation-generation-catalog/v999"),
            "unknown" => Mutate(json, root => root["unexpected"] = true),
            "duplicate" => json.Replace(
                $"\"schemaVersion\":\"{GenerationCatalogDocument.CurrentSchemaVersion}\"",
                $"\"schemaVersion\":\"{GenerationCatalogDocument.CurrentSchemaVersion}\"," +
                $"\"schemaVersion\":\"{GenerationCatalogDocument.CurrentSchemaVersion}\"",
                StringComparison.Ordinal),
            "order" => Mutate(json, ReverseEntries),
            _ => throw new InvalidOperationException($"Unknown invalid-catalog scenario '{scenario}'.")
        };

        var validation = GenerationCatalogJsonSerializer.TryDeserialize(invalid, out var document);

        Assert.Null(document);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void CatalogDocument_RejectsInvalidEntries()
    {
        var provenance = Provenance();
        var stringType = TypeMapper.Map(typeof(string), nullability: null);

        Assert.Throws<ArgumentException>(() => GenerationCatalogDocument.FromDefinition(new(
            id: "catalog/empty",
            revision: "r1",
            valueType: stringType,
            entries: [],
            provenance)));
        Assert.Throws<ArgumentException>(() => GenerationCatalogDocument.FromDefinition(new(
            id: "catalog/duplicate",
            revision: "r1",
            valueType: stringType,
            entries:
            [
                new("same", ObservationValue.FromString("Ada")),
                new("same", ObservationValue.FromString("Grace"))
            ],
            provenance)));
        Assert.Throws<ArgumentException>(() => GenerationCatalogDocument.FromDefinition(new(
            id: "catalog/weight",
            revision: "r1",
            valueType: stringType,
            entries: [new("name", ObservationValue.FromString("Ada"), weight: 0d)],
            provenance)));
        Assert.Throws<ArgumentException>(() => GenerationCatalogDocument.FromDefinition(new(
            id: "catalog/type",
            revision: "r1",
            valueType: stringType,
            entries: [new("number", ObservationValue.FromInt64(42))],
            provenance)));
    }

    [Fact]
    public void CatalogRecordSource_GeneratesCoherentRecordsAndReplaysWithoutProviderState()
    {
        var catalog = PersonCatalog();
        var definition = PersonDefinition(catalog);
        var generator = definition.Compile();

        var generated = generator.GenerateSequence(seed: 1729, count: 20);
        var allowed = catalog.Definition.Entries
            .Select(static entry => entry.Value)
            .ToHashSet();

        Assert.All(generated, item => Assert.Contains(ObservationValue.FromObject(item.Value), allowed));
        Assert.Equal(
            generated[7],
            generator.Generate(seed: 1729, sequenceIndex: 7));

        var json = GenerationDefinitionJsonSerializer.Serialize(definition.Definition);
        var restored = GenerationDefinitionJsonSerializer.Deserialize(json).Compile();
        Assert.Equal(
            generated[7].Observation,
            ReferenceGenerationInterpreter.Generate(restored, seed: 1729, sequenceIndex: 7).Observation);
        Assert.Contains(
            $"\"{GenerationDefinitionWireNames.GeneratorDiscriminator}\":\"{GenerationDefinitionWireNames.Catalog}\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"providerVersion\":\"35.6.3\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldArtifact_EmbedsTheCompleteCatalogRatherThanOnlyItsFingerprint()
    {
        var catalog = PersonCatalog();
        var definition = PersonDefinition(catalog);
        var world = Simulation.DefineWorld(
                "world/catalog-demo",
                "r1",
                builder => builder.Population("people", count: 3, definition))
            .Compile();

        var manifest = WorldArtifactManifest.FromWorld(world, rootSeed: 42);
        var json = WorldArtifactManifestJsonSerializer.Serialize(manifest);
        var retainedWorld = WorldArtifactManifestJsonSerializer.Deserialize(json).GetCoreWorld();
        var retainedCatalog = Assert.IsType<CatalogGenerationNode>(
            Assert.Single(retainedWorld.Definition.Populations).Generation.Root.Bindings[0].Generator).Catalog;

        Assert.Equal(catalog.Fingerprint, retainedCatalog.Fingerprint);
        Assert.Equal(catalog.Definition.Entries.Length, retainedCatalog.Definition.Entries.Length);
        Assert.Contains("\"adapter\":\"cohesive-adapters-bogus\"", json, StringComparison.Ordinal);
        Assert.Contains("\"providerVersion\":\"35.6.3\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogGenerator_RejectsAClrTypeThatDoesNotMatchTheRetainedCatalog()
    {
        var catalog = PersonCatalog();

        var exception = Assert.Throws<ArgumentException>(() => Gen.Catalog<string>(catalog));

        Assert.Equal("catalog", exception.ParamName);
    }

    static GenerationCatalogDocument PersonCatalog(
        string locale = "en-US",
        string providerVersion = "35.6.3",
        ImmutableArray<GenerationCatalogEntry> entries = default)
    {
        if (entries.IsDefault)
        {
            entries =
            [
                Entry("profile/ada", new PersonProfile("Ada", "Lovelace", "ada@example.test")),
                Entry("profile/grace", new PersonProfile("Grace", "Hopper", "grace@example.test"))
            ];
        }

        return GenerationCatalogDocument.FromDefinition(new(
            id: "catalog/person-profile",
            revision: $"bogus-{providerVersion}-{locale}",
            valueType: TypeMapper.Map(typeof(PersonProfile), nullability: null),
            entries,
            provenance: Provenance(locale, providerVersion)));
    }

    static PocoGenerationDefinition<GeneratedPerson> PersonDefinition(GenerationCatalogDocument catalog) =>
        Simulation.Define<GeneratedPerson>(person =>
        {
            var profile = person.SampleRecord("profile", Gen.Catalog<PersonProfile>(catalog));
            person.Member(value => value.GivenName, profile.Project(value => value.GivenName));
            person.Member(value => value.FamilyName, profile.Project(value => value.FamilyName));
            person.Member(value => value.Email, profile.Project(value => value.Email));
        });

    static GenerationCatalogProvenance Provenance(
        string locale = "en-US",
        string providerVersion = "35.6.3") => new(
        adapter: "cohesive-adapters-bogus",
        adapterVersion: "0.1.0-alpha.1",
        provider: "Bogus",
        providerVersion,
        locale,
        randomAlgorithm: "bogus-randomizer/v1",
        seed: "8675309",
        sourceReferences:
        [
            SourceReference.Repository(new("eng/catalog-import.json")),
            SourceReference.Create("nuget", $"Bogus/{providerVersion}")
        ]);

    static GenerationCatalogEntry Entry(string id, PersonProfile profile, double weight = 1d) =>
        new(id, ObservationValue.FromObject(profile), weight);

    static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Generation-catalog JSON did not contain an object.");
        mutate(root);
        return root.ToJsonString();
    }

    static void ReverseEntries(JsonObject root)
    {
        var entries = root["definition"]?["entries"]?.AsArray()
                      ?? throw new InvalidOperationException("Generation-catalog JSON has no entries array.");
        var reversed = entries
            .Select(static entry => entry?.DeepClone())
            .Reverse()
            .ToArray();
        entries.Clear();
        foreach (var entry in reversed)
            entries.Add(entry);
    }

    public sealed record PersonProfile(string GivenName, string FamilyName, string Email);

    public sealed record GeneratedPerson(string GivenName, string FamilyName, string Email);
}

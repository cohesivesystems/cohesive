using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Tests;

public sealed class PortableGenerationDefinitionTests
{
    [Fact]
    public void AuthoredDefinition_RoundTripsForScriptConsumption_WithoutChangingGeneration()
    {
        var authored = CustomerDefinition(["Name", "Age", "IsActive", "Tier"]);
        var authoredPlan = authored.Compile().Plan;
        var json = GenerationDefinitionJsonSerializer.Serialize(authored.Definition);

        var restoredDocument = GenerationDefinitionJsonSerializer.Deserialize(json);
        var restoredPlan = restoredDocument.Compile();
        var authoredValue = ReferenceGenerationInterpreter.Generate(authoredPlan, seed: 42, sequenceIndex: 7);
        var restoredValue = ReferenceGenerationInterpreter.Generate(restoredPlan, seed: 42, sequenceIndex: 7);

        Assert.Equal(GenerationDefinitionDocument.CurrentSchemaVersion, restoredDocument.SchemaVersion);
        Assert.Equal(authoredPlan.Fingerprint, restoredDocument.Fingerprint.Value);
        Assert.Equal(authoredPlan.Fingerprint, restoredPlan.Fingerprint);
        Assert.Equal(authoredValue, restoredValue);
        Assert.Equal(json, GenerationDefinitionJsonSerializer.Serialize(restoredDocument));
        Assert.Contains(
            $"\"{GenerationDefinitionWireNames.GeneratorDiscriminator}\":\"{GenerationDefinitionWireNames.Constant}\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"{GenerationDefinitionWireNames.GeneratorDiscriminator}\":\"{GenerationDefinitionWireNames.Int32}\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"{GenerationDefinitionWireNames.GeneratorDiscriminator}\":\"{GenerationDefinitionWireNames.Bernoulli}\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"{GenerationDefinitionWireNames.GeneratorDiscriminator}\":\"{GenerationDefinitionWireNames.WeightedCategorical}\"",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EquivalentMemberDeclarationOrders_ProduceOneCanonicalDocument()
    {
        var first = CustomerDefinition(["Name", "Age", "IsActive", "Tier"]);
        var reordered = CustomerDefinition(["Tier", "IsActive", "Name", "Age"]);

        var firstJson = GenerationDefinitionJsonSerializer.Serialize(first.Definition);
        var reorderedJson = GenerationDefinitionJsonSerializer.Serialize(reordered.Definition);

        Assert.Equal(firstJson, reorderedJson);
        Assert.Equal(
            ["Age", "IsActive", "Name", "Tier"],
            GenerationDefinitionJsonSerializer.Deserialize(firstJson)
                .Definition.Root.Members
                .Select(static member => member.Identity.Value));
    }

    [Fact]
    public void IndentedDocument_RestoresToTheSameCanonicalCompactDocument()
    {
        var definition = CustomerDefinition(["Name", "Age", "IsActive", "Tier"]);
        var document = GenerationDefinitionDocument.FromDefinition(definition.Definition);
        var indented = GenerationDefinitionJsonSerializer.Serialize(
            document,
            Cohesive.Model.Serialization.PortableDocumentJsonFormatting.Indented);

        var restored = GenerationDefinitionJsonSerializer.Deserialize(indented);

        Assert.Equal(
            GenerationDefinitionJsonSerializer.Serialize(document),
            GenerationDefinitionJsonSerializer.Serialize(restored));
    }

    [Theory]
    [InlineData("fingerprint", "simulation.generation.document.contentInvalid")]
    [InlineData("schema", "simulation.generation.document.contentInvalid")]
    [InlineData("unknown", "simulation.generation.document.contentInvalid")]
    [InlineData("duplicate", "simulation.generation.document.duplicateProperty")]
    [InlineData("order", "simulation.generation.document.wireNonCanonical")]
    [InlineData("generator", "simulation.generation.document.contentInvalid")]
    public void InvalidPortableDocuments_ProduceStructuredDiagnostics(string scenario, string expectedCode)
    {
        var json = GenerationDefinitionJsonSerializer.Serialize(
            CustomerDefinition(["Name", "Age", "IsActive", "Tier"]).Definition);
        var invalid = scenario switch
        {
            "fingerprint" => Mutate(json, root =>
                root["fingerprint"]!["value"] = new string('0', 64)),
            "schema" => Mutate(json, root =>
                root["schemaVersion"] = "cohesive-simulation-generation/v999"),
            "unknown" => Mutate(json, root => root["unexpected"] = true),
            "duplicate" => json.Replace(
                $"\"schemaVersion\":\"{GenerationDefinitionDocument.CurrentSchemaVersion}\"",
                $"\"schemaVersion\":\"{GenerationDefinitionDocument.CurrentSchemaVersion}\"," +
                $"\"schemaVersion\":\"{GenerationDefinitionDocument.CurrentSchemaVersion}\"",
                StringComparison.Ordinal),
            "order" => Mutate(json, ReverseMembers),
            "generator" => Mutate(json, root =>
                root["definition"]!["root"]!["members"]![0]!["generator"]![
                    GenerationDefinitionWireNames.GeneratorDiscriminator] = "unknown"),
            _ => throw new InvalidOperationException($"Unknown invalid-document scenario '{scenario}'.")
        };

        var result = GenerationDefinitionJsonSerializer.TryDeserialize(invalid, out var document);

        Assert.Null(document);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void ReplayToken_RoundTripsAndReplaysAnExactGeneratedObservation()
    {
        var plan = CustomerDefinition(["Name", "Age", "IsActive", "Tier"]).Compile().Plan;
        var generated = ReferenceGenerationInterpreter.Generate(plan, seed: 1729, sequenceIndex: 12);

        var token = generated.Replay.ToToken();
        var restoredEvidence = GenerationReplayEvidence.ParseToken(token);
        var replayed = ReferenceGenerationInterpreter.Replay(plan, token);

        Assert.StartsWith("csimr2.", token, StringComparison.Ordinal);
        Assert.DoesNotContain('=', token);
        Assert.Equal(generated.Replay, restoredEvidence);
        Assert.Equal(generated, replayed);
        Assert.Throws<FormatException>(() => GenerationReplayEvidence.ParseToken(
            token.Replace("csimr2.", "csimr1.", StringComparison.Ordinal)));
    }

    [Fact]
    public void Replay_RejectsAnotherDefinitionAndMalformedTokens()
    {
        var original = CustomerDefinition(["Name", "Age", "IsActive", "Tier"]).Compile().Plan;
        var changed = Simulation.Define<ScriptCustomer>(customer => customer
                .Member(value => value.Name, Gen.Constant("Grace"))
                .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90))
                .Member(value => value.IsActive, Gen.Bernoulli(probability: 0.85))
                .Member(value => value.Tier, Gen.Categorical(
                    Gen.Weighted("gold", weight: 1d),
                    Gen.Weighted("silver", weight: 3d))))
            .Compile()
            .Plan;
        var token = ReferenceGenerationInterpreter.Generate(original, seed: 7).Replay.ToToken();

        var mismatch = Assert.Throws<ArgumentException>(() =>
            ReferenceGenerationInterpreter.Replay(changed, token));

        Assert.Contains("definition", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<FormatException>(() => GenerationReplayEvidence.ParseToken("csimr2.not_base64!"));
    }

    [Fact]
    public void DefinitionFingerprint_IncludesTheExactGoverningShapeGraph()
    {
        var mutable = RequirePlan(DirectDefinition(FieldMutability.Mutable));
        var writeOnce = RequirePlan(DirectDefinition(FieldMutability.WriteOnce));

        Assert.NotEqual(mutable.Fingerprint, writeOnce.Fingerprint);
    }

    static PocoGenerationDefinition<ScriptCustomer> CustomerDefinition(IReadOnlyList<string> order) =>
        Simulation.Define<ScriptCustomer>(customer =>
        {
            foreach (var member in order)
            {
                switch (member)
                {
                    case "Name":
                        customer.Member(value => value.Name, Gen.Constant("Ada"));
                        break;
                    case "Age":
                        customer.Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90));
                        break;
                    case "IsActive":
                        customer.Member(value => value.IsActive, Gen.Bernoulli(probability: 0.85));
                        break;
                    case "Tier":
                        customer.Member(value => value.Tier, Gen.Categorical(
                            Gen.Weighted("gold", weight: 1d),
                            Gen.Weighted("silver", weight: 3d)));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown customer member '{member}'.");
                }
            }
        });

    static GenerationDefinition DirectDefinition(FieldMutability mutability)
    {
        var shapeId = new ShapeId("sample");
        var graph = new ShapeGraph(
            new GraphId("simulation:portable:test"),
            [new Shape(
                shapeId,
                [new FieldDefinition(
                    new FieldName("value"),
                    new ScalarTypeRef(ScalarTypeKind.Int32),
                    mutability: mutability)])]);
        return new(
            id: "simulation:portable:test",
            revision: "r1",
            shapeGraph: graph,
            root: new(
                shapeId,
                [new(new FieldName("value"), new Int32GenerationNode(minimum: 1, maximum: 10))]));
    }

    static CompiledGenerationPlan RequirePlan(GenerationDefinition definition)
    {
        var result = GenerationCompiler.Compile(definition);
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<CompiledGenerationPlan>(result.Plan);
    }

    static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Generation-definition JSON did not contain an object.");
        mutate(root);
        return root.ToJsonString();
    }

    static void ReverseMembers(JsonObject root)
    {
        var members = root["definition"]?["root"]?["members"]?.AsArray()
                      ?? throw new InvalidOperationException("Generation-definition JSON has no members array.");
        var reversed = members
            .Select(static member => member?.DeepClone())
            .Reverse()
            .ToArray();
        members.Clear();
        foreach (var member in reversed)
            members.Add(member);
    }

    public sealed record ScriptCustomer(string Name, int Age, bool IsActive, string Tier);
}

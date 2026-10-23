using System.Text.Json;
using Cohesive.Adapters.Cosmos;

namespace Cohesive.Tests.Cosmos;

// Entity and outbox documents now serialize immutable observation storage directly instead of a copied Dictionary.
// The wire shape must be byte-identical to the former copy for every built-in storage kind, and must round-trip.
public sealed class CosmosObservationDocumentShapeTests
{
    public static TheoryData<string> Storages() => new() { "owned", "small-json", "wide-json", "sorted", "nested" };

    [Theory]
    [MemberData(nameof(Storages))]
    public void Direct_observation_storage_serializes_exactly_like_the_former_dictionary_copy(string storage)
    {
        var fields = Fields(storage);
        var serializer = new CosmosSystemTextJsonSerializer();
        var direct = Serialize(serializer, Document(fields));
        var copied = Serialize(serializer, Document(new Dictionary<string, ObservationValue>(fields, StringComparer.Ordinal)));
        Assert.Equal(copied, direct);

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(direct));
        var restored = serializer.FromStream<CosmosObservationContainerDocument>(stream);
        Assert.Equal(ObservationValue.FromObject(fields), ObservationValue.FromObject(restored.Observation!));
    }

    static IReadOnlyDictionary<string, ObservationValue> Fields(string storage) => storage switch
    {
        "owned" => Parse("""{"id":"load-1","status":"ready","weight":12.50}""").WithFields(
            [new(FieldPath.FromField("added"), ObservationValue.FromBool(true))]).Fields!,
        "small-json" => Parse("""{"b":1,"a":"x"}""").Fields!,
        "wide-json" => Parse("{" + string.Join(',', Enumerable.Range(0, 24).Select(i => $"\"f{i:D2}\":{i}")) + "}").Fields!,
        "sorted" => ObservationValue.FromObject(new { Zeta = 1, Alpha = "a" }).Fields!,
        _ => Parse("""{"customer":{"name":"Acme","tags":["a","b"]},"total":null}""").Fields!
    };

    static ObservationValue Parse(string json) => ObservationValue.FromJsonElement(JsonDocument.Parse(json).RootElement);

    static CosmosObservationContainerDocument Document(IReadOnlyDictionary<string, ObservationValue> observation) => new(
        Id: "doc-1", PartitionKey: "tenant-a", DocumentKind: "entity", ObservationType: "load",
        ObservationId: "load-1", ObservationVersion: 3, Observation: observation);

    static string Serialize(CosmosSystemTextJsonSerializer serializer, CosmosObservationContainerDocument document)
    {
        using var stream = serializer.ToStream(document);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

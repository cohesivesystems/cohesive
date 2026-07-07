using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests indexed observation representation and reflection-based record mapping.
/// </summary>
public sealed class ObservationMapperTests
{
    [Fact]
    public void ObjectObservationMapper_MapsRecordUsingReflectionLayout()
    {
        var mapper = ObjectObservationMapper
            .For<OrderRecord>(new(nameof(OrderRecord)))
            .Map(nameof(OrderRecord.OrderNumber), nameof(OrderRecord.OrderNumber))
            .Map(nameof(OrderRecord.Stops), nameof(OrderRecord.Stops))
            .Map(nameof(OrderRecord.CustomerId), nameof(OrderRecord.CustomerId))
            .Build();

        var dto = new OrderRecord("ORD-42", ["PU", "DL"], "cust-7");
        var observation = mapper.Map(
            dto,
            new ObjectObservationMetadata
            {
                Id = "order-42",
                Version = 4
            });

        Assert.Equal(new ShapeId(nameof(OrderRecord)), observation.ShapeId);
        Assert.Equal(3, mapper.Layout.Count);
        Assert.Equal("ORD-42", observation.GetField(nameof(OrderRecord.OrderNumber)).GetString());
        Assert.Equal(2, observation.GetField(nameof(OrderRecord.Stops)).GetArrayLength());
        Assert.Equal("cust-7", observation.GetField(nameof(OrderRecord.CustomerId)).GetString());
    }

    [Fact]
    public void ObjectObservationMapper_MapAllFromJsonPropertyName_UsesAttributeAndFallback()
    {
        var mapper = ObjectObservationMapper
            .For<JsonNamedOrderRecord>(new(nameof(JsonNamedOrderRecord)))
            .MapAllFromJsonPropertyName(
                requireAttribute: false,
                resolveFieldIdentity: property => property.Name)
            .Build();

        var dto = new JsonNamedOrderRecord(
            OrderNumber: "ORD-77",
            CustomerId: "cust-77",
            StopCount: 6);
        var observation = mapper.Map(
            dto,
            new ObjectObservationMetadata
            {
                Id = "order-77"
            });

        Assert.Equal("ORD-77", observation.GetField(nameof(JsonNamedOrderRecord.OrderNumber)).GetString());
        Assert.Equal("cust-77", observation.GetField(nameof(JsonNamedOrderRecord.CustomerId)).GetString());
        Assert.Equal(6, observation.GetField(nameof(JsonNamedOrderRecord.StopCount)).GetInt32());
    }

    [Fact]
    public void ObjectObservationMapper_MapAllFromJsonPropertyName_WithRequireAttribute_ThrowsWhenMissing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ObjectObservationMapper
            .For<JsonNamedOrderRecord>(new(nameof(JsonNamedOrderRecord)))
            .MapAllFromJsonPropertyName(requireAttribute: true)
            .Build());

        Assert.Contains(nameof(JsonNamedOrderRecord.StopCount), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectObservationMapper_MapWithMetadataConventions_ExtractsIdAndVersion()
    {
        var mapper = ObjectObservationMapper
            .For<ConventionMetadataRecord>(new(nameof(ConventionMetadataRecord)))
            .MapAllFromJsonPropertyName(requireAttribute: true)
            .Build();

        var dto = new ConventionMetadataRecord(
            DocumentId: "order-301",
            VersionToken: 17,
            OrderNumber: "ORD-301");
        var observation = mapper.Map(dto);

        Assert.Equal("order-301", observation.Id);
        Assert.Equal(17, observation.Version);
        Assert.Equal("ORD-301", observation.GetField(nameof(ConventionMetadataRecord.OrderNumber)).GetString());
    }

    [Fact]
    public void ObjectObservationMapper_MapWithMetadataOverrides_UsesConfiguredSelectors()
    {
        var mapper = ObjectObservationMapper
            .For<OverrideMetadataRecord>(new(nameof(OverrideMetadataRecord)))
            .Map(nameof(OverrideMetadataRecord.OrderNumber), nameof(OverrideMetadataRecord.OrderNumber))
            .WithId(x => x.ExternalOrderKey)
            .WithVersion(x => x.Revision)
            .Build();

        var dto = new OverrideMetadataRecord(
            ExternalOrderKey: "order-override-1",
            ExternalEntityReference: "cust-override-1",
            Revision: "29",
            OrderNumber: "ORD-OVR-1");
        var observation = mapper.Map(dto);

        Assert.Equal("order-override-1", observation.Id);
        Assert.Equal(29, observation.Version);
        Assert.Equal("ORD-OVR-1", observation.GetField(nameof(OverrideMetadataRecord.OrderNumber)).GetString());
    }

    [Fact]
    public void ObjectObservationMapper_MapWithMetadataConventions_ThrowsWhenIdCannotBeResolved()
    {
        var mapper = ObjectObservationMapper
            .For<NoMetadataConventionRecord>(new(nameof(NoMetadataConventionRecord)))
            .Map(nameof(NoMetadataConventionRecord.OrderNumber), nameof(NoMetadataConventionRecord.OrderNumber))
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => mapper.Map(new("ORD-NO-KEY")));
        Assert.Contains("WithId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectObservationMapper_WithMetadataConventions_AllowsCustomConventionNames()
    {
        var mapper = ObjectObservationMapper
            .For<CustomConventionMetadataRecord>(new(nameof(CustomConventionMetadataRecord)))
            .Map(nameof(CustomConventionMetadataRecord.OrderNumber), nameof(CustomConventionMetadataRecord.OrderNumber))
            .WithMetadataConventions(new ObjectObservationMetadataConventionOptions
            {
                IdPropertyNames = [nameof(CustomConventionMetadataRecord.RecordKey)],
                IdJsonPropertyNames = [],
                VersionPropertyNames = [nameof(CustomConventionMetadataRecord.Revision)],
                VersionJsonPropertyNames = [],
                UseJsonPropertyNameAttributes = false
            })
            .Build();

        var observation = mapper.Map(new CustomConventionMetadataRecord("order-cc-1", 42, "ORD-CC-1"));

        Assert.Equal("order-cc-1", observation.Id);
        Assert.Equal(42, observation.Version);
    }

    [Fact]
    public void ObservationObjectMapper_MapsObservationToRecordUsingOrdinals()
    {
        var observation = new Observation(
            shapeId: new("SearchOrderDto"),
            id: "order-500",
            fields: Fields(new
            {
                OrderNumber = "ORD-500",
                StopCount = 8,
                Customer = new { id = "cust-500", name = "Contoso", segment = "Enterprise" }
            }),
            version: 1);

        var mapper = ObservationObjectMapper
            .For<SearchOrderRecord>(observation.Layout)
            .Map(nameof(SearchOrderRecord.OrderNumber), x => x.OrderNumber)
            .Map(nameof(SearchOrderRecord.StopCount), x => x.StopCount)
            .Map(
                nameof(SearchOrderRecord.Customer),
                x => x.Customer,
                element => element.Deserialize<SearchCustomerRecord>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!)
            .Build();

        var projected = mapper.Map(observation);
        Assert.Equal("ORD-500", projected.OrderNumber);
        Assert.Equal(8, projected.StopCount);
        Assert.Equal("cust-500", projected.Customer.Id);
        Assert.Equal("Contoso", projected.Customer.Name);
        Assert.Equal("Enterprise", projected.Customer.Segment);
    }

    static IReadOnlyDictionary<string, ObservationValue> Fields(object expression)
        => ObservationValue.ToFieldDictionary(expression);

    [Fact]
    public void Observation_FromJsonDocument_MapsStateObjectByLayoutOrdinals()
    {
        var layout = new ObservationLayout(
            schema: new("OrderProjection"),
            fieldNames: ["OrderNumber", "CustomerId", "StopCount"]
            );

        using var document = JsonDocument.Parse(
            """
            {
              "id": "order-100",
              "version": 9,
              "state": {
                "OrderNumber": "ORD-100",
                "CustomerId": "cust-1",
                "StopCount": 3,
                "unmapped_payload": "ignored"
              }
            }
            """);
        
        var observation = Observation.FromJsonDocument(
            layout: layout,
            document: document,
            id: "order-100",
            version: 9,
            statePropertyName: "state");

        Assert.Equal("ORD-100", observation.GetField("OrderNumber").GetString());
        Assert.Equal("cust-1", observation.GetField("CustomerId").GetString());
        Assert.Equal(3, observation.GetField("StopCount").GetInt32());
        Assert.False(observation.TryGetField("unknown_field", out _));
    }

    [Fact]
    public void Observation_FromJsonDocument_ReadsMetadataAndNestedStateWithoutExplicitArgs()
    {
        var layout = new ObservationLayout(
            schema: new("OrderProjection"),
            fieldNames: ["OrderNumber", "CustomerId", "StopCount"]
            );

        using var document = JsonDocument.Parse(
            """
            {
              "id": "order-200",
              "version": 15,
              "state": {
                "OrderNumber": "ORD-200",
                "CustomerId": "cust-200",
                "StopCount": 4
              }
            }
            """);

        var observation = Observation.FromJsonDocument(layout, document);

        Assert.Equal("order-200", observation.Id);
        Assert.Equal(15, observation.Version);
        Assert.Equal("ORD-200", observation.GetField("OrderNumber").GetString());
    }

    [Fact]
    public void Observation_FromJsonDocument_ReadsFlattenedStateWithMetadata()
    {
        var layout = new ObservationLayout(
            schema: new("OrderProjection"),
            fieldNames: ["OrderNumber", "CustomerId", "StopCount"]
            );

        using var document = JsonDocument.Parse(
            """
            {
              "id": "order-300",
              "version": 22,
              "OrderNumber": "ORD-300",
              "CustomerId": "cust-300",
              "StopCount": 5
            }
            """);

        var observation = Observation.FromJsonDocument(
            layout,
            document,
            options: new JsonObservationReadOptions
            {
                FlattenedState = true
            });

        Assert.Equal("order-300", observation.Id);
        Assert.Equal(22, observation.Version);
        Assert.Equal("ORD-300", observation.GetField("OrderNumber").GetString());
        Assert.Equal("cust-300", observation.GetField("CustomerId").GetString());
        Assert.Equal(5, observation.GetField("StopCount").GetInt32());
    }

    sealed record OrderRecord(string OrderNumber, IReadOnlyList<string> Stops, string CustomerId);

    sealed record JsonNamedOrderRecord(
        [property: JsonPropertyName("OrderNumber")] string OrderNumber,
        [property: JsonPropertyName("CustomerId")] string CustomerId,
        int StopCount);

    sealed record ConventionMetadataRecord(
        [property: JsonPropertyName("id")] string DocumentId,
        [property: JsonPropertyName("version")] long VersionToken,
        [property: JsonPropertyName("OrderNumber")] string OrderNumber);

    sealed record OverrideMetadataRecord(
        string ExternalOrderKey,
        string ExternalEntityReference,
        string Revision,
        string OrderNumber);

    sealed record NoMetadataConventionRecord(string OrderNumber);

    sealed record CustomConventionMetadataRecord(string RecordKey, long Revision, string OrderNumber);

    sealed record SearchOrderRecord(string OrderNumber, int StopCount, SearchCustomerRecord Customer);

    sealed record SearchCustomerRecord(string Id, string Name, string Segment);
}

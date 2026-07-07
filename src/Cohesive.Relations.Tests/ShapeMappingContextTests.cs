using System.Text.Json.Serialization;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Tests;

public sealed class ShapeMappingContextTests
{
    [Fact]
    public void ShapeMappingContext_GetReadableProperties_CachesByType()
    {
        var context = new ShapeMappingContext();
        var first = context.GetReadableProperties(typeof(CarrierDto));
        var second = context.GetReadableProperties(typeof(CarrierDto));
        Assert.Same(first, second);
    }

    [Fact]
    public void ShapeMappingContext_Map_ObjectToObservation_UsesCachedMapper()
    {
        var context = CreateContext();
        var observation = context.Map(new CarrierDto("carrier-1", "Acme", "MC-88"));

        Assert.Equal(new ShapeId(nameof(CarrierDto)), observation.ShapeId);
        Assert.Equal("carrier-1", observation.Id);
        Assert.Equal("Acme", observation.GetField("fld_name").GetString());
    }

    [Fact]
    public void ShapeMappingContext_Map_ObservationToObject_UsesCachedMapper()
    {
        var context = CreateContext();
        var observation = new Observation(
            shapeId: new(nameof(CarrierDto)),
            id: "carrier-1",
            fields: Fields(new { fld_id = "carrier-1", fld_name = "Acme", fld_mc_number = "MC-88" }),
            version: 1);

        var dto = context.Map<CarrierDto>(observation);

        Assert.Equal("carrier-1", dto.Id);
        Assert.Equal("Acme", dto.Name);
        Assert.Equal("MC-88", dto.McNumber);
    }

    [Fact]
    public void ShapeMappingContext_Map_ObjectToObservation_CompilesMapperOncePerTypeAndSchema()
    {
        var resolveCalls = 0;
        var context = new ShapeMappingContext
        {
            UseJsonPropertyNameAttributesForFieldIdentity = false,
            ResolveFieldIdentityFallback = property =>
            {
                resolveCalls++;
                return property.Name;
            }
        };

        _ = context.Map(new NonAttributedCarrierDto("carrier-1", "Acme"));
        _ = context.Map(new NonAttributedCarrierDto("carrier-2", "Contoso"));

        Assert.Equal(2, resolveCalls);
    }

    [Fact]
    public void ShapeMappingContext_Map_ObservationToObject_CompilesMapperOncePerTypeAndLayout()
    {
        var resolveCalls = 0;
        var context = new ShapeMappingContext
        {
            UseJsonPropertyNameAttributesForFieldIdentity = false,
            ResolveFieldIdentityFallback = property =>
            {
                resolveCalls++;
                return property.Name;
            }
        };

        var first = new Observation(
            shapeId: new(nameof(NonAttributedCarrierDto)),
            id: "carrier-1",
            fields: Fields(new { Id = "carrier-1", Name = "Acme" }),
            version: 1);
        var second = new Observation(
            shapeId: new(nameof(NonAttributedCarrierDto)),
            id: "carrier-2",
            fields: Fields(new { Id = "carrier-2", Name = "Contoso" }),
            version: 1);

        _ = context.Map<NonAttributedCarrierDto>(first);
        _ = context.Map<NonAttributedCarrierDto>(second);

        Assert.Equal(2, resolveCalls);
    }

    [Fact]
    public void ObjectObservationMapper_Build_UsesImplicitMappingsFromContext()
    {
        var mapper = ObjectObservationMapper
            .For<CarrierDto>(new(nameof(CarrierDto)), CreateContext())
            .Build();

        var observation = mapper.Map(new("carrier-1", "Acme", "MC-88"));

        Assert.Equal(["fld_id", "fld_name", "fld_mc_number"], mapper.Layout.FieldNames.ToArray());
        Assert.Equal("carrier-1", observation.Id);
        Assert.Equal("Acme", observation.GetField("fld_name").GetString());
        Assert.Equal("MC-88", observation.GetField("fld_mc_number").GetString());
    }

    [Fact]
    public void ObjectObservationMapper_Build_ExplicitMappingOverridesImplicitConvention()
    {
        var mapper = ObjectObservationMapper
            .For<CarrierDto>(new(nameof(CarrierDto)), CreateContext())
            .Map(nameof(CarrierDto.Name), "fld_legal_name")
            .Build();

        var observation = mapper.Map(new("carrier-1", "Acme", "MC-88"));

        Assert.Equal(["fld_id", "fld_legal_name", "fld_mc_number"], mapper.Layout.FieldNames.ToArray());
        Assert.Equal("Acme", observation.GetField("fld_legal_name").GetString());
        Assert.False(observation.TryGetField("fld_name", out _));
    }

    [Fact]
    public void ObservationObjectMapper_Build_UsesImplicitMappingsFromContext()
    {
        var observation = new Observation(
            shapeId: new(nameof(CarrierDto)),
            id: "carrier-1",
            fields: Fields(new { fld_id = "carrier-1", fld_name = "Acme", fld_mc_number = "MC-88" }),
            version: 1);
        var mapper = ObservationObjectMapper
            .For<CarrierDto>(observation.Layout, CreateContext())
            .Build();

        var dto = mapper.Map(observation);

        Assert.Equal("carrier-1", dto.Id);
        Assert.Equal("Acme", dto.Name);
        Assert.Equal("MC-88", dto.McNumber);
    }

    [Fact]
    public void ObservationObjectMapper_Build_ExplicitMappingOverridesImplicitConvention()
    {
        var observation = new Observation(
            shapeId: new(nameof(CarrierDto)),
            id: "carrier-1",
            fields: Fields(new { fld_id = "carrier-1", fld_legal_name = "Acme", fld_mc_number = "MC-88" }),
            version: 1);
        var mapper = ObservationObjectMapper
            .For<CarrierDto>(observation.Layout, CreateContext())
            .Map("fld_legal_name", x => x.Name)
            .Build();

        var dto = mapper.Map(observation);

        Assert.Equal("Acme", dto.Name);
        Assert.Equal("MC-88", dto.McNumber);
    }

    [Fact]
    public void ShapeMappingContext_Map_ObservationToObject_UsesDefaultForMissingOptionalMembers()
    {
        var context = CreateContext();
        var observation = new Observation(
            shapeId: new(nameof(OptionalCarrierDto)),
            id: "carrier-1",
            fields: Fields(new { fld_id = "carrier-1", fld_name = "Acme" }),
            version: 1);

        var dto = context.Map<OptionalCarrierDto>(observation);

        Assert.Equal("carrier-1", dto.Id);
        Assert.Equal("Acme", dto.Name);
        Assert.Null(dto.McNumber);
    }

    [Fact]
    public void Observation_Map_ConfiguredMapperCanRequireMissingOptionalMembers()
    {
        var context = CreateContext();
        var observation = new Observation(
            shapeId: new(nameof(OptionalCarrierDto)),
            id: "carrier-1",
            fields: Fields(new { fld_id = "carrier-1", fld_name = "Acme" }),
            version: 1);

        var ex = Assert.Throws<InvalidOperationException>(() => observation.Map<OptionalCarrierDto>(
            static builder => builder.WithMissingFieldBehavior(ObservationObjectMissingFieldBehavior.Throw),
            context));

        Assert.Contains("fld_mc_number", ex.Message, StringComparison.Ordinal);
    }

    static ShapeMappingContext CreateContext() => new()
    {
        ResolveFieldIdentityFallback = property => $"fld_{ToSnakeCase(property.Name)}",
        ObjectObservationMetadataConventions = new()
        {
            IdPropertyNames = [nameof(CarrierDto.Id)]
        }
    };

    static IReadOnlyDictionary<string, ObservationValue> Fields(object expression)
        => ObservationValue.ToFieldDictionary(expression);

    static string ToSnakeCase(string value)
    {
        var chars = value
            .SelectMany((ch, idx) =>
            {
                if (idx > 0 && char.IsUpper(ch))
                    return new[] { '_', char.ToLowerInvariant(ch) };
                return new[] { char.ToLowerInvariant(ch) };
            })
            .ToArray();
        return new string(chars);
    }

    sealed record CarrierDto(
        [property: JsonPropertyName("fld_id")] string Id,
        string Name,
        string McNumber);

    sealed record OptionalCarrierDto(
        [property: JsonPropertyName("fld_id")] string Id,
        string Name,
        string? McNumber);

    sealed record NonAttributedCarrierDto(string Id, string Name);
}

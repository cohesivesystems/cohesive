using System.Text.Json.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.Storage;

public sealed class EntityIdentitySelectorTests
{
    [Fact]
    public void Declared_json_field_selects_Sku_instead_of_Id() =>
        Assert.Equal("sku-1", EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>("sku")(new("wrong", "sku-1")));

    [Fact]
    public void Guid_entity_id_and_formattable_values_preserve_encoding()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString(), EntityRepositoryMappingExtensions.PrepareIdentitySelector<GuidRow>("Key")(new(id)));
        Assert.Equal("entity-1", EntityRepositoryMappingExtensions.PrepareIdentitySelector<EntityRow>()(new(new("entity-1"))));
        Assert.Equal("42", EntityRepositoryMappingExtensions.PrepareIdentitySelector<NumberRow>()(new(42)));
    }

    [Fact]
    public void Missing_or_ambiguous_identity_fails_during_preparation()
    {
        Assert.Throws<InvalidOperationException>(() => EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>("missing"));
        Assert.Throws<InvalidOperationException>(() => EntityRepositoryMappingExtensions.PrepareIdentitySelector<Ambiguous>("same"));
        Assert.Throws<InvalidOperationException>(() => EntityRepositoryMappingExtensions.PrepareIdentitySelector<NoIdentity>());
    }

    public sealed record Stock(string Id, [property: JsonPropertyName("sku")] string Sku);
    public sealed record GuidRow(Guid Key);
    public sealed record EntityRow(EntityId Id);
    public sealed record NumberRow(int Key);
    public sealed record NoIdentity(string Name);
    public sealed record Ambiguous([property: JsonPropertyName("same")] string First, [property: JsonPropertyName("same")] string Second);
}

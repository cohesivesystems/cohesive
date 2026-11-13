using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public class StrictEnumKeyTests
{
    [JsonConverter(typeof(JsonStringEnumConverter<Role>))]
    public enum Role { [JsonStringEnumMemberName("source-role")] Source, Target }

    [Fact]
    public void EnumDictionaryKeysUseTheSameCanonicalCatalogAsValues()
    {
        var options = StrictDocumentJson.CreateOptions();
        var values = new Dictionary<Role, int> { [Role.Source] = 1 };
        var json = Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(values, options));
        Assert.Equal("{\"source-role\":1}", json);
        Assert.True(StrictDocumentJson.TryReadCanonicalObject<Dictionary<Role, int>>(json, options, "roles", out var restored, out _));
        Assert.Equal(1, restored![Role.Source]);
        Assert.Throws<JsonException>(() => StrictDocumentJson.GetCanonicalBytes(new Dictionary<Role, int> { [(Role)99] = 1 }, options));
    }

    [Theory]
    [InlineData("{\"Source\":1}")]
    [InlineData("{\"SOURCE-ROLE\":1}")]
    [InlineData("{\"0\":1}")]
    [InlineData("{\"source-role\":1,\"source-role\":2}")]
    public void EnumKeysRejectAliasesAndDuplicates(string json)
    {
        Assert.False(StrictDocumentJson.TryReadCanonicalObject<Dictionary<Role, int>>(json,
            StrictDocumentJson.CreateOptions(), "roles", out _, out _));
    }
}

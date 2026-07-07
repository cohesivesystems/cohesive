using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class ExprSerializationTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serialize_ParameterExpr_UsesNeutralParameterDiscriminator()
    {
        var json = JsonSerializer.Serialize<Expr>(new ParameterExpr("carrierId"), JsonOptions);

        Assert.Contains("\"$expr\":\"parameter\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_ParameterExpr_UsesNeutralParameterDiscriminator()
    {
        var expr = JsonSerializer.Deserialize<Expr>(
            "{\"$expr\":\"parameter\",\"parameter\":\"carrierId\"}",
            JsonOptions);

        var parameter = Assert.IsType<ParameterExpr>(expr);
        Assert.Equal("carrierId", parameter.Parameter);
    }
}

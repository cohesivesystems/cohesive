using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.CodeGen;

public sealed class JsonContractOptionsTests
{
    [Fact]
    public void Resolve_SnapshotsAndFreezesTheDeclaredFactoryOptions()
    {
        var options = JsonContractOptionsAttribute.Resolve(Assembly(nameof(Factory.Create)));
        Assert.True(options.IsReadOnly);
        Assert.Equal("field_name", options.PropertyNamingPolicy!.ConvertName("FieldName"));
        Assert.Throws<InvalidOperationException>(() => options.PropertyNamingPolicy = null);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData(nameof(Factory.WrongType))]
    [InlineData(nameof(Factory.Null))]
    public void Resolve_RejectsMissingOrInvalidFactory(string method) =>
        Assert.Throws<InvalidOperationException>(() => JsonContractOptionsAttribute.Resolve(Assembly(method)));

    [Fact]
    public void Resolve_AttributesFactoryFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(() => JsonContractOptionsAttribute.Resolve(Assembly(nameof(Factory.Fails))));
        Assert.Contains(nameof(Factory.Fails), error.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Fact]
    public void Resolve_RequiresAnExplicitDeclaration() =>
        Assert.Throws<InvalidOperationException>(() => JsonContractOptionsAttribute.Resolve(typeof(string).Assembly));

    static Assembly Assembly(string method)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(JsonContractOptionsAttribute).GetConstructor([typeof(Type), typeof(string)])!, [typeof(Factory), method]));
        return assembly;
    }

    public static class Factory
    {
        public static JsonSerializerOptions Create() => new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        public static string WrongType() => "invalid";
        public static JsonSerializerOptions Null() => null!;
        public static JsonSerializerOptions Fails() => throw new ArgumentException("Invalid profile");
    }
}

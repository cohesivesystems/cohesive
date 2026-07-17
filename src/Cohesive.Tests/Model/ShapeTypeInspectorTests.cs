namespace Cohesive.Tests.Model;

public sealed class ShapeTypeInspectorTests
{
    [Fact]
    public void ReadablePropertyMetadata_IsSafeAcrossConcurrentColdTypes()
    {
        var types = Enumerable.Range(1, 32)
            .Select(rank => typeof(ConcurrentShape<>).MakeGenericType(typeof(int).MakeArrayType(rank)))
            .ToArray();

        Parallel.ForEach(types, type =>
        {
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var property = Assert.Single(ShapeTypeInspector.GetReadablePropertyMetadata(type));
                Assert.Equal(nameof(ConcurrentShape<int[]>.Value), property.Property.Name);
            }
        });
    }

    [Fact]
    public void ReadableProperties_UseTotalDeterministicOrderAcrossModules()
    {
        var properties = ShapeTypeInspector.GetReadableProperties(typeof(CrossModuleShape));
        var keys = properties.Select(PropertyOrderKey).ToArray();

        Assert.True(properties.Select(static property => property.Module).Distinct().Count() > 1);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Same(properties, ShapeTypeInspector.GetReadableProperties(typeof(CrossModuleShape)));
    }

    [Fact]
    public void PropertyIdentity_DistinguishesConstructedGenericDeclaringTypes()
    {
        var integer = Assert.Single(ShapeTypeInspector.GetReadableProperties(typeof(GenericShape<int>)));
        var text = Assert.Single(ShapeTypeInspector.GetReadableProperties(typeof(GenericShape<string>)));

        Assert.Same(integer.Module, text.Module);
        Assert.Equal(integer.MetadataToken, text.MetadataToken);
        Assert.NotEqual(integer.DeclaringType, text.DeclaringType);
        Assert.False(ShapeTypeInspector.IsSameProperty(integer, text));
    }

    static string PropertyOrderKey(System.Reflection.PropertyInfo property) => string.Join(
        '\u001f',
        property.DeclaringType?.Assembly.GetName().Name ?? string.Empty,
        property.Module.ScopeName,
        property.DeclaringType is null
            ? string.Empty
            : ClrShapeIdentityConvention.GetTypeId(property.DeclaringType).Value,
        property.MetadataToken.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
        property.Name,
        ClrShapeIdentityConvention.GetTypeId(property.PropertyType).Value);

    sealed class ConcurrentShape<T>
    {
        public T Value { get; init; } = default!;
    }

    sealed class CrossModuleShape : Dictionary<string, string>
    {
        public string LocalValue { get; init; } = string.Empty;
    }

    sealed class GenericShape<T>
    {
        public T Value { get; init; } = default!;
    }
}

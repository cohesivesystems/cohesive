using Cohesive.Prelude;

namespace Cohesive.Tests.Prelude;

public sealed class EnumerableExtensionsTests
{
    [Fact]
    public void WhereNotNull_FiltersReferenceNulls_AndPreservesOrder()
    {
        IEnumerable<string?> source = ["alpha", null, "beta", null, "gamma"];

        var values = source.WhereNotNull().ToArray();

        Assert.Equal(["alpha", "beta", "gamma"], values);
    }

    [Fact]
    public void WhereNotNull_FiltersNullableValueNulls_AndPreservesOrder()
    {
        IEnumerable<int?> source = [1, null, 2, null, 3];

        var values = source.WhereNotNull().ToArray();

        Assert.Equal([1, 2, 3], values);
    }

    [Fact]
    public void WhereNotNull_NullReferenceSource_ReturnsEmpty()
    {
        IEnumerable<string?>? source = null;

        var values = source.WhereNotNull().ToArray();

        Assert.Empty(values);
    }

    [Fact]
    public void WhereNotNull_NullNullableValueSource_ReturnsEmpty()
    {
        IEnumerable<int?>? source = null;

        var values = source.WhereNotNull().ToArray();

        Assert.Empty(values);
    }

    [Fact]
    public void WhereNotNull_IsDeferred_AndEnumeratesInSinglePass()
    {
        var enumerations = 0;

        IEnumerable<string?> Source()
        {
            enumerations++;
            yield return "alpha";
            yield return null;
            yield return "beta";
        }

        var values = Source().WhereNotNull();

        Assert.Equal(0, enumerations);
        Assert.Equal(["alpha", "beta"], values.ToArray());
        Assert.Equal(1, enumerations);
    }
}

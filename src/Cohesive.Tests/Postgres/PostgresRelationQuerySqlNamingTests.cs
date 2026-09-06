using Cohesive.Adapters.Sql;
using System.Text;
using Cohesive.Adapters.Postgres;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresRelationQuerySqlNamingTests
{
    [Fact]
    public void SharedAllocatorHonorsCaseSensitivityAndConfiguredByteBudget()
    {
        var sensitive = new SqlAliasAllocator(maxUtf8ByteLength: 32, StringComparer.Ordinal);
        var insensitive = new SqlAliasAllocator(maxUtf8ByteLength: 32, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Price", sensitive.Allocate("Price", "field:upper", "field"));
        Assert.Equal("price", sensitive.Allocate("price", "field:lower", "field"));
        Assert.Equal("Price", insensitive.Allocate("Price", "field:upper", "field"));
        var lower = insensitive.Allocate("price", "field:lower", "field");
        Assert.False(StringComparer.OrdinalIgnoreCase.Equals("Price", lower));
        var shortened = insensitive.Allocate(new string('界', 100), "long", "field");
        Assert.InRange(SqlUtf8.GetByteCount(shortened, nameof(shortened)), 1, 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlAliasAllocator(31, StringComparer.Ordinal));
        Assert.Throws<ArgumentNullException>(() => new SqlAliasAllocator(32, null!));
    }

    [Fact]
    public void Allocate_PreservesReadableSemanticNamesAndSanitizesPunctuation()
    {
        var aliases = new SqlAliasAllocator(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);

        Assert.Equal(
            "LoadSearchDto__customerName",
            aliases.Allocate(
                "LoadSearchDto__customerName",
                "shape:load-search|field:customerName",
                "value"));
        Assert.Equal(
            "Customer_DROP_TABLE_loads",
            aliases.Allocate(
                "Customer\"; DROP TABLE loads; --",
                "shape:hostile",
                "value"));
        Assert.Equal("result", aliases.Allocate("\";--", "shape:punctuation", "result"));
    }

    [Fact]
    public void Allocate_DisambiguatesRepeatedAndNormalizationCollidingNamesDeterministically()
    {
        var first = new SqlAliasAllocator(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);
        var second = new SqlAliasAllocator(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);

        Assert.Equal("Customer", first.Allocate("Customer", "shape:customer", "rows"));
        Assert.Equal("Customer__2", first.Allocate("Customer", "shape:customer", "rows"));
        Assert.Equal("Customer__3", first.Allocate("Customer", "shape:customer", "rows"));
        Assert.StartsWith(
            "Customer__2__",
            first.Allocate("Customer__2", "shape:customer-2", "rows"),
            StringComparison.Ordinal);

        var readable = first.Allocate("customer name", "field:customer name", "field");
        var collision = first.Allocate("customer-name", "field:customer-name", "field");
        Assert.Equal("customer_name", readable);
        Assert.StartsWith("customer_name__", collision, StringComparison.Ordinal);
        Assert.NotEqual(readable, collision);
        Assert.NotEqual(
            collision,
            first.Allocate(collision, "field:literal-collision-suffix", "field"));

        Assert.Equal("customer_name", second.Allocate("customer name", "field:customer name", "field"));
        Assert.Equal(collision, second.Allocate("customer-name", "field:customer-name", "field"));
    }

    [Fact]
    public void Allocate_ShortensAsciiAndUnicodeAtThePostgresUtf8Boundary()
    {
        var first = new SqlAliasAllocator(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);
        var second = new SqlAliasAllocator(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);
        var longAscii = new string('a', 100);
        var longUnicode = string.Concat(Enumerable.Repeat("顧客", 20));

        var ascii = first.Allocate(longAscii, "field:long-ascii", "field");
        var unicode = first.Allocate(longUnicode, "field:long-unicode", "field");
        var repeatedAscii = first.Allocate(longAscii, "field:long-ascii", "field");

        Assert.True(Encoding.UTF8.GetByteCount(ascii) <= PostgresSqlDialect.StandardMaxUtf8ByteLength);
        Assert.True(Encoding.UTF8.GetByteCount(unicode) <= PostgresSqlDialect.StandardMaxUtf8ByteLength);
        Assert.True(Encoding.UTF8.GetByteCount(repeatedAscii) <= PostgresSqlDialect.StandardMaxUtf8ByteLength);
        Assert.NotEqual(ascii, repeatedAscii);
        Assert.Equal(ascii, second.Allocate(longAscii, "field:long-ascii", "field"));
        Assert.Equal(unicode, second.Allocate(longUnicode, "field:long-unicode", "field"));
        _ = new SqlIdentifier(ascii);
        _ = new SqlIdentifier(unicode);
    }

    [Fact]
    public void Allocate_NormalizesComposedUnicodeAndReplacesInvalidUtf16()
    {
        var aliases = new SqlAliasAllocator(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);

        Assert.Equal("Café", aliases.Allocate("Cafe\u0301", "field:cafe", "field"));
        Assert.Equal("bad_name", aliases.Allocate("bad\ud800name", "field:invalid", "field"));
    }
}

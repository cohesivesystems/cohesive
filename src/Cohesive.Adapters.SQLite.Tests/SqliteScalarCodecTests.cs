using System.Globalization;
using Cohesive.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteScalarCodecTests
{
    public static TheoryData<ScalarTypeKind, ObservationValue> Scalars => new()
    {
        { ScalarTypeKind.Bool, ObservationValue.FromBool(false) },
        { ScalarTypeKind.Bool, ObservationValue.FromBool(true) },
        { ScalarTypeKind.Int32, ObservationValue.FromInt64(int.MinValue) },
        { ScalarTypeKind.Int32, ObservationValue.FromInt64(int.MaxValue) },
        { ScalarTypeKind.Int64, ObservationValue.FromInt64(long.MinValue) },
        { ScalarTypeKind.Int64, ObservationValue.FromInt64(long.MaxValue) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(decimal.MinValue) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(decimal.MaxValue) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(0.0000000000000000000000000001m) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(123.4500m) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(0.000m) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(100.00m) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(long.MaxValue) },
        { ScalarTypeKind.Decimal, ObservationValue.FromDecimal(long.MinValue) },
        { ScalarTypeKind.String, ObservationValue.FromString("quotes ' \" ; -- \0 and Unicode λ 🚀") },
        { ScalarTypeKind.Guid, ObservationValue.FromString("b0250342-90d2-4771-8f11-49c1cd0f5cc3") },
        { ScalarTypeKind.Date, ObservationValue.FromDateOnly(DateOnly.MinValue) },
        { ScalarTypeKind.Date, ObservationValue.FromDateOnly(DateOnly.MaxValue) },
        { ScalarTypeKind.Instant, ObservationValue.FromDateTimeOffset(new(2026, 9, 5, 12, 34, 56, TimeSpan.FromHours(-7))) },
        { ScalarTypeKind.Instant, ObservationValue.FromDateTimeOffset(DateTimeOffset.MaxValue) },
        { ScalarTypeKind.DateTime, ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 9, 5, 12, 34, 56, TimeSpan.FromHours(14)).AddTicks(1)) },
        { ScalarTypeKind.Bytes, ObservationValue.FromBytes(new byte[] { 0, 1, 255 }) },
        { ScalarTypeKind.Bytes, ObservationValue.FromBytes(ReadOnlyMemory<byte>.Empty) }
    };

    [Theory]
    [MemberData(nameof(Scalars))]
    public void ScalarBoundariesRoundTripThroughARealStrictColumn(ScalarTypeKind kind, ObservationValue value)
    {
        using var fixture = new DatabaseFixture();
        ValueContract contract = new(new ScalarTypeRef(kind));
        var storageType = SqliteScalarCodec.GetStorageType(contract);
        using var connection = fixture.Database.OpenConnection();
        var declaration = storageType.ToString().ToUpperInvariant();
        using (var create = fixture.Database.CreateCommand(connection, null, $"CREATE TABLE sample (value {declaration} NOT NULL) STRICT;"))
            create.ExecuteNonQuery();
        using (var insert = fixture.Database.CreateCommand(connection, null, "INSERT INTO sample VALUES ($value);",
            SqliteScalarCodec.CreateParameter("$value", contract, value)))
            insert.ExecuteNonQuery();
        using var query = fixture.Database.CreateCommand(connection, null, "SELECT value, typeof(value) FROM sample;");
        using var row = query.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(declaration.ToLowerInvariant(), row.GetString(1));
        Assert.Equal(value, SqliteScalarCodec.Decode(contract, row.GetValue(0)));
    }

    [Fact]
    public void CatalogCoverageIsExhaustiveOverSemanticScalarKinds()
    {
        var covered = Scalars.Select(row => (ScalarTypeKind)row[0]).ToHashSet();
        Assert.Equal(Enum.GetValues<ScalarTypeKind>().Order(), covered.Order());
        foreach (var kind in covered)
            Assert.NotEqual(SqliteType.Real, SqliteScalarCodec.GetStorageType(new(new ScalarTypeRef(kind))));
    }

    [Fact]
    public void EncodingsAreCultureIndependentAndDecimalsNeverUseReal()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            ValueContract contract = new(new ScalarTypeRef(ScalarTypeKind.Decimal));
            Assert.Equal(SqliteType.Text, SqliteScalarCodec.GetStorageType(contract));
            Assert.Equal("123.4500", SqliteScalarCodec.Encode(contract, ObservationValue.FromDecimal(123.4500m)));
            Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(contract, 123.45d));
            Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Encode(contract, ObservationValue.FromDouble(123.45d)));
            Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(contract, "0.12345678901234567890123456789"));
            Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(contract, "1e-40"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void CodecValidatesRangesEnumsKindsAndExplicitNullability()
    {
        ValueContract integer = new(new ScalarTypeRef(ScalarTypeKind.Int32));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(integer, (long)int.MaxValue + 1));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Encode(integer, ObservationValue.FromInt64((long)int.MaxValue + 1)));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(integer, "1"));
        ValueContract boolean = new(new ScalarTypeRef(ScalarTypeKind.Bool));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(boolean, 2L));
        ValueContract enumeration = new(new EnumTypeRef("run-status", ["pending-run", "approved-run"]));
        Assert.Equal(ObservationValue.FromString("approved-run"), SqliteScalarCodec.Decode(enumeration, "approved-run"));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(enumeration, "Approved"));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(integer, DBNull.Value));
        ValueContract nullable = new(integer.Type, nullability: FieldNullability.Nullable);
        Assert.Equal(DBNull.Value, SqliteScalarCodec.Encode(nullable, ObservationValue.Null));
        Assert.Equal(ObservationValue.Null, SqliteScalarCodec.Decode(nullable, DBNull.Value));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Encode(nullable, ObservationValue.Undefined));
        Assert.Throws<NotSupportedException>(() => SqliteScalarCodec.GetStorageType(new(new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String)))));
    }

    [Fact]
    public void BytesHaveExplicitOwnershipAndInvalidUnicodeIsRejected()
    {
        ValueContract bytes = new(new ScalarTypeRef(ScalarTypeKind.Bytes));
        var native = (byte[])SqliteScalarCodec.Encode(bytes, ObservationValue.FromBytes(new byte[] { 1, 2 }));
        var decoded = SqliteScalarCodec.Decode(bytes, native);
        native[0] = 99;
        Assert.Equal(1, decoded.GetBytes().Span[0]);
        ValueContract text = new(new ScalarTypeRef(ScalarTypeKind.String));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Encode(text, ObservationValue.FromString("\ud800")));
        Assert.Throws<ArgumentException>(() => SqliteScalarCodec.Decode(text, "\udc00"));
    }
}

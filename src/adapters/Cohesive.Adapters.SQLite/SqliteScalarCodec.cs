using System.Globalization;
using System.Text;
using Cohesive.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Exact scalar storage encodings shared by SQLite repository implementations.</summary>
/// <remarks>
/// This profile accepts canonical observation kinds, not coercible JSON strings or binary floating point.
/// Decimal TEXT preserves the canonical observation value and its retained scale; integral decimals have already
/// normalized to Int64 in ObservationValue. Temporal TEXT preserves the offset and ticks. These encodings
/// do not grant SQL numeric arithmetic or chronological ordering over arbitrary stored text. Use matching STRICT
/// column declarations and BINARY text collation. Named/structural contracts require another explicit realization.
/// </remarks>
public static class SqliteScalarCodec
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly EncodingEntry Integer = new(SqliteType.Integer, ObservationValueKind.Int64,
        value => value.GetInt64(), value => ObservationValue.FromInt64((long)value));
    static readonly EncodingEntry Text = new(SqliteType.Text, ObservationValueKind.String,
        value => RequireText(value.GetString()!), value => ObservationValue.FromString(RequireText((string)value)));
    static readonly EncodingEntry Temporal = new(SqliteType.Text, ObservationValueKind.DateTimeOffset,
        value => value.GetDateTimeOffset().ToString("O", Invariant), value => ReadTemporal((string)value));
    static readonly IReadOnlyDictionary<ScalarTypeKind, EncodingEntry> Encodings = new Dictionary<ScalarTypeKind, EncodingEntry>
    {
        [ScalarTypeKind.Bool] = new(SqliteType.Integer, ObservationValueKind.Bool,
            value => value.GetBoolean() ? 1L : 0L,
            value => (long)value switch { 0 => ObservationValue.FromBool(false), 1 => ObservationValue.FromBool(true), _ => throw InvalidStoredValue() }),
        [ScalarTypeKind.Int32] = Integer,
        [ScalarTypeKind.Int64] = Integer,
        [ScalarTypeKind.Decimal] = new(SqliteType.Text, ObservationValueKind.Decimal,
            value => value.GetDecimal().ToString(Invariant), value => ReadDecimal((string)value)),
        [ScalarTypeKind.String] = Text,
        [ScalarTypeKind.Guid] = Text,
        [ScalarTypeKind.Date] = new(SqliteType.Text, ObservationValueKind.DateOnly,
            value => value.GetDateOnly().ToString("O", Invariant), value => ReadDate((string)value)),
        [ScalarTypeKind.DateTime] = Temporal,
        [ScalarTypeKind.Instant] = Temporal,
        [ScalarTypeKind.Bytes] = new(SqliteType.Blob, ObservationValueKind.Bytes,
            value => value.GetBytes().ToArray(), value => ObservationValue.FromBytes((byte[])value))
    };

    /// <summary>Resolves the SQLite storage class for a supported, single-valued inline contract.</summary>
    /// <param name="contract">Scalar, enum, entity-reference, or scalar quantity contract.</param>
    /// <returns>The required column and parameter storage class; Decimal is always TEXT.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is null.</exception>
    /// <exception cref="NotSupportedException">The contract requires a named, structural, array, unknown, or graph-qualified encoding.</exception>
    public static SqliteType GetStorageType(ValueContract contract) => Resolve(contract).StorageType;

    /// <summary>Encodes a canonical scalar without implicit provider coercion.</summary>
    /// <param name="contract">Full value contract, including nullability.</param>
    /// <param name="value">Canonical observation scalar or permitted explicit null; Undefined/absence is not SQL NULL.</param>
    /// <returns>A provider-native long, string, owned byte array, or DBNull. Byte storage is defensively copied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is null.</exception>
    /// <exception cref="NotSupportedException">The contract has no supported scalar realization.</exception>
    /// <exception cref="ArgumentException">The value violates the contract, uses a noncanonical kind, or contains invalid Unicode.</exception>
    public static object Encode(ValueContract contract, ObservationValue value)
    {
        var encoding = Resolve(contract);
        if (!contract.IsSatisfiedByConstant(value) || value.Kind == ObservationValueKind.Undefined)
            throw new ArgumentException("The scalar value does not satisfy its declared contract.", nameof(value));
        if (value.Kind == ObservationValueKind.Null)
            return DBNull.Value;
        // ObservationValue canonically represents integral decimals in Int64 when they fit that range.
        var canonicalIntegralDecimal = encoding.Kind == ObservationValueKind.Decimal && value.Kind == ObservationValueKind.Int64;
        if (value.Kind != encoding.Kind && !canonicalIntegralDecimal)
            throw new ArgumentException($"The SQLite scalar profile requires observation kind {encoding.Kind}, not {value.Kind}.", nameof(value));
        return encoding.Write(value);
    }

    /// <summary>Decodes and validates a native SQLite scalar value using the same encoding catalog.</summary>
    /// <param name="contract">Full value contract, including enum members, range, and nullability.</param>
    /// <param name="value">Native value returned by SqliteDataReader.GetValue; null must be represented as DBNull.</param>
    /// <returns>A validated scalar observation; byte storage is copied into observation ownership.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="NotSupportedException">The contract has no supported scalar realization.</exception>
    /// <exception cref="ArgumentException">Storage class, scalar text, or decoded value violates the encoding/contract.</exception>
    public static ObservationValue Decode(ValueContract contract, object value)
    {
        var encoding = Resolve(contract);
        ArgumentNullException.ThrowIfNull(value);
        if (value is DBNull)
        {
            if (!contract.IsSatisfiedByConstant(ObservationValue.Null))
                throw InvalidStoredValue();
            return ObservationValue.Null;
        }
        var correctStorageClass = encoding.StorageType switch
        {
            SqliteType.Integer => value is long,
            SqliteType.Text => value is string,
            SqliteType.Blob => value is byte[],
            _ => false
        };
        if (!correctStorageClass)
            throw InvalidStoredValue();
        var decoded = encoding.Read(value);
        if (!contract.IsSatisfiedByConstant(decoded))
            throw InvalidStoredValue();
        return decoded;
    }

    /// <summary>Creates a fresh parameter with explicit storage type and validated encoded ownership.</summary>
    /// <param name="name">SQLite parameter name beginning with $, @, or :.</param>
    /// <param name="contract">Supported scalar contract.</param>
    /// <param name="value">Canonical observation to encode.</param>
    /// <returns>A parameter ready for transfer to a command.</returns>
    /// <exception cref="ArgumentException">The parameter name or value is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is null.</exception>
    /// <exception cref="NotSupportedException">The contract has no supported scalar realization.</exception>
    public static SqliteParameter CreateParameter(string name, ValueContract contract, ObservationValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length < 2 || name[0] is not ('$' or '@' or ':') || RequireText(name).Contains('\0'))
            throw new ArgumentException("Use a named SQLite parameter beginning with $, @, or :.", nameof(name));
        return new(name, GetStorageType(contract)) { Value = Encode(contract, value) };
    }

    static EncodingEntry Resolve(ValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Cardinality != FieldCardinality.Single || contract.Shape is not null)
            throw new NotSupportedException("The SQLite scalar profile requires a single-valued inline contract.");
        var kind = contract.Type switch
        {
            ScalarTypeRef scalar => scalar.Kind,
            QuantityTypeRef quantity => quantity.BaseKind,
            EnumTypeRef or EntityReferenceTypeRef => ScalarTypeKind.String,
            _ => (ScalarTypeKind)(-1)
        };
        return Encodings.TryGetValue(kind, out var encoding) ? encoding
            : throw new NotSupportedException("The value type has no supported SQLite scalar encoding.");
    }

    internal static string RequireText(string text)
    {
        // SQLite is UTF-8; reject unpaired UTF-16 surrogates instead of silently substituting replacement characters.
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException exception) { throw new ArgumentException("SQLite text must be valid Unicode.", nameof(text), exception); }
        return text;
    }

    static ObservationValue ReadDecimal(string text)
    {
        if (text.Length > 32 || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out var value)
            || value.ToString(Invariant) != text)
            throw InvalidStoredValue();
        return ObservationValue.FromDecimal(value);
    }

    static ObservationValue ReadTemporal(string text)
    {
        if (!DateTimeOffset.TryParseExact(text, "O", Invariant, DateTimeStyles.None, out var value)
            || value.ToString("O", Invariant) != text)
            throw InvalidStoredValue();
        return ObservationValue.FromDateTimeOffset(value);
    }

    static ObservationValue ReadDate(string text)
    {
        if (!DateOnly.TryParseExact(text, "O", Invariant, DateTimeStyles.None, out var value))
            throw InvalidStoredValue();
        return ObservationValue.FromDateOnly(value);
    }

    static ArgumentException InvalidStoredValue() => new("The stored SQLite value violates its exact scalar encoding or declared contract.", "value");
    sealed record EncodingEntry(SqliteType StorageType, ObservationValueKind Kind,
        Func<ObservationValue, object> Write, Func<object, ObservationValue> Read);
}

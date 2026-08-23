using System.Globalization;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Expressions;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Defines the exact canonical value closure shared by Cosmos relation/query compilation, invocation binding,
/// artifact affinity validation, and physical result decoding.
/// </summary>
internal static class CosmosRelationQueryCanonicalValueCodec
{
    /// <summary>Tests whether one portable type has an exact Cosmos runtime-parameter representation.</summary>
    /// <param name="type">Portable runtime-parameter type.</param>
    /// <returns><see langword="true"/> when the type is in the exact scalar or recursive array closure.</returns>
    internal static bool SupportsRuntimeParameterType(TypeRef? type) => type switch
    {
        ScalarTypeRef
        {
            Kind: ScalarTypeKind.Bool
                or ScalarTypeKind.Int32
                or ScalarTypeKind.String
                or ScalarTypeKind.Guid
                or ScalarTypeKind.Date
                or ScalarTypeKind.DateTime
                or ScalarTypeKind.Instant
        } => true,
        ArrayTypeRef array => SupportsRuntimeParameterType(array.ElementType),
        _ => false
    };

    /// <summary>Resolves the exact physical result encoding for one supported scalar value contract.</summary>
    /// <param name="contract">Canonical semantic result contract.</param>
    /// <param name="encoding">Resolved physical encoding when successful.</param>
    /// <returns><see langword="true"/> when the contract has one ordinary scalar Cosmos result encoding.</returns>
    internal static bool TryResolveResultEncoding(
        ValueContract contract,
        out CosmosRelationQueryResultValueEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Cardinality != FieldCardinality.Single)
        {
            encoding = default;
            return false;
        }

        switch (contract.GetEffectiveType())
        {
            case ScalarTypeRef { Kind: ScalarTypeKind.Bool }:
                encoding = CosmosRelationQueryResultValueEncoding.JsonBoolean;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int32 }:
                encoding = CosmosRelationQueryResultValueEncoding.JsonInt32;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.String }:
            case ScalarTypeRef { Kind: ScalarTypeKind.Guid }:
            case ScalarTypeRef { Kind: ScalarTypeKind.Date }:
            case ScalarTypeRef { Kind: ScalarTypeKind.DateTime or ScalarTypeKind.Instant }:
                encoding = CosmosRelationQueryResultValueEncoding.JsonString;
                return true;
            default:
                encoding = default;
                return false;
        }
    }

    /// <summary>Tests whether retained result encoding metadata exactly represents its semantic contract.</summary>
    /// <param name="contract">Retained semantic result contract.</param>
    /// <param name="encoding">Retained physical result encoding.</param>
    /// <returns><see langword="true"/> when the pair belongs to the exact compiler/runtime closure.</returns>
    internal static bool IsResultEncodingCompatible(
        ValueContract contract,
        CosmosRelationQueryResultValueEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (!Enum.IsDefined(encoding))
            return false;
        if (encoding == CosmosRelationQueryResultValueEncoding.ExactCountInteger)
        {
            return contract is
            {
                Cardinality: FieldCardinality.Single,
                Presence: FieldPresence.Required,
                Nullability: FieldNullability.NonNullable
            }
            && contract.GetEffectiveType() is ScalarTypeRef { Kind: ScalarTypeKind.Int64 };
        }
        if (encoding == CosmosRelationQueryResultValueEncoding.JsonExactInt64)
        {
            return contract.Cardinality == FieldCardinality.Single
                   && contract.GetEffectiveType() is ScalarTypeRef { Kind: ScalarTypeKind.Int64 };
        }

        return TryResolveResultEncoding(contract, out var expected) && expected == encoding;
    }

    /// <summary>Validates and retains one canonical invocation value without changing its semantic representation.</summary>
    /// <param name="contract">Effective compiled parameter contract.</param>
    /// <param name="value">Supplied or defaulted invocation value.</param>
    /// <param name="encoded">The unchanged canonical scalar or recursively validated array value when successful.</param>
    /// <param name="valueDomain">Additional exact physical representation required by the compiled use site.</param>
    /// <returns>
    /// <see langword="true"/> when the value satisfies the contract and already has the exact representation that
    /// Cosmos result decoding returns for the same semantic type.
    /// </returns>
    internal static bool TryEncodeRuntimeParameter(
        ValueContract contract,
        ObservationValue value,
        out ObservationValue encoded,
        CosmosRelationQueryParameterValueDomain valueDomain = CosmosRelationQueryParameterValueDomain.Canonical)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (!Enum.IsDefined(valueDomain))
        {
            encoded = default;
            return false;
        }
        if (!contract.IsSatisfiedByConstant(value) || value.Kind == ObservationValueKind.Undefined)
        {
            encoded = default;
            return false;
        }
        if (value.Kind == ObservationValueKind.Null)
        {
            encoded = ObservationValue.Null;
            return true;
        }
        if (contract.GetEffectiveType() is not { } type)
        {
            encoded = default;
            return false;
        }

        if (valueDomain == CosmosRelationQueryParameterValueDomain.UtcRoundTripInstant
            && type is ScalarTypeRef { Kind: ScalarTypeKind.Instant })
        {
            if (!IsCanonicalUtcInstant(value))
            {
                encoded = default;
                return false;
            }

            encoded = value;
            return true;
        }

        if (!TryEncodeRuntimeValue(type, value, out encoded))
            return false;
        return valueDomain switch
        {
            CosmosRelationQueryParameterValueDomain.Canonical => true,
            CosmosRelationQueryParameterValueDomain.UtcRoundTripDateTime => IsCanonicalUtcDateTime(value),
            _ => false
        };
    }

    static bool IsCanonicalUtcDateTime(ObservationValue value)
    {
        if (value.Kind != ObservationValueKind.String
            || !DateTime.TryParseExact(
                value.String,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Kind != DateTimeKind.Utc)
        {
            return false;
        }
        return string.Equals(
            value.String,
            parsed.ToString("O", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    static bool IsCanonicalUtcInstant(ObservationValue value)
    {
        if (!value.TryGetInstant(out var parsed) || parsed.Offset != TimeSpan.Zero)
            return false;
        return string.Equals(
            value.String,
            parsed.ToString("O", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    /// <summary>Decodes one non-null physical result value according to retained exact encoding metadata.</summary>
    /// <param name="element">Physical JSON value.</param>
    /// <param name="contract">Retained semantic value contract used to interpret temporal encodings.</param>
    /// <param name="encoding">Expected exact physical encoding.</param>
    /// <param name="value">Canonical observation value when successful.</param>
    /// <returns><see langword="true"/> when the JSON value exactly satisfies the retained encoding.</returns>
    internal static bool TryDecodeResultValue(
        JsonElement element,
        ValueContract contract,
        CosmosRelationQueryResultValueEncoding encoding,
        out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(contract);
        switch (encoding)
        {
            case CosmosRelationQueryResultValueEncoding.JsonBoolean:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = ObservationValue.FromBool(element.GetBoolean());
                    return true;
                }
                break;
            case CosmosRelationQueryResultValueEncoding.JsonInt32:
                if (TryDecodeExactInteger(element, int.MinValue, int.MaxValue, out var int32))
                {
                    value = ObservationValue.FromInt64(int32);
                    return true;
                }
                break;
            case CosmosRelationQueryResultValueEncoding.JsonString:
                if (element.ValueKind == JsonValueKind.String)
                {
                    value = ObservationValue.FromString(element.GetString());
                    return true;
                }
                break;
            case CosmosRelationQueryResultValueEncoding.ExactCountInteger:
                if (TryDecodeExactInteger(
                        element,
                        minimum: 0,
                        CosmosRelationQueryTargetProfile.MaximumExactInteger,
                        out var count))
                {
                    value = ObservationValue.FromInt64(count);
                    return true;
                }
                break;
            case CosmosRelationQueryResultValueEncoding.JsonExactInt64:
                if (TryDecodeExactInteger(
                        element,
                        -CosmosRelationQueryTargetProfile.MaximumExactInteger,
                        CosmosRelationQueryTargetProfile.MaximumExactInteger,
                        out var exactInt64))
                {
                    value = ObservationValue.FromInt64(exactInt64);
                    return true;
                }
                break;
            default:
                break;
        }

        value = default;
        return false;
    }

    static bool TryEncodeRuntimeValue(
        TypeRef type,
        ObservationValue value,
        out ObservationValue encoded) => type switch
    {
        ScalarTypeRef scalar => TryEncodeScalar(scalar.Kind, value, out encoded),
        ArrayTypeRef array => TryEncodeArray(array.ElementType, value, out encoded),
        _ => Fail(out encoded)
    };

    static bool TryEncodeScalar(
        ScalarTypeKind kind,
        ObservationValue value,
        out ObservationValue encoded)
    {
        switch (kind)
        {
            case ScalarTypeKind.Bool when value.Kind == ObservationValueKind.Bool:
                encoded = value;
                return true;
            case ScalarTypeKind.Int32
                when value.Kind == ObservationValueKind.Int64
                     && value.Int64 is >= int.MinValue and <= int.MaxValue:
                encoded = value;
                return true;
            case ScalarTypeKind.String when value.Kind == ObservationValueKind.String:
                encoded = value;
                return true;
            case ScalarTypeKind.Guid or ScalarTypeKind.Date or ScalarTypeKind.DateTime
                when value.Kind == ObservationValueKind.String:
                encoded = value;
                return true;
            case ScalarTypeKind.Instant when value.Kind == ObservationValueKind.String:
                encoded = value;
                return true;
            default:
                encoded = default;
                return false;
        }
    }

    static bool TryEncodeArray(
        TypeRef elementType,
        ObservationValue value,
        out ObservationValue encoded)
    {
        if (value.Kind != ObservationValueKind.Array)
        {
            encoded = default;
            return false;
        }

        var items = value.EnumerateArray();
        for (var index = 0; index < items.Length; index++)
        {
            if (!TryEncodeRuntimeValue(elementType, items[index], out _))
            {
                encoded = default;
                return false;
            }
        }

        encoded = value;
        return true;
    }

    static bool TryDecodeExactInteger(
        JsonElement element,
        long minimum,
        long maximum,
        out long value)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            value = default;
            return false;
        }

        if (element.TryGetInt64(out var integer))
        {
            if (integer >= minimum && integer <= maximum)
            {
                value = integer;
                return true;
            }

            value = default;
            return false;
        }

        if (TryParseExactJsonInteger(element.GetRawText().AsSpan(), out var parsed)
            && parsed >= minimum
            && parsed <= maximum)
        {
            value = parsed;
            return true;
        }

        value = default;
        return false;
    }

    static bool TryParseExactJsonInteger(ReadOnlySpan<char> token, out long value)
    {
        var index = 0;
        var negative = token.Length != 0 && token[0] == '-';
        if (negative)
            index++;

        var integerStart = index;
        while (index < token.Length && token[index] is >= '0' and <= '9')
            index++;
        var integerLength = index - integerStart;
        if (integerLength == 0)
            return Fail(out value);

        var fractionStart = index;
        var fractionLength = 0;
        if (index < token.Length && token[index] == '.')
        {
            index++;
            fractionStart = index;
            while (index < token.Length && token[index] is >= '0' and <= '9')
                index++;
            fractionLength = index - fractionStart;
            if (fractionLength == 0)
                return Fail(out value);
        }

        long exponent = 0;
        if (index < token.Length && token[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = index < token.Length && token[index] == '-';
            if (index < token.Length && token[index] is '+' or '-')
                index++;
            var exponentStart = index;
            var exponentLimit = (long)token.Length + 20;
            while (index < token.Length && token[index] is >= '0' and <= '9')
            {
                exponent = Math.Min(exponentLimit, (exponent * 10) + (token[index] - '0'));
                index++;
            }
            if (index == exponentStart)
                return Fail(out value);
            if (exponentNegative)
                exponent = -exponent;
        }
        if (index != token.Length)
            return Fail(out value);

        var totalDigits = integerLength + fractionLength;
        var firstNonZero = -1;
        for (var digitIndex = 0; digitIndex < totalDigits; digitIndex++)
        {
            if (GetDigit(token, integerStart, integerLength, fractionStart, digitIndex) != '0')
            {
                firstNonZero = digitIndex;
                break;
            }
        }
        if (firstNonZero < 0)
        {
            value = 0;
            return true;
        }

        var scale = exponent - fractionLength;
        var removedDigits = scale < 0 ? -scale : 0;
        if (removedDigits > totalDigits)
            return Fail(out value);
        for (long removed = 0; removed < removedDigits; removed++)
        {
            var digitIndex = totalDigits - 1 - (int)removed;
            if (GetDigit(token, integerStart, integerLength, fractionStart, digitIndex) != '0')
                return Fail(out value);
        }

        var retainedDigits = totalDigits - (int)removedDigits;
        if (firstNonZero >= retainedDigits)
            return Fail(out value);
        var appendedZeros = scale > 0 ? scale : 0;
        if ((long)retainedDigits - firstNonZero + appendedZeros > 19)
            return Fail(out value);

        var magnitudeLimit = negative ? 9_223_372_036_854_775_808UL : long.MaxValue;
        ulong magnitude = 0;
        for (var digitIndex = firstNonZero; digitIndex < retainedDigits; digitIndex++)
        {
            var digit = (uint)(GetDigit(token, integerStart, integerLength, fractionStart, digitIndex) - '0');
            if (magnitude > (magnitudeLimit - digit) / 10)
                return Fail(out value);
            magnitude = (magnitude * 10) + digit;
        }
        for (long appended = 0; appended < appendedZeros; appended++)
        {
            if (magnitude > magnitudeLimit / 10)
                return Fail(out value);
            magnitude *= 10;
        }

        value = negative
            ? magnitude == 9_223_372_036_854_775_808UL
                ? long.MinValue
                : -(long)magnitude
            : (long)magnitude;
        return true;
    }

    static char GetDigit(
        ReadOnlySpan<char> token,
        int integerStart,
        int integerLength,
        int fractionStart,
        int digitIndex) => digitIndex < integerLength
        ? token[integerStart + digitIndex]
        : token[fractionStart + digitIndex - integerLength];

    static bool Fail<T>(out T value)
    {
        value = default!;
        return false;
    }
}

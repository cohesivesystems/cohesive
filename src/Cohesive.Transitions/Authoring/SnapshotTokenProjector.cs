using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Transitions.Authoring;

public static class SnapshotTokenProjector
{
    const byte NullMarker = 0;
    const byte Int64Marker = 1;
    const byte DoubleMarker = 2;
    const byte BoolMarker = 3;
    const byte StringMarker = 4;
    const byte BytesMarker = 5;
    const byte ObjectMarker = 6;
    const byte ArrayMarker = 7;

    public static string Compute(
        IReadOnlyDictionary<string, ObservationValue> stateByFieldName,
        IReadOnlyList<string> fieldNames
        )
    {
        ArgumentNullException.ThrowIfNull(stateByFieldName);
        ArgumentNullException.ThrowIfNull(fieldNames);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var fieldName in fieldNames.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            AppendString(hash, fieldName);
            AppendObservationValue(
                hash,
                stateByFieldName.TryGetValue(fieldName, out var value)
                    ? value
                    : ObservationValue.Null);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static void AppendObservationValue(IncrementalHash hash, ObservationValue value)
    {
        switch (value.Kind)
        {
            case ObservationValueKind.Undefined:
            case ObservationValueKind.Null:
                AppendByte(hash, NullMarker);
                return;
            case ObservationValueKind.Int64:
                AppendByte(hash, Int64Marker);
                AppendInt64(hash, value.Int64);
                return;
            case ObservationValueKind.Double:
                AppendByte(hash, DoubleMarker);
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(value.Double));
                return;
            case ObservationValueKind.Bool:
                AppendByte(hash, BoolMarker);
                AppendByte(hash, value.Bool ? (byte)1 : (byte)0);
                return;
            case ObservationValueKind.String:
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                AppendByte(hash, StringMarker);
                AppendNullableString(hash, value.String);
                return;
            case ObservationValueKind.Bytes:
                AppendByte(hash, BytesMarker);
                AppendBytes(hash, value.Bytes.Span);
                return;
            case ObservationValueKind.Object:
            {
                AppendByte(hash, ObjectMarker);
                var objectValues = value.Fields;
                if (objectValues is null)
                {
                    AppendInt32(hash, 0);
                    return;
                }

                AppendInt32(hash, objectValues.Count);
                foreach (var (key, child) in objectValues.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    AppendString(hash, key);
                    AppendObservationValue(hash, child);
                }

                return;
            }
            case ObservationValueKind.Array:
            {
                AppendByte(hash, ArrayMarker);
                var arrayValues = value.Array;
                if (arrayValues is null)
                {
                    AppendInt32(hash, 0);
                    return;
                }

                AppendInt32(hash, arrayValues.Length);
                foreach (var child in arrayValues)
                    AppendObservationValue(hash, child);

                return;
            }
            default:
                throw new InvalidOperationException($"Unsupported observation value kind '{value.Kind}'.");
        }
    }

    static void AppendNullableString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendInt32(hash, -1);
            return;
        }

        AppendString(hash, value);
    }

    static void AppendString(IncrementalHash hash, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendBytes(hash, utf8);
    }

    static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendInt32(hash, value.Length);
        if (!value.IsEmpty)
            hash.AppendData(value);
    }

    static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value;
        hash.AppendData(bytes);
    }
}

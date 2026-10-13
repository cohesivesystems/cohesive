using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Cohesive.Model.Serialization;

// Shared binary fingerprint tokens: UTF-8 strings/bytes have signed big-endian Int32 lengths;
// integers are big-endian and booleans are one byte. Output is borrowed, never retained.
internal static class LengthPrefixedFingerprintEncoding
{
    internal static void Append(IBufferWriter<byte> output, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Append(output, length);
        Encoding.UTF8.GetBytes(value.AsSpan(), output.GetSpan(length));
        output.Advance(length);
    }
    internal static void Append(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        Append(output, value.Length);
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }
    internal static void Append(IBufferWriter<byte> output, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.GetSpan(sizeof(int)), value);
        output.Advance(sizeof(int));
    }
    internal static void Append(IBufferWriter<byte> output, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(output.GetSpan(sizeof(long)), value);
        output.Advance(sizeof(long));
    }
    internal static void Append(IBufferWriter<byte> output, bool value)
    {
        output.GetSpan(1)[0] = value ? (byte)1 : (byte)0;
        output.Advance(1);
    }
}

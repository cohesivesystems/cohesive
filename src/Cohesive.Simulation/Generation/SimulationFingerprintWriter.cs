using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>Shared length-prefixed SHA-256 writer for Simulation semantic fingerprints.</summary>
internal sealed class SimulationFingerprintWriter : IDisposable
{
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    readonly byte[] numberBuffer = new byte[8];

    public void Append(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32BigEndian(numberBuffer, byteCount);
        hash.AppendData(numberBuffer.AsSpan(0, sizeof(int)));

        if (byteCount == 0)
            return;

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, rented);
            hash.AppendData(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Append(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(numberBuffer, value);
        hash.AppendData(numberBuffer.AsSpan(0, sizeof(int)));
    }

    public void Append(double value)
    {
        BinaryPrimitives.WriteInt64BigEndian(numberBuffer, BitConverter.DoubleToInt64Bits(value));
        hash.AppendData(numberBuffer);
    }

    public void Append(ObservationValue value)
    {
        ArrayBufferWriter<byte> buffer = new();
        CanonicalJsonWriter.WriteCanonicalObservationValue(buffer, value);
        Append(buffer.WrittenSpan);
    }

    public void Append(ReadOnlySpan<byte> value)
    {
        Append(value.Length);
        hash.AppendData(value);
    }

    public string Complete() => Convert.ToHexStringLower(hash.GetHashAndReset());

    public void Dispose()
    {
        hash.Dispose();
        GC.SuppressFinalize(this);
    }
}

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

    public void Append(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(numberBuffer, value);
        hash.AppendData(numberBuffer);
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

    public void Append(TypeRef type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (type)
        {
            case ScalarTypeRef scalar:
                Append("scalar");
                Append((int)scalar.Kind);
                Append((int)scalar.Format);
                return;

            case EnumTypeRef @enum:
                Append("enum");
                Append(@enum.Name);
                foreach (var member in @enum.Members.Order(StringComparer.Ordinal))
                    Append(member);
                return;

            case EntityReferenceTypeRef entityReference:
                Append("entity-reference");
                Append(entityReference.Entity.Value);
                return;

            case ArrayTypeRef array:
                Append("array");
                Append(array.ElementType);
                return;

            case ObjectTypeRef obj:
                Append("object");
                foreach (var field in obj.Fields.OrderBy(static field => field.Name, StringComparer.Ordinal))
                {
                    Append(field.Name);
                    Append((int)field.Cardinality);
                    Append((int)field.Presence);
                    Append((int)field.Nullability);
                    Append(field.Type);
                }
                return;

            case NamedTypeRef named:
                Append("named");
                Append(named.TypeId.Value);
                return;

            case QuantityTypeRef quantity:
                Append("quantity");
                Append(quantity.Quantity);
                Append((int)quantity.BaseKind);
                return;

            case OpaqueRuntimeTypeRef opaque:
                Append("opaque");
                Append(opaque.RuntimeType);
                return;

            case JsonTypeRef json:
                Append("json");
                Append((int)json.Kind);
                return;

            default:
                throw new NotSupportedException(
                    $"Simulation canonicalization does not support type '{type.GetType().Name}'.");
        }
    }

    public void Append(ValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Type is { } type)
        {
            Append(1);
            Append(type);
        }
        else
        {
            Append(0);
        }

        if (contract.Shape is { } shape)
        {
            Append(1);
            Append(shape.GraphId.Value);
            Append(shape.ShapeId.Value);
        }
        else
        {
            Append(0);
        }

        Append((int)contract.Cardinality);
        Append((int)contract.Presence);
        Append((int)contract.Nullability);
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

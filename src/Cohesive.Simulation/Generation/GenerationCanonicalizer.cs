using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

static class GenerationCanonicalizer
{
    public const string FingerprintAlgorithm = "sha256";
    public const string CanonicalizationProfile = "cohesive-generation/v1-c14n/v1";

    public static string ComputeShapeFingerprint(
        ShapeId shapeId,
        IEnumerable<RecordGenerationMember> members)
    {
        using HashWriter writer = new();
        writer.Append("cohesive-simulation-shape/v1");
        writer.Append(shapeId.Value);
        foreach (var member in members.OrderBy(static member => member.Identity.Value, StringComparer.Ordinal))
        {
            writer.Append(member.Identity.Value);
            AppendType(writer, member.Generator.ValueType);
        }

        return writer.Complete();
    }

    public static string ComputeDefinitionFingerprint(GenerationDefinition definition)
    {
        using HashWriter writer = new();
        writer.Append(CanonicalizationProfile);
        writer.Append(definition.ShapeGraph.Id.Value);
        writer.Append(definition.Root.ShapeId.Value);
        foreach (var member in definition.Root.Members.OrderBy(
                     static member => member.Identity.Value,
                     StringComparer.Ordinal))
        {
            writer.Append(member.Identity.Value);
            AppendGenerator(writer, member.Generator);
        }

        return writer.Complete();
    }

    static void AppendGenerator(HashWriter writer, ValueGeneratorNode generator)
    {
        switch (generator)
        {
            case ConstantGenerationNode constant:
                writer.Append("constant");
                AppendType(writer, constant.ValueType);
                writer.Append(constant.Value);
                return;

            case Int32GenerationNode integer:
                writer.Append("int32");
                writer.Append(integer.Minimum);
                writer.Append(integer.Maximum);
                return;

            case BernoulliGenerationNode bernoulli:
                writer.Append("bernoulli");
                writer.Append(bernoulli.Probability);
                return;

            case WeightedCategoricalGenerationNode categorical:
                writer.Append("weighted-categorical");
                AppendType(writer, categorical.ValueType);
                writer.Append(categorical.Options.Length);
                foreach (var option in categorical.Options)
                {
                    writer.Append(option.Value);
                    writer.Append(option.Weight);
                }
                return;

            default:
                throw new NotSupportedException(
                    $"Generation canonicalization does not support node '{generator.GetType().Name}'.");
        }
    }

    static void AppendType(HashWriter writer, TypeRef type)
    {
        switch (type)
        {
            case ScalarTypeRef scalar:
                writer.Append("scalar");
                writer.Append((int)scalar.Kind);
                writer.Append((int)scalar.Format);
                return;

            case EnumTypeRef @enum:
                writer.Append("enum");
                writer.Append(@enum.Name);
                foreach (var member in @enum.Members.Order(StringComparer.Ordinal))
                    writer.Append(member);
                return;

            case EntityReferenceTypeRef entityReference:
                writer.Append("entity-reference");
                writer.Append(entityReference.Entity.Value);
                return;

            case ArrayTypeRef array:
                writer.Append("array");
                AppendType(writer, array.ElementType);
                return;

            case ObjectTypeRef obj:
                writer.Append("object");
                foreach (var field in obj.Fields.OrderBy(static field => field.Name, StringComparer.Ordinal))
                {
                    writer.Append(field.Name);
                    writer.Append((int)field.Cardinality);
                    writer.Append((int)field.Presence);
                    writer.Append((int)field.Nullability);
                    AppendType(writer, field.Type);
                }
                return;

            case NamedTypeRef named:
                writer.Append("named");
                writer.Append(named.TypeId.Value);
                return;

            case QuantityTypeRef quantity:
                writer.Append("quantity");
                writer.Append(quantity.Quantity);
                writer.Append((int)quantity.BaseKind);
                return;

            case OpaqueRuntimeTypeRef opaque:
                writer.Append("opaque");
                writer.Append(opaque.RuntimeType);
                return;

            case JsonTypeRef json:
                writer.Append("json");
                writer.Append((int)json.Kind);
                return;

            default:
                throw new NotSupportedException(
                    $"Generation canonicalization does not support type '{type.GetType().Name}'.");
        }
    }

    sealed class HashWriter : IDisposable
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
            Append(buffer.WrittenSpan.Length);
            hash.AppendData(buffer.WrittenSpan);
        }

        public string Complete() => Convert.ToHexStringLower(hash.GetHashAndReset());

        public void Dispose()
        {
            hash.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

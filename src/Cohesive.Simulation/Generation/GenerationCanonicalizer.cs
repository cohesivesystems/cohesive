using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

static class GenerationCanonicalizer
{
    public const string FingerprintAlgorithm = GenerationDefinitionFingerprint.CurrentAlgorithm;
    public const string CanonicalizationProfile = GenerationDefinitionFingerprint.CurrentCanonicalization;

    public static string ComputeShapeFingerprint(
        ShapeId shapeId,
        IEnumerable<RecordGenerationMember> members)
    {
        using SimulationFingerprintWriter writer = new();
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
        using SimulationFingerprintWriter writer = new();
        writer.Append(CanonicalizationProfile);
        writer.Append(StrictDocumentJson.GetCanonicalBytes(
            ShapeGraphDocument.FromGraph(definition.ShapeGraph),
            StrictDocumentJson.CreateOptions()));
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

    static void AppendGenerator(SimulationFingerprintWriter writer, ValueGeneratorNode generator)
    {
        switch (generator)
        {
            case ConstantGenerationNode constant:
                writer.Append(GenerationDefinitionWireNames.Constant);
                AppendType(writer, constant.ValueType);
                writer.Append(constant.Value);
                return;

            case Int32GenerationNode integer:
                writer.Append(GenerationDefinitionWireNames.Int32);
                writer.Append(integer.Minimum);
                writer.Append(integer.Maximum);
                return;

            case BernoulliGenerationNode bernoulli:
                writer.Append(GenerationDefinitionWireNames.Bernoulli);
                writer.Append(bernoulli.Probability);
                return;

            case WeightedCategoricalGenerationNode categorical:
                writer.Append(GenerationDefinitionWireNames.WeightedCategorical);
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

    static void AppendType(SimulationFingerprintWriter writer, TypeRef type)
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

}

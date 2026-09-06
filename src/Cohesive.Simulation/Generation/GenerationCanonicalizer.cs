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
            writer.Append(member.Generator.ValueType);
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
        writer.Append(definition.Root.Bindings.Length);
        foreach (var binding in definition.Root.Bindings.OrderBy(
                     static binding => binding.Identity.Value,
                     StringComparer.Ordinal))
        {
            writer.Append(binding.Identity.Value);
            AppendGenerator(writer, binding.Generator);
        }

        writer.Append(definition.Root.Members.Length);
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
                writer.Append(constant.ValueType);
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
                writer.Append(categorical.ValueType);
                writer.Append(categorical.Options.Length);
                foreach (var option in categorical.Options)
                {
                    writer.Append(option.Value);
                    writer.Append(option.Weight);
                }
                return;

            case CatalogGenerationNode catalog:
                writer.Append(GenerationDefinitionWireNames.Catalog);
                writer.Append(GenerationCatalogJsonSerializer.GetCanonicalBytes(catalog.Catalog));
                return;

            case ExpressionGenerationNode expression:
                writer.Append(GenerationDefinitionWireNames.Expression);
                writer.Append(expression.ValueType);
                writer.Append(StrictDocumentJson.GetCanonicalBytes(
                    expression.Expression,
                    StrictDocumentJson.CreateOptions()));
                return;

            default:
                throw new NotSupportedException(
                    $"Generation canonicalization does not support node '{generator.GetType().Name}'.");
        }
    }

}

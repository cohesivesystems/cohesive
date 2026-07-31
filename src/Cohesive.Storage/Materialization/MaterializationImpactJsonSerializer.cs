using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Strict canonical JSON serialization for portable materialization impact plans.</summary>
public static class MaterializationImpactJsonSerializer
{
    /// <summary>Creates strict impact-plan JSON options with canonical Relations converters.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        if (!Enum.IsDefined(formatting))
        {
            throw new ArgumentOutOfRangeException(nameof(formatting), formatting, "Unsupported JSON formatting.");
        }

        return RelationQueryJsonSerializer.CreateOptions(formatting == PortableDocumentJsonFormatting.Indented);
    }

    /// <summary>Serializes one structurally verified impact plan.</summary>
    /// <param name="plan">Impact plan to serialize.</param>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Deterministic impact-plan JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The plan cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The plan contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The plan has no canonical JSON representation.</exception>
    public static string Serialize(
        MaterializationImpactPlan plan,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(plan))
            : JsonSerializer.Serialize(plan, CreateOptions(formatting));
    }

    /// <summary>Gets the unique canonical compact UTF-8 representation of one impact plan.</summary>
    /// <param name="plan">Impact plan to encode.</param>
    /// <returns>Canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The plan cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The plan contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The plan has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(MaterializationImpactPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return StrictDocumentJson.GetCanonicalBytes(plan, CreateOptions());
    }

    /// <summary>Deserializes and definition-links one current-version impact plan.</summary>
    /// <param name="json">Persisted impact-plan JSON.</param>
    /// <param name="definition">Canonical materialization definition claimed by the persisted plan.</param>
    /// <returns>The exact normalized, fingerprint-verified, and deterministically reproduced impact plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan is foreign, stale, or cannot be reproduced from the definition.</exception>
    /// <exception cref="JsonException">The wire shape, schema version, or fingerprint is invalid.</exception>
    /// <exception cref="NotSupportedException">Canonical content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical content cannot be fingerprinted.</exception>
    public static MaterializationImpactPlan Deserialize(
        string json,
        MaterializationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "materialization impact plan",
                out MaterializationImpactPlan? plan,
                out var error)
            || plan is null)
        {
            throw new JsonException(error.Message);
        }

        _ = MaterializationImpactPlanLinker.Link(plan, definition);
        return plan;
    }
}

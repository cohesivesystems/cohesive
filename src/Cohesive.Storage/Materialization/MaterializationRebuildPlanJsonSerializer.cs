using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Strict canonical JSON serialization for persisted materialization rebuild realization plans.</summary>
/// <remarks>
/// Deserialization projects through <see cref="MaterializationRebuildPlan"/>'s canonical constructor, so schema,
/// definition, capability, shard, normalization, and fingerprint invariants are re-established before a plan is
/// returned. Runtime adapter bindings are deliberately outside this wire contract.
/// </remarks>
public static class MaterializationRebuildPlanJsonSerializer
{
    /// <summary>Creates strict rebuild-plan JSON options including canonical Relations converters.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        MaterializationJsonSerializer.CreateOptions(formatting);

    /// <summary>Serializes one exactly fingerprinted rebuild realization plan.</summary>
    /// <param name="plan">Rebuild plan to serialize.</param>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Deterministic rebuild-plan JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The plan cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The plan contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The plan has no canonical JSON representation.</exception>
    public static string Serialize(
        MaterializationRebuildPlan plan,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(plan))
            : JsonSerializer.Serialize(plan, CreateOptions(formatting));
    }

    /// <summary>Gets the unique canonical compact UTF-8 representation of one rebuild realization plan.</summary>
    /// <param name="plan">Rebuild plan to encode.</param>
    /// <returns>Canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The plan cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The plan contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The plan has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(MaterializationRebuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return StrictDocumentJson.GetCanonicalBytes(plan, CreateOptions());
    }

    /// <summary>Deserializes and verifies one current-version materialization rebuild realization plan.</summary>
    /// <param name="json">Persisted rebuild-plan JSON.</param>
    /// <returns>An exactly normalized plan whose persisted fingerprint matches all canonical content.</returns>
    /// <exception cref="JsonException">
    /// The wire is empty, malformed, open, duplicate, non-canonical, uses an unsupported schema, violates a plan
    /// invariant, or carries a stale or forged fingerprint.
    /// </exception>
    public static MaterializationRebuildPlan Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "materialization rebuild plan",
                out MaterializationRebuildPlan? plan,
                out var error)
            || plan is null)
        {
            throw new JsonException(error.Message);
        }

        return plan;
    }
}

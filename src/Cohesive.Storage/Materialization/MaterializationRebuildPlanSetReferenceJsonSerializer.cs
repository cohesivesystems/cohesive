using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Strict canonical JSON persistence for <see cref="MaterializationRebuildPlanSetReference"/>.</summary>
public static class MaterializationRebuildPlanSetReferenceJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact rebuild plan-set reference to canonical compact JSON.</summary>
    /// <param name="reference">Exact content-addressed plan-set authority.</param>
    /// <returns>Canonical JSON preserving request and plan-set fingerprints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The reference cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The reference has no canonical JSON representation.</exception>
    public static string Serialize(MaterializationRebuildPlanSetReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(reference, Options));
    }

    /// <summary>Deserializes and validates one exact rebuild plan-set reference.</summary>
    /// <param name="json">Strict canonical reference JSON.</param>
    /// <returns>The constructor-validated content-addressed reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical, open, or uses another schema.</exception>
    public static MaterializationRebuildPlanSetReference Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                "materialization rebuild plan-set reference",
                out MaterializationRebuildPlanSetReference? reference,
                out var error)
            && reference is not null)
        {
            return reference;
        }

        throw new JsonException(error.Message);
    }
}

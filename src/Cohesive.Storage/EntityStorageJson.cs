using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage;

/// <summary>Explicit lossless JSON profile for retained entity snapshots and Transition operation receipts.</summary>
public static class EntityStorageJson
{
    /// <summary>Wire revision introducing tagged detached observation values; plain-JSON v1 is not lossless.</summary>
    public const int FormatVersion = 2;

    /// <summary>Shared immutable lossless profile for retained storage evidence.</summary>
    public static JsonSerializerOptions Options { get; } = Freeze();
    static JsonSerializerOptions Freeze()
    {
        var options = CreateOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    /// <summary>Computes the lowercase SHA-256 integrity digest of canonical storage bytes.</summary>
    /// <param name="canonicalBytes">Canonical bytes produced with the storage profile.</param>
    /// <returns>Lowercase hexadecimal digest.</returns>
    public static string Hash(ReadOnlySpan<byte> canonicalBytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(canonicalBytes));

    /// <summary>Creates strict options preserving every observation scalar kind through retained state and evidence.</summary>
    /// <returns>New caller-owned options; configure before first use, then reuse for serialization and deserialization.</returns>
    /// <remarks>Uses the existing PortableValue tagged codec; no parallel scalar catalog or encoding is introduced.</remarks>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = StrictDocumentJson.CreateOptions();
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        return options;
    }
}

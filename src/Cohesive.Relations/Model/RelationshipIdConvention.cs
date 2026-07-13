using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Relations.Model;

/// <summary>
/// Versioned deterministic convention for deriving semantic relationship identifiers.
/// </summary>
/// <remarks>
/// <para>
/// Version 1 hashes a sequence of length-prefixed UTF-8 fields. Each field is encoded as the
/// invariant decimal UTF-8 byte count, <c>:</c>, the exact UTF-8 bytes, and <c>;</c>. Text is not
/// Unicode-normalized.
/// </para>
/// <para>
/// Fields occur in this order: convention version, source graph id, source shape id, invariant
/// decimal path-segment count, then each segment's invariant decimal <see cref="SegmentKind"/> value
/// and segment text, target graph id, target shape id, target-key discriminator token, and invariant
/// decimal <see cref="SourceReferenceUniqueness"/> value. SHA-256 is applied to the complete byte
/// sequence and rendered as 64 lowercase hexadecimal characters after <see cref="Prefix"/>.
/// </para>
/// </remarks>
public static class RelationshipIdConvention
{
    /// <summary>Canonical convention version encoded into generated relationship identifiers.</summary>
    public const string Version = "relationship-id/v1";

    /// <summary>
    /// Prefix reserved for relationship IDs produced by this convention. Catalog validation rejects
    /// prefixed identifiers that do not match their canonical semantic inputs.
    /// </summary>
    public const string Prefix = "relationship:v1:sha256:";

    /// <summary>Creates a deterministic identifier from a relationship's semantic content.</summary>
    /// <param name="relationship">Relationship whose identifier-independent semantics are hashed.</param>
    /// <returns>A deterministic relationship identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relationship"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="relationship"/> contains incomplete or unsupported canonical semantics.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="relationship"/> declares unsupported source-reference uniqueness.
    /// </exception>
    public static RelationshipId Create(RelationshipDefinition relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return Create(
            relationship.SourceShape,
            relationship.SourceReference,
            relationship.TargetShape,
            relationship.TargetKey,
            relationship.SourceReferenceUniqueness);
    }

    /// <summary>Creates a deterministic identifier from canonical relationship semantics.</summary>
    /// <param name="sourceShape">Graph-qualified reference-bearing shape.</param>
    /// <param name="sourceReference">Reference field path.</param>
    /// <param name="targetShape">Graph-qualified target shape.</param>
    /// <param name="targetKey">Semantic target key.</param>
    /// <param name="sourceReferenceUniqueness">Global source-reference uniqueness guarantee.</param>
    /// <returns>A deterministic relationship identifier.</returns>
    /// <exception cref="ArgumentException">
    /// A shape identifier or <paramref name="sourceReference"/> is default, or
    /// <paramref name="targetKey"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="targetKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    public static RelationshipId Create(
        QualifiedShapeId sourceShape,
        FieldPath sourceReference,
        QualifiedShapeId targetShape,
        RelationshipTargetKey targetKey,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed)
    {
        RequireQualifiedShape(sourceShape, nameof(sourceShape));
        if (sourceReference.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A relationship source reference is required.", nameof(sourceReference));
        RequireQualifiedShape(targetShape, nameof(targetShape));
        ArgumentNullException.ThrowIfNull(targetKey);
        if (!Enum.IsDefined(sourceReferenceUniqueness))
            throw new ArgumentOutOfRangeException(nameof(sourceReferenceUniqueness), sourceReferenceUniqueness, "Unsupported source-reference uniqueness value.");

        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, Version);
        Append(canonical, sourceShape.GraphId.Value);
        Append(canonical, sourceShape.ShapeId.Value);
        Append(canonical, sourceReference.Segments.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var segment in sourceReference.Segments)
        {
            Append(canonical, ((int)segment.Kind).ToString(CultureInfo.InvariantCulture));
            Append(canonical, segment.Segment ?? string.Empty);
        }
        Append(canonical, targetShape.GraphId.Value);
        Append(canonical, targetShape.ShapeId.Value);
        Append(canonical, TargetKeyName(targetKey));
        Append(canonical, ((int)sourceReferenceUniqueness).ToString(CultureInfo.InvariantCulture));

        var digest = SHA256.HashData(canonical.WrittenSpan);
        return new(Prefix + Convert.ToHexStringLower(digest));
    }

    /// <summary>Returns whether an identifier has this convention's versioned prefix.</summary>
    /// <param name="id">Relationship identifier to inspect.</param>
    /// <returns><see langword="true"/> when the identifier uses this convention's prefix.</returns>
    public static bool IsConventionId(RelationshipId id) =>
        id.Value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    static void Append(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteAscii(writer, byteCount.ToString(CultureInfo.InvariantCulture));
        WriteByte(writer, (byte)':');

        var destination = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, destination);
        writer.Advance(written);
        WriteByte(writer, (byte)';');
    }

    static void WriteAscii(ArrayBufferWriter<byte> writer, string value)
    {
        var destination = writer.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++)
            destination[index] = (byte)value[index];
        writer.Advance(value.Length);
    }

    static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    static string TargetKeyName(RelationshipTargetKey targetKey) => targetKey switch
    {
        ObservationIdentityRelationshipTargetKey => RelationshipWireNames.ObservationIdentityTargetKey,
        _ => throw new ArgumentException($"Unsupported relationship target key '{targetKey.GetType().Name}'.", nameof(targetKey))
    };

    static void RequireQualifiedShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A graph-qualified shape identifier is required.", parameterName);
    }
}

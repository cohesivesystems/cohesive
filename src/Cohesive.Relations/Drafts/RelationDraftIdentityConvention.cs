using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Drafts;

/// <summary>
/// Versioned deterministic identities for relation-draft projection slots and candidates.
/// </summary>
/// <remarks>
/// Slot identity is derived from the graph-qualified target shape and target field path. Candidate
/// identity is derived from the slot identity and canonical JSON for the candidate expression.
/// Convention decisions, producer metadata, scores, ranks, and declaration order are deliberately
/// excluded.
/// </remarks>
public static class RelationDraftIdentityConvention
{
    /// <summary>Canonical identity convention version.</summary>
    public const string Version = "relation-draft-identity/v1";

    /// <summary>Prefix reserved for convention-derived projection-slot identifiers.</summary>
    public const string AssignmentSlotPrefix = "relation-draft-slot:v1:sha256:";

    /// <summary>Prefix reserved for convention-derived candidate identifiers.</summary>
    public const string CandidatePrefix = "relation-draft-candidate:v1:sha256:";

    /// <summary>Derives a stable projection-slot identifier from its target field.</summary>
    /// <param name="targetShape">Graph-qualified shape projected by the draft.</param>
    /// <param name="target">Target field path represented by the slot.</param>
    /// <returns>A deterministic identifier suitable for both the draft slot and accepted projection assignment.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="targetShape"/> is default or <paramref name="target"/> contains no segments.
    /// </exception>
    public static QueryAssignmentId CreateAssignmentSlotId(
        QualifiedShapeId targetShape,
        FieldPath target)
    {
        RequireQualifiedShape(targetShape, nameof(targetShape));
        RequireFieldPath(target, nameof(target));

        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, Version);
        Append(canonical, "slot");
        Append(canonical, targetShape.GraphId.Value);
        Append(canonical, targetShape.ShapeId.Value);
        Append(canonical, target.Segments.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var segment in target.Segments)
        {
            Append(canonical, ((int)segment.Kind).ToString(CultureInfo.InvariantCulture));
            Append(canonical, segment.Segment ?? string.Empty);
        }

        return new(AssignmentSlotPrefix + Hash(canonical.WrittenSpan));
    }

    /// <summary>Derives a stable semantic candidate identifier.</summary>
    /// <param name="slotId">Projection slot to which the candidate belongs.</param>
    /// <param name="value">Canonical candidate expression.</param>
    /// <returns>A deterministic candidate identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="slotId"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="value"/> contains a value without a canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="JsonException"><paramref name="value"/> cannot be serialized using the canonical wire contract.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="value"/> contains a runtime type unsupported by canonical relation/query serialization.
    /// </exception>
    public static RelationDraftCandidateId CreateCandidateId(QueryAssignmentId slotId, Expr value)
    {
        if (string.IsNullOrWhiteSpace(slotId.Value))
            throw new ArgumentException("A projection-slot identifier is required.", nameof(slotId));
        ArgumentNullException.ThrowIfNull(value);

        var options = RelationQueryJsonSerializer.CreateOptions();
        var expressionNode = JsonSerializer.SerializeToNode(value, typeof(Expr), options)
                             ?? throw new InvalidOperationException("Failed to materialize canonical candidate expression JSON.");
        var expression = CanonicalJsonWriter.GetCanonicalBytes(
            expressionNode,
            options,
            static _ => null);

        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, Version);
        Append(canonical, "candidate");
        Append(canonical, slotId.Value);
        Append(canonical, expression);
        return new(CandidatePrefix + Hash(canonical.WrittenSpan));
    }

    /// <summary>Tests whether a slot identifier uses this convention's reserved prefix.</summary>
    /// <param name="id">Slot identifier to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> uses the versioned convention prefix.</returns>
    public static bool IsConventionAssignmentSlotId(QueryAssignmentId id) =>
        id.Value?.StartsWith(AssignmentSlotPrefix, StringComparison.Ordinal) == true;

    /// <summary>Tests whether a candidate identifier uses this convention's reserved prefix.</summary>
    /// <param name="id">Candidate identifier to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> uses the versioned convention prefix.</returns>
    public static bool IsConventionCandidateId(RelationDraftCandidateId id) =>
        id.Value?.StartsWith(CandidatePrefix, StringComparison.Ordinal) == true;

    static string Hash(ReadOnlySpan<byte> canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(canonical));

    static void Append(ArrayBufferWriter<byte> writer, string value) =>
        Append(writer, Encoding.UTF8.GetBytes(value));

    static void Append(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        WriteAscii(writer, value.Length.ToString(CultureInfo.InvariantCulture));
        WriteByte(writer, (byte)':');
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
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

    static void RequireQualifiedShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("A graph-qualified shape identifier is required.", parameterName);
        }
    }

    static void RequireFieldPath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A field path is required.", parameterName);
    }
}

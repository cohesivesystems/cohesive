using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cohesive.Model;

/// <summary>
/// Immutable top-level field layout for physical observation interpretations and compiled execution plans.
/// </summary>
/// <remarks>
/// A layout assigns stable zero-based ordinals to canonical field identities for one exact graph-qualified shape.
/// It is an execution artifact, not another semantic authority: the governing <see cref="Shape"/> remains the
/// source of field meaning, and <see cref="Observation"/> remains the portable validated value representation.
/// Layout instances are safe to share across occurrences, readers, materializers, and concurrent operations.
/// </remarks>
public sealed class ObservationLayout
{
    static readonly ConditionalWeakTable<ShapeGraph, ConcurrentDictionary<ShapeId, ObservationLayout>>
        canonicalLayoutsByGraph = new();

    readonly ImmutableArray<string> fieldIdentities;
    readonly ImmutableArray<FieldDefinition> fieldDefinitions;
    readonly Dictionary<string, int> ordinalByFieldIdentity;
    readonly Dictionary<ulong, int> ordinalByJsonNameHash;
    readonly ImmutableArray<int> canonicalJsonOrdinals;
    readonly ImmutableArray<JsonEncodedText> jsonPropertyNamesByOrdinal;

    ObservationLayout(
        QualifiedShapeId shapeId,
        ImmutableArray<string> fieldIdentities,
        ImmutableArray<FieldDefinition> fieldDefinitions)
    {
        ShapeId = shapeId;
        this.fieldIdentities = fieldIdentities;
        this.fieldDefinitions = fieldDefinitions;
        ordinalByFieldIdentity = new(fieldIdentities.Length, StringComparer.Ordinal);
        ordinalByJsonNameHash = new(fieldIdentities.Length);
        var canonicalOrdinals = new int[fieldIdentities.Length];
        var jsonPropertyNames = ImmutableArray.CreateBuilder<JsonEncodedText>(fieldIdentities.Length);
        for (var ordinal = 0; ordinal < fieldIdentities.Length; ordinal++)
        {
            var fieldIdentity = fieldIdentities[ordinal];
            if (!ordinalByFieldIdentity.TryAdd(fieldIdentity, ordinal))
            {
                throw new ArgumentException(
                    $"Layout for shape '{shapeId}' contains duplicate field identity '{fieldIdentity}'.",
                    nameof(fieldIdentities));
            }

            canonicalOrdinals[ordinal] = ordinal;
            var jsonPropertyName = JsonEncodedText.Encode(
                fieldIdentity,
                JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
            jsonPropertyNames.Add(jsonPropertyName);
            var hash = GetUtf8Hash(jsonPropertyName.EncodedUtf8Bytes);
            if (!ordinalByJsonNameHash.TryAdd(hash, ordinal))
                ordinalByJsonNameHash[hash] = -1;
        }

        Array.Sort(
            canonicalOrdinals,
            (left, right) => StringComparer.Ordinal.Compare(fieldIdentities[left], fieldIdentities[right]));
        canonicalJsonOrdinals = ImmutableArray.Create(canonicalOrdinals);
        jsonPropertyNamesByOrdinal = jsonPropertyNames.MoveToImmutable();
    }

    /// <summary>Gets the exact graph-qualified shape interpreted by this layout.</summary>
    public QualifiedShapeId ShapeId { get; }

    /// <summary>Gets the immutable canonical field identities in ordinal order.</summary>
    public ImmutableArray<string> FieldIdentities => fieldIdentities;

    /// <summary>Gets the number of field ordinals in this layout.</summary>
    public int Count => fieldIdentities.Length;

    internal ReadOnlySpan<int> CanonicalJsonOrdinals => canonicalJsonOrdinals.AsSpan();

    internal JsonEncodedText GetJsonPropertyName(int ordinal) => jsonPropertyNamesByOrdinal[ordinal];

    internal FieldDefinition GetFieldDefinition(int ordinal) => fieldDefinitions[ordinal];

    internal bool TryGetJsonOrdinal(ref Utf8JsonReader reader, out int ordinal)
    {
        if (!reader.HasValueSequence && !reader.ValueIsEscaped)
        {
            var hash = GetUtf8Hash(reader.ValueSpan);
            if (ordinalByJsonNameHash.TryGetValue(hash, out ordinal)
                && ordinal >= 0
                && reader.ValueTextEquals(fieldIdentities[ordinal]))
            {
                return true;
            }
        }

        for (ordinal = 0; ordinal < fieldIdentities.Length; ordinal++)
        {
            if (reader.ValueTextEquals(fieldIdentities[ordinal]))
                return true;
        }

        ordinal = default;
        return false;
    }

    /// <summary>Gets the ordinal assigned to a canonical field identity.</summary>
    /// <param name="fieldIdentity">Canonical top-level field identity.</param>
    /// <returns>The zero-based ordinal assigned to <paramref name="fieldIdentity"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="fieldIdentity"/> is not part of this layout.
    /// </exception>
    public int GetOrdinal(string fieldIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
        return ordinalByFieldIdentity.TryGetValue(fieldIdentity, out var ordinal)
            ? ordinal
            : throw new KeyNotFoundException(
                $"Field '{fieldIdentity}' is not part of layout '{ShapeId}'.");
    }

    /// <summary>Attempts to resolve the ordinal assigned to a canonical field identity.</summary>
    /// <param name="fieldIdentity">Canonical top-level field identity.</param>
    /// <param name="ordinal">Assigned zero-based ordinal when found; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the field belongs to this layout; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    public bool TryGetOrdinal(string fieldIdentity, out int ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
        return ordinalByFieldIdentity.TryGetValue(fieldIdentity, out ordinal);
    }

    /// <summary>Gets the shared canonical declaration-order layout for an exact graph-scoped shape.</summary>
    /// <param name="shape">Exact graph and shape whose fields define the layout.</param>
    /// <returns>
    /// The graph-cached immutable layout containing every shape field in declaration order. Repeated calls for the
    /// same graph instance and shape return the same layout instance.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is default.</exception>
    public static ObservationLayout Create(GraphShapeId shape)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        var layouts = canonicalLayoutsByGraph.GetValue(
            shape.Graph,
            static _ => new ConcurrentDictionary<ShapeId, ObservationLayout>());
        return layouts.GetOrAdd(shape.ShapeId, _ => CreateCanonical(shape));
    }

    static ObservationLayout CreateCanonical(GraphShapeId shape)
    {
        var definition = shape.Graph.GetShape(shape.ShapeId);
        var identities = ImmutableArray.CreateBuilder<string>(definition.Fields.Length);
        var fields = ImmutableArray.CreateBuilder<FieldDefinition>(definition.Fields.Length);
        foreach (var field in definition.Fields)
        {
            identities.Add(field.Name.Value);
            fields.Add(field);
        }

        return new(shape.QualifiedId, identities.MoveToImmutable(), fields.MoveToImmutable());
    }

    /// <summary>Creates a caller-ordered layout for an exact graph-scoped shape.</summary>
    /// <param name="shape">Exact graph and shape whose fields may appear in the layout.</param>
    /// <param name="fieldIdentities">Canonical top-level field identities in desired ordinal order.</param>
    /// <returns>An immutable layout containing the requested fields in the requested order.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default or <paramref name="fieldIdentities"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A field identity is empty, duplicated, or absent from the governing shape.
    /// </exception>
    public static ObservationLayout Create(
        GraphShapeId shape,
        IEnumerable<string> fieldIdentities)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(fieldIdentities);
        var definition = shape.Graph.GetShape(shape.ShapeId);
        var hasCount = fieldIdentities.TryGetNonEnumeratedCount(out var count);
        var identities = hasCount
            ? ImmutableArray.CreateBuilder<string>(count)
            : ImmutableArray.CreateBuilder<string>();
        var fields = hasCount
            ? ImmutableArray.CreateBuilder<FieldDefinition>(count)
            : ImmutableArray.CreateBuilder<FieldDefinition>();

        foreach (var fieldIdentity in fieldIdentities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
            if (!definition.TryGetField(fieldIdentity, out var field))
            {
                throw new ArgumentException(
                    $"Layout for shape '{shape.QualifiedId}' contains unknown field '{fieldIdentity}'.",
                    nameof(fieldIdentities));
            }

            identities.Add(fieldIdentity);
            fields.Add(field);
        }

        return new(shape.QualifiedId, identities.ToImmutable(), fields.ToImmutable());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong GetUtf8Hash(ReadOnlySpan<byte> value)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        var hash = OffsetBasis;
        foreach (var character in value)
        {
            hash ^= character;
            hash = unchecked(hash * Prime);
        }

        return hash;
    }

    /// <inheritdoc />
    public override string ToString() => $"{ShapeId} [{string.Join(", ", fieldIdentities)}]";
}

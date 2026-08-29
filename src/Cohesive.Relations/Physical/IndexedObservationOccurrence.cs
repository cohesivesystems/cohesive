using System.Buffers;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Model;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Relations.Physical;

/// <summary>
/// Indexed physical storage for one shaped binding occurrence in a relation evaluation.
/// </summary>
/// <remarks>
/// <para>
/// The core <see cref="CoreObservation"/> remains the semantic authority for what was observed. This type composes
/// the existing evaluation-scoped occurrence identity with an ordinal layout, dense value buffer, packed presence
/// bits, and derived-field lineage. It does not introduce entity identity or source-version semantics.
/// </para>
/// <para>
/// Public factories retain the supplied immutable layout, snapshot caller-owned mutable buffer inputs, and validate
/// them against the exact supplied graph. The resulting instance is immutable and safe to reuse across concurrent
/// reads.
/// </para>
/// </remarks>
public sealed class IndexedObservationOccurrence : IOrdinalObservationFieldReader
{
    readonly ObservationBuffer buffer;
    IReadOnlyDictionary<string, ObservationValue>? fields;

    IndexedObservationOccurrence(
        RelationQueryObservationOccurrence occurrence,
        ObservationLayout layout,
        ObservationBuffer buffer,
        ObservationLineage lineage)
    {
        Occurrence = occurrence;
        Layout = layout;
        this.buffer = buffer;
        Lineage = lineage;
    }

    /// <summary>Gets the evaluation-scoped occurrence interpreted by this physical row.</summary>
    public RelationQueryObservationOccurrence Occurrence { get; }

    /// <inheritdoc />
    public QualifiedShapeId ShapeId => Occurrence.Shape;

    /// <summary>Gets the immutable ordinal layout used by the physical buffer.</summary>
    public ObservationLayout Layout { get; }

    /// <summary>Gets ordinal-aligned values as read-only memory.</summary>
    public ReadOnlyMemory<ObservationValue> ValuesByOrdinal => buffer.ValuesByOrdinal;

    /// <summary>Gets the packed value-presence bitmap as read-only memory.</summary>
    public ReadOnlyMemory<ulong> HasValueBitMask => buffer.HasValueBitMask;

    /// <summary>Gets derived-field lineage attached to this occurrence.</summary>
    public ObservationLineage Lineage { get; }

    /// <summary>Gets present fields keyed by canonical semantic identity.</summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields => fields ??= BuildFields(Layout, buffer);

    /// <summary>
    /// Creates an indexed occurrence from a validated identity-free observation using the shape's declaration-order
    /// layout.
    /// </summary>
    /// <param name="shape">Exact graph and shape governing the observation and generated layout.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="observation">Validated semantic observation to interpret physically.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable indexed occurrence.</returns>
    /// <remarks>
    /// This convenience overload resolves the graph-cached declaration-order layout. Pass an explicit layout when
    /// the physical field order is caller-defined or when the same layout must also bind a compiled plan.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default, or <paramref name="occurrence"/> or <paramref name="observation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The shape, occurrence, and observation have different qualified identities.
    /// </exception>
    public static IndexedObservationOccurrence FromObservation(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        CoreObservation observation,
        ObservationLineage? lineage = null)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ShapeId != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Observation shape '{observation.ShapeId}' does not match supplied shape '{shape.QualifiedId}'.",
                nameof(observation));
        }

        return FromObservationCore(occurrence, observation, ObservationLayout.Create(shape), lineage);
    }

    /// <summary>Creates an indexed occurrence using a validated semantic observation and an explicit layout.</summary>
    /// <param name="shape">Exact graph and shape governing the observation and layout.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="observation">Validated semantic observation to interpret physically.</param>
    /// <param name="layout">Physical layout whose fields must all belong to <paramref name="shape"/>.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable indexed occurrence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default, or <paramref name="occurrence"/>, <paramref name="observation"/>, or
    /// <paramref name="layout"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Qualified identities conflict or a present observation field is absent from the layout.
    /// </exception>
    public static IndexedObservationOccurrence FromObservation(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        CoreObservation observation,
        ObservationLayout layout,
        ObservationLineage? lineage = null)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ShapeId != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Observation shape '{observation.ShapeId}' does not match supplied shape '{shape.QualifiedId}'.",
                nameof(observation));
        }

        return FromObservationCore(occurrence, observation, layout, lineage);
    }

    static IndexedObservationOccurrence FromObservationCore(
        RelationQueryObservationOccurrence occurrence,
        CoreObservation observation,
        ObservationLayout layout,
        ObservationLineage? lineage)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(observation);
        if (occurrence.Shape != observation.ShapeId)
        {
            throw new ArgumentException(
                $"Occurrence shape '{occurrence.Shape}' does not match observation shape '{observation.ShapeId}'.",
                nameof(observation));
        }

        ArgumentNullException.ThrowIfNull(layout);
        RequireLayoutShape(layout, occurrence.Shape);

        var values = new ObservationValue[layout.Count];
        var presence = new ulong[ObservationBuffer.RequiredWordCount(layout.Count)];
        foreach (var (fieldIdentity, value) in observation.Fields)
        {
            if (!layout.TryGetOrdinal(fieldIdentity, out var ordinal))
            {
                throw new ArgumentException(
                    $"Physical layout for shape '{occurrence.Shape}' omits present field '{fieldIdentity}'.",
                    nameof(layout));
            }

            values[ordinal] = value;
            ObservationBuffer.SetHasValue(presence, ordinal);
        }

        ObservationBuffer effectiveBuffer = new(values, presence, layout.Count);
        return new(
            occurrence,
            layout,
            effectiveBuffer,
            SnapshotLineage(lineage, layout, effectiveBuffer));
    }

    /// <summary>Creates and validates an indexed occurrence from ordinal-aligned physical buffers.</summary>
    /// <param name="shape">Exact graph and shape governing the physical row.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="layout">Ordinal layout for the supplied buffers.</param>
    /// <param name="valuesByOrdinal">One value slot per layout ordinal.</param>
    /// <param name="hasValueBitMask">Packed presence bits for the supplied value slots.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable, validated indexed occurrence.</returns>
    /// <remarks>
    /// The immutable <paramref name="layout"/> is retained and may be shared across rows. Mutable caller-owned value
    /// and presence buffers are snapshotted before this method returns.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default, or <paramref name="occurrence"/> or <paramref name="layout"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Qualified identities conflict, buffer lengths are invalid, the bitmap addresses an ordinal outside the
    /// layout, the layout contains a foreign field, or present fields do not adhere to the shape.
    /// </exception>
    public static IndexedObservationOccurrence Create(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        ObservationLayout layout,
        ReadOnlyMemory<ObservationValue> valuesByOrdinal,
        ReadOnlyMemory<ulong> hasValueBitMask,
        ObservationLineage? lineage = null)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(layout);
        if (occurrence.Shape != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Occurrence shape '{occurrence.Shape}' does not match supplied shape '{shape.QualifiedId}'.",
                nameof(occurrence));
        }

        RequireLayoutShape(layout, shape.QualifiedId);

        var valuesSnapshot = valuesByOrdinal.ToArray();
        var presenceSnapshot = hasValueBitMask.ToArray();
        var effectiveBuffer = new ObservationBuffer(valuesSnapshot, presenceSnapshot, layout.Count);
        RequireNoPresenceOutsideLayout(effectiveBuffer);
        if (!ObservationValidator.TryValidateAgainstShape(
                shape,
                layout,
                valuesSnapshot,
                presenceSnapshot,
                out var validationError))
        {
            throw new ArgumentException(
                $"Physical observation does not adhere to shape '{shape.QualifiedId}': {validationError}",
                nameof(valuesByOrdinal));
        }

        return new(
            occurrence,
            layout,
            effectiveBuffer,
            SnapshotLineage(lineage, layout, effectiveBuffer));
    }

    /// <summary>
    /// Reads and validates one plain JSON object directly into the shape's shared declaration-order layout.
    /// </summary>
    /// <param name="shape">Exact graph and shape governing the JSON object.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="utf8Json">Complete UTF-8 JSON object representing the occurrence fields.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable indexed occurrence backed directly by the newly parsed ordinal storage.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default or <paramref name="occurrence"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The occurrence belongs to another shape or the parsed fields do not satisfy the governing shape.
    /// </exception>
    /// <exception cref="JsonException">
    /// <paramref name="utf8Json"/> is empty, malformed, has trailing content, is not a JSON object, or contains an
    /// unknown or duplicate root property.
    /// </exception>
    public static IndexedObservationOccurrence FromJson(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        ReadOnlySpan<byte> utf8Json,
        ObservationLineage? lineage = null) =>
        FromJson(shape, occurrence, ObservationLayout.Create(shape), utf8Json, lineage);

    /// <summary>Reads and validates one plain JSON object directly into an explicit ordinal layout.</summary>
    /// <param name="shape">Exact graph and shape governing the JSON object.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="layout">Physical layout receiving the parsed fields.</param>
    /// <param name="utf8Json">Complete UTF-8 JSON object representing the occurrence fields.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable indexed occurrence backed directly by the newly parsed ordinal storage.</returns>
    /// <remarks>
    /// The parser allocates each value and presence buffer once and transfers their ownership to the result. It does
    /// not construct a root field dictionary, <see cref="JsonDocument"/>, or an intermediate semantic observation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default, or <paramref name="occurrence"/> or <paramref name="layout"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Qualified identities conflict or the parsed fields do not satisfy the governing shape.
    /// </exception>
    /// <exception cref="JsonException">
    /// <paramref name="utf8Json"/> is empty, malformed, has trailing content, is not a JSON object, or contains an
    /// unknown or duplicate root property.
    /// </exception>
    public static IndexedObservationOccurrence FromJson(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        ObservationLayout layout,
        ReadOnlySpan<byte> utf8Json,
        ObservationLineage? lineage = null)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(layout);
        if (occurrence.Shape != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Occurrence shape '{occurrence.Shape}' does not match supplied shape '{shape.QualifiedId}'.",
                nameof(occurrence));
        }

        var values = new ObservationValue[layout.Count];
        var presence = new ulong[ObservationBuffer.RequiredWordCount(layout.Count)];
        var reader = new Utf8JsonReader(utf8Json);
        if (!reader.Read())
            throw new JsonException("The JSON observation is empty.");
        if (!ObservationJsonReader.TryReadShape(
                ref reader,
                shape,
                layout,
                values,
                presence,
                out var validationError))
        {
            throw new ArgumentException(
                $"JSON observation does not adhere to shape '{shape.QualifiedId}': {validationError}",
                nameof(utf8Json));
        }
        var trailing = utf8Json[checked((int)reader.BytesConsumed)..];
        foreach (var character in trailing)
        {
            if (character is not (0x20 or 0x09 or 0x0A or 0x0D))
                throw new JsonException("The JSON observation contains trailing content.");
        }

        ObservationBuffer effectiveBuffer = new(values, presence, layout.Count);
        return new(
            occurrence,
            layout,
            effectiveBuffer,
            SnapshotLineage(lineage, layout, effectiveBuffer));
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetField(string fieldIdentity, out ObservationValue field)
    {
        if (!Layout.TryGetOrdinal(fieldIdentity, out var ordinal) || !buffer.HasValue(ordinal))
        {
            field = default;
            return false;
        }

        field = buffer.GetValue(ordinal);
        return true;
    }

    /// <summary>Attempts to read a field directly by physical ordinal.</summary>
    /// <param name="ordinal">Zero-based layout ordinal.</param>
    /// <param name="field">Field value when present; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the ordinal is valid and present; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetField(int ordinal, out ObservationValue field)
    {
        if (!buffer.HasValue(ordinal))
        {
            field = default;
            return false;
        }

        field = buffer.GetValue(ordinal);
        return true;
    }

    /// <summary>Projects this physical occurrence to the validated identity-free semantic observation.</summary>
    /// <param name="graph">Exact shape graph named by <see cref="ShapeId"/>.</param>
    /// <returns>A validated semantic observation with exactly the present physical fields.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="graph"/> has another identity or the physical fields no longer satisfy the shape.
    /// </exception>
    public CoreObservation ToObservation(ShapeGraph graph) =>
        CoreObservation.Create(graph, ShapeId, ObservationValue.FromObject(Fields));

    /// <summary>Writes this physical occurrence directly as canonical portable UTF-8 observation JSON.</summary>
    /// <param name="output">Caller-owned destination that receives the complete canonical representation.</param>
    /// <remarks>
    /// The ordinal buffer is serialized directly in the layout's cached canonical field order. The operation does
    /// not construct <see cref="Fields"/> or project through <see cref="ToObservation"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A retained value has no canonical portable JSON encoding.</exception>
    public void WriteCanonicalJson(IBufferWriter<byte> output) =>
        CanonicalJsonWriter.WriteCanonicalObservation(output, this);

    /// <summary>Materializes a CLR value using a shared compiled core plan directly over indexed reads.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="materializer">Compiled materializer for this occurrence's exact qualified shape.</param>
    /// <returns>The materialized CLR value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The materializer targets another shape, a required field is absent, or conversion fails.
    /// </exception>
    public T Materialize<T>(ObservationMaterializer<T> materializer)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        return materializer.Materialize(this);
    }

    static ObservationLineage SnapshotLineage(
        ObservationLineage? lineage,
        ObservationLayout layout,
        ObservationBuffer buffer)
    {
        if (lineage is null || lineage.Fields.Count == 0)
            return ObservationLineage.Empty;
        if (lineage.Fields.Any(static field => field is null))
            throw new ArgumentException("Occurrence lineage cannot contain null fields.", nameof(lineage));
        if (lineage.Fields.GroupBy(static field => field.TargetField, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Occurrence lineage cannot repeat a target field.", nameof(lineage));
        }

        List<FieldLineage> fields = new(lineage.Fields.Count);
        foreach (var field in lineage.Fields)
        {
            if (!layout.TryGetOrdinal(field.TargetField, out var ordinal) || !buffer.HasValue(ordinal))
            {
                throw new ArgumentException(
                    $"Occurrence lineage targets field '{field.TargetField}', which is absent from the physical occurrence.",
                    nameof(lineage));
            }
            if (field.Contributions.Any(static contribution => contribution is null))
                throw new ArgumentException("Occurrence field lineage cannot contain null contributions.", nameof(lineage));

            List<LineageContribution> contributions = new(field.Contributions.Count);
            foreach (var contribution in field.Contributions)
            {
                contributions.Add(new(
                    contribution.NodeId,
                    Array.AsReadOnly(contribution.SourcePaths.ToArray()),
                    contribution.Expression,
                    contribution.Reason));
            }

            fields.Add(new(field.TargetField, contributions.AsReadOnly()));
        }

        return new(fields.AsReadOnly());
    }

    static void RequireLayoutShape(ObservationLayout layout, QualifiedShapeId shape)
    {
        if (layout.ShapeId != shape)
        {
            throw new ArgumentException(
                $"Physical layout shape '{layout.ShapeId}' does not match occurrence shape '{shape}'.",
                nameof(layout));
        }
    }

    static void RequireNoPresenceOutsideLayout(ObservationBuffer buffer)
    {
        var remainder = buffer.FieldCount & 63;
        if (remainder == 0 || buffer.HasValueBitMask.Length == 0)
            return;

        var allowed = (1UL << remainder) - 1UL;
        if ((buffer.HasValueBitMask.Span[^1] & ~allowed) != 0)
        {
            throw new ArgumentException(
                "The physical presence bitmap contains values outside the observation layout.",
                "hasValueBitMask");
        }
    }

    static IReadOnlyDictionary<string, ObservationValue> BuildFields(
        ObservationLayout layout,
        ObservationBuffer buffer)
    {
        Dictionary<string, ObservationValue> result = new(layout.Count, StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < layout.Count; ordinal++)
        {
            if (buffer.HasValue(ordinal))
                result.Add(layout.FieldIdentities[ordinal], buffer.GetValue(ordinal));
        }

        return new ReadOnlyDictionary<string, ObservationValue>(result);
    }
}

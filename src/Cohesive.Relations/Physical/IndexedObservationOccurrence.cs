using System.Collections.ObjectModel;
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
/// Public factories snapshot caller-owned layout and buffer inputs and validate them against the exact supplied
/// graph. The resulting instance is immutable and safe to reuse across concurrent reads.
/// </para>
/// </remarks>
public sealed class IndexedObservationOccurrence : IObservationFieldReader
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

    /// <summary>Creates an indexed occurrence from a validated identity-free observation.</summary>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="observation">Validated semantic observation to interpret physically.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable indexed occurrence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="occurrence"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The occurrence and observation have different qualified shapes.
    /// </exception>
    public static IndexedObservationOccurrence FromObservation(
        RelationQueryObservationOccurrence occurrence,
        CoreObservation observation,
        ObservationLineage? lineage = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return FromObservationCore(
            occurrence,
            observation,
            new(
                observation.ShapeId.ShapeId,
                [.. observation.Fields.Keys.Order(StringComparer.Ordinal)]),
            lineage);
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
    /// Qualified identities conflict, the layout contains a foreign field, or a present observation field is absent
    /// from the layout.
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

        ValidateLayoutFields(shape, layout);
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

        var effectiveLayout = SnapshotLayout(layout);
        RequireLayoutShape(effectiveLayout, occurrence.Shape);

        var values = new ObservationValue[effectiveLayout.Count];
        var presence = new ulong[ObservationBuffer.RequiredWordCount(effectiveLayout.Count)];
        foreach (var (fieldIdentity, value) in observation.Fields)
        {
            if (!effectiveLayout.TryGetOrdinal(fieldIdentity, out var ordinal))
            {
                throw new ArgumentException(
                    $"Physical layout for shape '{occurrence.Shape}' omits present field '{fieldIdentity}'.",
                    nameof(layout));
            }

            values[ordinal] = value;
            ObservationBuffer.SetHasValue(presence, ordinal);
        }

        ObservationBuffer effectiveBuffer = new(values, presence, effectiveLayout.Count);
        return new(
            occurrence,
            effectiveLayout,
            effectiveBuffer,
            SnapshotLineage(lineage, effectiveLayout, effectiveBuffer));
    }

    /// <summary>Creates and validates an indexed occurrence from ordinal-aligned physical buffers.</summary>
    /// <param name="shape">Exact graph and shape governing the physical row.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="layout">Ordinal layout for the supplied buffers.</param>
    /// <param name="valuesByOrdinal">One value slot per layout ordinal.</param>
    /// <param name="hasValueBitMask">Packed presence bits for the supplied value slots.</param>
    /// <param name="lineage">Optional derived-field lineage for this occurrence.</param>
    /// <returns>An immutable, validated indexed occurrence.</returns>
    /// <remarks>All mutable caller-owned collections and buffers are snapshotted before this method returns.</remarks>
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

        var effectiveLayout = SnapshotLayout(layout);
        RequireLayoutShape(effectiveLayout, shape.QualifiedId);
        ValidateLayoutFields(shape, effectiveLayout);

        var valuesSnapshot = valuesByOrdinal.ToArray();
        var presenceSnapshot = hasValueBitMask.ToArray();
        var effectiveBuffer = new ObservationBuffer(valuesSnapshot, presenceSnapshot, effectiveLayout.Count);
        RequireNoPresenceOutsideLayout(effectiveBuffer);
        _ = CoreObservation.Create(shape, BuildFields(effectiveLayout, effectiveBuffer));

        return new(
            occurrence,
            effectiveLayout,
            effectiveBuffer,
            SnapshotLineage(lineage, effectiveLayout, effectiveBuffer));
    }

    /// <inheritdoc />
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

    static ObservationLayout SnapshotLayout(ObservationLayout layout) =>
        new(layout.Schema, [.. layout.FieldNames]);

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
        if (layout.Schema != shape.ShapeId)
        {
            throw new ArgumentException(
                $"Physical layout shape '{layout.Schema.Value}' does not match occurrence shape '{shape}'.",
                nameof(layout));
        }
    }

    static void ValidateLayoutFields(GraphShapeId shape, ObservationLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        RequireLayoutShape(layout, shape.QualifiedId);
        var definition = shape.Graph.GetShape(shape.ShapeId);
        foreach (var fieldIdentity in layout.FieldNames)
        {
            if (!definition.TryGetField(fieldIdentity, out _))
            {
                throw new ArgumentException(
                    $"Physical layout for shape '{shape.QualifiedId}' contains unknown field '{fieldIdentity}'.",
                    nameof(layout));
            }
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
                result.Add(layout.FieldNames[ordinal], buffer.GetValue(ordinal));
        }

        return new ReadOnlyDictionary<string, ObservationValue>(result);
    }
}

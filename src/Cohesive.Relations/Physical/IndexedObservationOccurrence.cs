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
/// the existing evaluation-scoped occurrence identity with an ordinal layout, dense value buffer, compact presence
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

    /// <summary>Gets derived-field lineage attached to this occurrence.</summary>
    public ObservationLineage Lineage { get; }

    /// <summary>Gets present fields keyed by canonical semantic identity.</summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields => fields ??= BuildFields(Layout, buffer);

    /// <summary>Creates a single-use builder that owns and transfers ordinal storage without defensive recopying.</summary>
    /// <param name="shape">Exact graph and shape governing the physical occurrence.</param>
    /// <param name="occurrence">Evaluation-scoped occurrence descriptor.</param>
    /// <param name="layout">Physical layout that assigns fields to ordinals.</param>
    /// <param name="lineage">Optional derived-field lineage for the completed occurrence.</param>
    /// <returns>A mutable ingestion boundary whose successful <see cref="Builder.Build"/> transfers owned storage.</returns>
    /// <remarks>
    /// Use this boundary when an adapter or database reader is producing new storage exclusively for one occurrence.
    /// Use <see cref="Create"/> when the supplied buffers remain caller-owned and must be snapshotted.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default, or <paramref name="occurrence"/> or <paramref name="layout"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The occurrence or layout belongs to another qualified shape.</exception>
    public static Builder CreateBuilder(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        ObservationLayout layout,
        ObservationLineage? lineage = null)
    {
        RequireOccurrenceAndLayout(shape, occurrence, layout);
        return new(shape, occurrence, layout, lineage);
    }

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
        ulong inlinePresence = 0;
        var presenceWords = ObservationBuffer.CreatePresenceWords(layout.Count);
        foreach (var (fieldIdentity, value) in observation.Fields)
        {
            if (!layout.TryGetOrdinal(fieldIdentity, out var ordinal))
            {
                throw new ArgumentException(
                    $"Physical layout for shape '{occurrence.Shape}' omits present field '{fieldIdentity}'.",
                    nameof(layout));
            }

            values[ordinal] = value;
            ObservationBuffer.SetHasValue(
                ref inlinePresence,
                presenceWords,
                layout.Count,
                ordinal);
        }

        ObservationBuffer effectiveBuffer = new(values, inlinePresence, presenceWords);
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
        RequireOccurrenceAndLayout(shape, occurrence, layout);

        SnapshotPresence(
            hasValueBitMask.Span,
            layout.Count,
            out var inlinePresence,
            out var presenceWords);
        var valuesSnapshot = valuesByOrdinal.ToArray();
        if (!TryValidateOwnedStorage(
                shape,
                layout,
                valuesSnapshot,
                inlinePresence,
                presenceWords,
                out var validationError))
        {
            throw new ArgumentException(
                $"Physical observation does not adhere to shape '{shape.QualifiedId}': {validationError}",
                nameof(valuesByOrdinal));
        }

        var effectiveBuffer = new ObservationBuffer(valuesSnapshot, inlinePresence, presenceWords);
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
        RequireOccurrenceAndLayout(shape, occurrence, layout);

        var values = new ObservationValue[layout.Count];
        Span<ulong> inlinePresenceWord = stackalloc ulong[1];
        inlinePresenceWord.Clear();
        var presenceWords = ObservationBuffer.CreatePresenceWords(layout.Count);

        var reader = new Utf8JsonReader(utf8Json);
        if (!reader.Read())
            throw new JsonException("The JSON observation is empty.");
        string? validationError;
        var isValid = layout.Count switch
        {
            0 => ObservationJsonReader.TryReadShape(
                ref reader,
                shape,
                layout,
                values,
                Span<ulong>.Empty,
                out validationError),
            <= 64 => ObservationJsonReader.TryReadShape(
                ref reader,
                shape,
                layout,
                values,
                inlinePresenceWord,
                out validationError),
            _ => ObservationJsonReader.TryReadShape(
                ref reader,
                shape,
                layout,
                values,
                presenceWords,
                out validationError),
        };
        if (!isValid)
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

        var inlinePresence = layout.Count is > 0 and <= 64 ? inlinePresenceWord[0] : 0;
        ObservationBuffer effectiveBuffer = new(values, inlinePresence, presenceWords);
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

    /// <summary>
    /// Single-use owned ingestion boundary for adapter- or database-produced ordinal state.
    /// </summary>
    /// <remarks>
    /// The builder exclusively owns its mutable buffers. A successful <see cref="Build"/> validates the complete
    /// physical occurrence, transfers those buffers without copying, and consumes the builder. Failed validation
    /// does not consume it, so callers may populate or replace fields and retry.
    /// </remarks>
    public sealed class Builder
    {
        readonly GraphShapeId shape;
        readonly RelationQueryObservationOccurrence occurrence;
        readonly ObservationLineage? lineage;
        ObservationValue[]? valuesByOrdinal;
        ulong inlinePresence;
        ulong[]? presenceWords;

        internal Builder(
            GraphShapeId shape,
            RelationQueryObservationOccurrence occurrence,
            ObservationLayout layout,
            ObservationLineage? lineage)
        {
            this.shape = shape;
            this.occurrence = occurrence;
            Layout = layout;
            this.lineage = lineage;
            valuesByOrdinal = new ObservationValue[layout.Count];
            presenceWords = ObservationBuffer.CreatePresenceWords(layout.Count);
        }

        /// <summary>Gets the exact immutable layout governing field ordinals accepted by this builder.</summary>
        public ObservationLayout Layout { get; }

        /// <summary>Assigns or replaces a field by canonical identity.</summary>
        /// <param name="fieldIdentity">Canonical top-level field identity in <see cref="Layout"/>.</param>
        /// <param name="value">Value to retain at the field's physical ordinal.</param>
        /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
        /// <exception cref="KeyNotFoundException"><paramref name="fieldIdentity"/> is absent from <see cref="Layout"/>.</exception>
        /// <exception cref="InvalidOperationException">The builder has already transferred its storage.</exception>
        public void SetField(string fieldIdentity, ObservationValue value)
        {
            _ = RequireActiveValues();
            SetField(Layout.GetOrdinal(fieldIdentity), value);
        }

        /// <summary>Assigns or replaces a field directly by physical ordinal.</summary>
        /// <param name="ordinal">Zero-based ordinal in <see cref="Layout"/>.</param>
        /// <param name="value">Value to retain at <paramref name="ordinal"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside <see cref="Layout"/>.</exception>
        /// <exception cref="InvalidOperationException">The builder has already transferred its storage.</exception>
        public void SetField(int ordinal, ObservationValue value)
        {
            var values = RequireActiveValues();
            if ((uint)ordinal >= (uint)values.Length)
                throw new ArgumentOutOfRangeException(nameof(ordinal));

            values[ordinal] = value;
            ObservationBuffer.SetHasValue(
                ref inlinePresence,
                presenceWords,
                values.Length,
                ordinal);
        }

        /// <summary>Validates the populated fields and transfers owned storage into an immutable occurrence.</summary>
        /// <returns>The immutable indexed occurrence backed by this builder's transferred storage.</returns>
        /// <exception cref="InvalidOperationException">
        /// The builder was already consumed or the populated fields do not satisfy the governing shape.
        /// </exception>
        /// <exception cref="ArgumentException">The supplied lineage is invalid for the populated fields.</exception>
        public IndexedObservationOccurrence Build()
        {
            var values = RequireActiveValues();
            if (!TryValidateOwnedStorage(
                    shape,
                    Layout,
                    values,
                    inlinePresence,
                    presenceWords,
                    out var validationError))
            {
                throw new InvalidOperationException(
                    $"Owned physical observation does not adhere to shape '{shape.QualifiedId}': {validationError}");
            }

            ObservationBuffer effectiveBuffer = new(values, inlinePresence, presenceWords);
            var result = new IndexedObservationOccurrence(
                occurrence,
                Layout,
                effectiveBuffer,
                SnapshotLineage(lineage, Layout, effectiveBuffer));
            valuesByOrdinal = null;
            presenceWords = null;
            return result;
        }

        ObservationValue[] RequireActiveValues() =>
            valuesByOrdinal
            ?? throw new InvalidOperationException("The indexed observation builder has already transferred its storage.");
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

    static void RequireOccurrenceAndLayout(
        GraphShapeId shape,
        RelationQueryObservationOccurrence occurrence,
        ObservationLayout layout)
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
    }

    static void SnapshotPresence(
        ReadOnlySpan<ulong> hasValueBitMask,
        int fieldCount,
        out ulong inlinePresence,
        out ulong[]? presenceWords)
    {
        var requiredWords = ObservationBuffer.RequiredWordCount(fieldCount);
        if (hasValueBitMask.Length != requiredWords)
        {
            throw new ArgumentException(
                "Physical presence bitmap length does not match the observation layout.",
                nameof(hasValueBitMask));
        }

        if (requiredWords == 0)
        {
            inlinePresence = 0;
            presenceWords = null;
        }
        else if (requiredWords == 1)
        {
            inlinePresence = hasValueBitMask[0];
            presenceWords = null;
        }
        else
        {
            inlinePresence = 0;
            presenceWords = hasValueBitMask.ToArray();
        }
    }

    static bool TryValidateOwnedStorage(
        GraphShapeId shape,
        ObservationLayout layout,
        ReadOnlySpan<ObservationValue> valuesByOrdinal,
        ulong inlinePresence,
        ulong[]? presenceWords,
        out string? validationError)
    {
        if (layout.Count == 0)
        {
            return ObservationValidator.TryValidateAgainstShape(
                shape,
                layout,
                valuesByOrdinal,
                ReadOnlySpan<ulong>.Empty,
                out validationError);
        }
        if (presenceWords is not null)
        {
            return ObservationValidator.TryValidateAgainstShape(
                shape,
                layout,
                valuesByOrdinal,
                presenceWords,
                out validationError);
        }

        ReadOnlySpan<ulong> inlinePresenceWords = [inlinePresence];
        return ObservationValidator.TryValidateAgainstShape(
            shape,
            layout,
            valuesByOrdinal,
            inlinePresenceWords,
            out validationError);
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

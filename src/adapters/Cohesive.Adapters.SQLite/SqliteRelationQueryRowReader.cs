using System.Diagnostics.CodeAnalysis;
using Cohesive.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Immutable core materializer and result layout for one native SQLite query artifact.</summary>
/// <typeparam name="T">CLR result type, including records and POCOs supported by the core materializer.</typeparam>
/// <remarks>
/// Create once with <see cref="SqliteRelationQueryCompiledArtifact.CreateRowMapping{T}"/> and share across
/// operations. Member conversion and missing-field policy belong to the core materializer. This mapping validates
/// the exact semantic field contracts and binds them to native ordinals; no per-row name lookup is required.
/// </remarks>
public sealed class SqliteRelationQueryRowMapping<T>
{
    readonly int fieldCount;

    internal SqliteRelationQueryRowMapping(SqliteRelationQueryCompiledArtifact artifact, GraphShapeId shape,
        Action<ObservationMaterializerBuilder<T>>? configure)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        var definition = shape.Graph.GetShape(shape.ShapeId);
        foreach (var field in artifact.ResultFields)
        {
            if (field.Field.Shape != shape.QualifiedId || field.Field.Path.Segments.Length != 1
                || !definition.TryGetField(field.Field.Path.Segments[0].Segment!, out var declared)
                || ValueContract.FromField(declared) != field.Contract)
                throw new ArgumentException("Result shape and field contracts must match the compiled artifact exactly.", nameof(shape));
        }

        Artifact = artifact;
        Layout = ObservationLayout.Create(shape, artifact.ResultFields.Select(field => field.Field.Path.Segments[0].Segment!));
        var builder = ObservationMaterializer.For<T>(shape);
        configure?.Invoke(builder);
        Materializer = builder.Compile(Layout);
        var lastOrdinal = artifact.BindingPresenceOrdinal;
        foreach (var field in artifact.ResultFields)
            lastOrdinal = Math.Max(lastOrdinal, Math.Max(field.ValueOrdinal, field.PresenceOrdinal));
        foreach (var occurrence in artifact.OccurrenceColumns)
        foreach (var component in occurrence.Components)
            lastOrdinal = Math.Max(lastOrdinal, component.Ordinal);
        fieldCount = lastOrdinal + 1;
    }

    /// <summary>Gets the immutable artifact whose result columns this mapping interprets.</summary>
    public SqliteRelationQueryCompiledArtifact Artifact { get; }

    /// <summary>Gets the shared semantic field layout bound into the core materializer.</summary>
    public ObservationLayout Layout { get; }

    /// <summary>Gets the compiled core interpretation, also usable with canonical observation readers.</summary>
    public ObservationMaterializer<T> Materializer { get; }

    /// <summary>Borrows an operation-owned provider reader without advancing or taking ownership of it.</summary>
    /// <param name="reader">Reader executing this artifact, before or after positioning on its first row.</param>
    /// <returns>A non-thread-safe typed reader scoped to the borrowed provider reader's lifetime.</returns>
    /// <remarks>
    /// Column count is checked once. The caller must execute this mapping's artifact: column count alone cannot
    /// establish query identity. Standard conversions produce CLR results that own their data and survive
    /// advancement/disposal; custom converters are responsible for their output ownership.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="ArgumentException">The provider column count differs from the compiled layout.</exception>
    /// <exception cref="InvalidOperationException">The provider reader is closed.</exception>
    public SqliteRelationQueryRowReader<T> Bind(SqliteDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.FieldCount != fieldCount)
            throw new ArgumentException("Reader column count does not match the compiled result layout.", nameof(reader));
        return new(this, reader);
    }
}

/// <summary>Borrowed current-row interpretation using a cached SQLite layout and core CLR materializer.</summary>
/// <typeparam name="T">CLR result type.</typeparam>
/// <remarks>
/// This object never advances, closes or disposes the provider reader. The caller owns that lifetime and must
/// position it on a row before reading. Values are decoded on demand through the shared SQLite scalar codec.
/// Typed reads return values only; use the artifact's canonical row API when contributor identities are needed.
/// </remarks>
public sealed class SqliteRelationQueryRowReader<T> : IOrdinalObservationFieldReader
{
    readonly SqliteRelationQueryRowMapping<T> mapping;
    readonly SqliteDataReader reader;

    internal SqliteRelationQueryRowReader(SqliteRelationQueryRowMapping<T> mapping, SqliteDataReader reader)
    {
        this.mapping = mapping;
        this.reader = reader;
    }

    /// <inheritdoc />
    public QualifiedShapeId ShapeId => Layout.ShapeId;

    /// <inheritdoc />
    public ObservationLayout Layout => mapping.Layout;

    /// <summary>Materializes the provider's current row without advancing it.</summary>
    /// <returns>A CLR value owning its mutable bytes, independent of other reads and the provider lifetime.</returns>
    /// <exception cref="InvalidOperationException">
    /// No provider row is current, the output binding is absent, presence encoding is invalid, or materialization fails.
    /// </exception>
    /// <exception cref="ArgumentException">A stored scalar violates its canonical contract.</exception>
    public T ReadCurrent() => TryReadCurrent(out var value)
        ? value
        : throw new InvalidOperationException("The current row has no output binding. Use TryReadCurrent to distinguish absence.");

    /// <summary>Materializes a current output binding while preserving whole-binding absence.</summary>
    /// <param name="value">Owned CLR result when present, otherwise the type default.</param>
    /// <returns>True for a present binding, including one with optional missing fields; false for an absent binding.</returns>
    /// <exception cref="InvalidOperationException">No row is current, presence encoding is invalid, or materialization fails.</exception>
    /// <exception cref="ArgumentException">A stored scalar violates its canonical contract.</exception>
    public bool TryReadCurrent([MaybeNullWhen(false)] out T value)
    {
        if (!SqliteRelationQueryCompiledArtifact.ReadPresence(reader, mapping.Artifact.BindingPresenceOrdinal))
        {
            foreach (var field in mapping.Artifact.ResultFields)
                if (SqliteRelationQueryCompiledArtifact.IsFieldPresent(reader, field))
                    throw new InvalidOperationException("An absent output binding contains a present field.");
            value = default;
            return false;
        }

        value = mapping.Materializer.Materialize(this);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetField(string fieldIdentity, out ObservationValue field)
    {
        if (Layout.TryGetOrdinal(fieldIdentity, out var ordinal))
            return TryGetField(ordinal, out field);
        field = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetField(int ordinal, out ObservationValue field)
    {
        if (!TryGetMapping(ordinal, out var column))
        {
            field = default;
            return false;
        }
        field = SqliteScalarCodec.Decode(column.Contract, reader.GetValue(column.ValueOrdinal));
        return true;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The ordinal identifies a field other than a single-valued scalar bytes contract.</exception>
    /// <exception cref="ArgumentException">The stored bytes or null violate the field's encoding or nullability.</exception>
    public bool TryGetBytes(int ordinal, out byte[]? value)
    {
        if (!TryGetMapping(ordinal, out var column))
        {
            value = null;
            return false;
        }
        value = SqliteScalarCodec.ReadOwnedBytes(column.Contract, reader, column.ValueOrdinal);
        return true;
    }

    bool TryGetMapping(int ordinal, [NotNullWhen(true)] out SqliteRelationQueryResultField? column)
    {
        if ((uint)ordinal >= (uint)mapping.Artifact.ResultFields.Length)
        {
            column = null;
            return false;
        }
        column = mapping.Artifact.ResultFields[ordinal];
        if (SqliteRelationQueryCompiledArtifact.IsFieldPresent(reader, column))
            return true;
        if (column.Contract.Presence == FieldPresence.Required
            && SqliteRelationQueryCompiledArtifact.ReadPresence(reader, mapping.Artifact.BindingPresenceOrdinal))
            throw new InvalidOperationException($"Required result field '{column.Field.Path}' is missing.");
        column = null;
        return false;
    }
}

namespace Cohesive.Model;

/// <summary>
/// Reads canonical top-level fields from one physical interpretation of an identity-free observation.
/// </summary>
/// <remarks>
/// This is an execution boundary, not another observation authority. Implementations must preserve the exact
/// <see cref="ShapeId"/> and field-value semantics of a validated <see cref="Observation"/>. Consumers that need
/// semantic validation or portable serialization should first project the physical representation to
/// <see cref="Observation"/>.
/// </remarks>
public interface IObservationFieldReader
{
    /// <summary>Gets the exact graph-qualified semantic shape governing the readable fields.</summary>
    QualifiedShapeId ShapeId { get; }

    /// <summary>Attempts to read a top-level field by canonical semantic identity.</summary>
    /// <param name="fieldIdentity">Canonical top-level field identity.</param>
    /// <param name="field">Field value when present; otherwise the default value.</param>
    /// <returns><see langword="true"/> when the field is present; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    bool TryGetField(string fieldIdentity, out ObservationValue field);
}

/// <summary>
/// Reads top-level observation fields by ordinals from one exact immutable <see cref="ObservationLayout"/>.
/// </summary>
/// <remarks>
/// Ordinals are physical execution addresses and are meaningful only for the exact shared <see cref="Layout"/>
/// instance. Consumers must retain name-based fallback behavior when a reader uses another layout. Nested
/// <see cref="ObservationValue"/> objects remain canonical semantic values and are not implicitly ordinalized.
/// </remarks>
public interface IOrdinalObservationFieldReader : IObservationFieldReader
{
    /// <summary>Gets the exact immutable layout governing ordinal reads from this reader.</summary>
    ObservationLayout Layout { get; }

    /// <summary>Attempts to read a top-level field by its physical ordinal.</summary>
    /// <param name="ordinal">Zero-based ordinal in <see cref="Layout"/>.</param>
    /// <param name="field">Field value when present; otherwise the default value.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="ordinal"/> is valid and its field is present; otherwise
    /// <see langword="false"/>.
    /// </returns>
    bool TryGetField(int ordinal, out ObservationValue field);

    /// <summary>Reads a scalar bytes field directly into independently owned mutable CLR storage.</summary>
    /// <param name="ordinal">Ordinal of a single-valued bytes field in <see cref="Layout"/>.</param>
    /// <param name="value">Caller-owned bytes when present, or null for an explicit null or missing field.</param>
    /// <returns>True for a present field, including explicit null; false for a missing or invalid ordinal.</returns>
    /// <remarks>
    /// The default implementation uses the core byte-array converter, including its JSON compatibility fallback,
    /// and copies canonical bytes. Physical readers may override this operation to avoid
    /// an intermediate immutable snapshot, but must preserve the field contract and return independently mutable
    /// storage. Changing that buffer must not affect this reader, another result, or
    /// retained observations. The default materializer uses this operation only for scalar bytes-to-byte-array
    /// mappings with its standard conversion policy; custom converters retain the canonical value path.
    /// </remarks>
    /// <exception cref="System.Text.Json.JsonException">A present value cannot be converted by the default byte-array policy.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be represented for the default conversion.</exception>
    bool TryGetBytes(int ordinal, out byte[]? value)
    {
        if (!TryGetField(ordinal, out var field))
        {
            value = null;
            return false;
        }

        value = DefaultObservationValueConverterCache.ReadBytes(field);
        return true;
    }
}

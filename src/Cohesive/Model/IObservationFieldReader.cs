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
}

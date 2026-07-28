using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Describes the semantic state of a value crossing a portable execution boundary.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortableValueState
{
    /// <summary>No value was supplied for the binding.</summary>
    Missing = 0,

    /// <summary>The source authoritatively reported that the value does not exist.</summary>
    Absent = 1,

    /// <summary>The source supplied an explicit null value.</summary>
    Null = 2,

    /// <summary>The value cannot currently be determined, but evaluation did not fail.</summary>
    Unknown = 3,

    /// <summary>Acquiring or evaluating the value failed.</summary>
    Failed = 4,

    /// <summary>A non-null, defined observation value is available.</summary>
    Concrete = 5
}

/// <summary>
/// A typed value that preserves absence, uncertainty, failure, and concrete data as distinct states.
/// </summary>
/// <remarks>
/// Instances are created through the state-specific factories. The factories prevent payload/state
/// combinations that cannot have a coherent meaning; <see cref="PortableExecutionValidator"/> validates
/// the value against its semantic contract and the portable execution subset.
/// </remarks>
[JsonConverter(typeof(PortableValueJsonConverter))]
public sealed record PortableValue
{
    PortableValue(
        ValueContract contract,
        PortableValueState state,
        ObservationValue? value = null,
        DocumentValidationDiagnostic? failure = null)
    {
        ArgumentNullException.ThrowIfNull(contract);

        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown portable value state.");
        if (state == PortableValueState.Concrete)
        {
            if (value is null)
                throw new ArgumentException("A concrete portable value requires an observation payload.", nameof(value));
            if (value.Value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            {
                throw new ArgumentException(
                    "A concrete portable value cannot contain an undefined or null root observation.",
                    nameof(value));
            }
        }
        else if (value is not null)
        {
            throw new ArgumentException(
                $"Portable value state '{state}' cannot contain an observation payload.",
                nameof(value));
        }

        if (state == PortableValueState.Failed)
        {
            ArgumentNullException.ThrowIfNull(failure);
            if (failure.Severity != DiagnosticSeverity.Error
                || string.IsNullOrWhiteSpace(failure.Code)
                || string.IsNullOrWhiteSpace(failure.Message))
            {
                throw new ArgumentException(
                    "A failed portable value requires an error diagnostic with a code and message.",
                    nameof(failure));
            }
        }
        else if (failure is not null)
        {
            throw new ArgumentException(
                $"Portable value state '{state}' cannot contain a failure diagnostic.",
                nameof(failure));
        }

        Contract = contract;
        State = state;
        Value = value;
        Failure = failure;
    }

    /// <summary>The semantic type, cardinality, presence, and nullability expected at this boundary.</summary>
    public ValueContract Contract { get; }

    /// <summary>The semantic state of this value.</summary>
    public PortableValueState State { get; }

    /// <summary>
    /// The observation payload for <see cref="PortableValueState.Concrete"/>; otherwise <see langword="null"/>.
    /// </summary>
    public ObservationValue? Value { get; }

    /// <summary>
    /// The acquisition or evaluation diagnostic for <see cref="PortableValueState.Failed"/>; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public DocumentValidationDiagnostic? Failure { get; }

    /// <summary>Creates a value for which no binding value was supplied.</summary>
    /// <param name="contract">The semantic contract governing the missing value.</param>
    /// <returns>A portable value in the <see cref="PortableValueState.Missing"/> state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static PortableValue Missing(ValueContract contract) =>
        new(contract, PortableValueState.Missing);

    /// <summary>Creates a value that the source authoritatively reported as nonexistent.</summary>
    /// <param name="contract">The semantic contract governing the absent value.</param>
    /// <returns>A portable value in the <see cref="PortableValueState.Absent"/> state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static PortableValue Absent(ValueContract contract) =>
        new(contract, PortableValueState.Absent);

    /// <summary>Creates an explicitly present null value.</summary>
    /// <param name="contract">The semantic contract governing the null value.</param>
    /// <returns>A portable value in the <see cref="PortableValueState.Null"/> state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static PortableValue Null(ValueContract contract) =>
        new(contract, PortableValueState.Null);

    /// <summary>Creates a value whose result cannot currently be determined.</summary>
    /// <param name="contract">The semantic contract governing the unknown value.</param>
    /// <returns>A portable value in the <see cref="PortableValueState.Unknown"/> state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    public static PortableValue Unknown(ValueContract contract) =>
        new(contract, PortableValueState.Unknown);

    /// <summary>Creates a value whose acquisition or evaluation failed.</summary>
    /// <param name="contract">The semantic contract governing the failed value.</param>
    /// <param name="failure">The structured diagnostic describing the failure.</param>
    /// <returns>A portable value in the <see cref="PortableValueState.Failed"/> state.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/> or <paramref name="failure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="failure"/> is not an error diagnostic or does not contain a code and message.
    /// </exception>
    public static PortableValue Failed(
        ValueContract contract,
        DocumentValidationDiagnostic failure) =>
        new(contract, PortableValueState.Failed, failure: failure);

    /// <summary>Creates a concrete, non-null, defined observation value.</summary>
    /// <param name="contract">The semantic contract governing the concrete value.</param>
    /// <param name="value">The concrete observation payload.</param>
    /// <returns>A portable value in the <see cref="PortableValueState.Concrete"/> state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is <see cref="ObservationValue.Undefined"/> or <see cref="ObservationValue.Null"/>.
    /// </exception>
    public static PortableValue Concrete(ValueContract contract, ObservationValue value) =>
        new(contract, PortableValueState.Concrete, value);
}

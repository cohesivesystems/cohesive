using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

internal static class ControlDerivedIdentity
{
    internal static ControlRecommendationId Recommendation(
        ControlLoopId loopId,
        string target,
        ControlEpochId epoch,
        ExecutionDefinitionFingerprint definitionFingerprint,
        ControlRevision expectedRevision,
        ControlObservationId observationId,
        ControlActuationId? priorActuationId,
        ControlRevision? priorActuationRevision)
    {
        var builder = new StringBuilder("control-recommendation/v2");
        Append(builder, loopId.Value);
        Append(builder, target);
        Append(builder, epoch.Value);
        Append(builder, definitionFingerprint.Algorithm);
        Append(builder, definitionFingerprint.Canonicalization);
        Append(builder, definitionFingerprint.Value);
        Append(builder, expectedRevision.Value);
        Append(builder, observationId.Value);
        Append(builder, priorActuationId is null ? "none" : "some");
        Append(builder, priorActuationId?.Value ?? string.Empty);
        Append(builder, priorActuationRevision?.Value ?? string.Empty);
        return new(builder.ToString());
    }

    internal static ControlActuationId Actuation(
        ControlRecommendation recommendation,
        ControlApplicationPoint applicationPoint)
    {
        var builder = new StringBuilder("control-actuation/v1");
        Append(builder, recommendation.Id.Value);
        Append(builder, applicationPoint.LoopId.Value);
        Append(builder, applicationPoint.Target);
        Append(builder, applicationPoint.Epoch.Value);
        Append(builder, applicationPoint.DefinitionFingerprint.Algorithm);
        Append(builder, applicationPoint.DefinitionFingerprint.Canonicalization);
        Append(builder, applicationPoint.DefinitionFingerprint.Value);
        Append(builder, applicationPoint.ExpectedRevision.Value);
        Append(builder, applicationPoint.Fence.Value);
        Append(builder, applicationPoint.Id.Value);
        Append(builder, applicationPoint.Authority);
        return new(builder.ToString());
    }

    internal static ControlActuationId LimitUpdateActuation(
        ControlLimitUpdateReceipt receipt,
        ControlApplicationPoint applicationPoint)
    {
        var command = receipt.Command;
        var builder = new StringBuilder("control-limit-update-actuation/v1");
        Append(builder, command.CommandId.Value);
        Append(builder, command.IdempotencyKey.Value);
        Append(builder, command.LoopId.Value);
        Append(builder, command.Target);
        Append(builder, command.Epoch.Value);
        Append(builder, command.DefinitionFingerprint.Algorithm);
        Append(builder, command.DefinitionFingerprint.Canonicalization);
        Append(builder, command.DefinitionFingerprint.Value);
        Append(builder, receipt.AcceptedRevision.Value);
        Append(builder, applicationPoint.Fence.Value);
        Append(builder, applicationPoint.Id.Value);
        Append(builder, applicationPoint.Authority);
        return new(builder.ToString());
    }

    static void Append(StringBuilder builder, string value) =>
        builder
            .Append(':')
            .Append(Encoding.UTF8.GetByteCount(value))
            .Append(':')
            .Append(value);
}

/// <summary>Stable identity of one independently regulated control loop.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlLoopId
{
    /// <summary>Creates a control-loop identity.</summary>
    /// <param name="value">Stable identity independent of a runtime object or process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ControlLoopId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable loop identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of the Process attempt, materialization generation, or other controlled epoch.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlEpochId
{
    /// <summary>Creates a control epoch identity.</summary>
    /// <param name="value">Stable identity that changes when stale control evidence must be fenced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ControlEpochId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable epoch identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable revision-scoped idempotency identity of one measured control observation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlObservationId
{
    /// <summary>Creates an observation identity.</summary>
    /// <param name="value">
    /// Stable identity reused only for the same logical observation within its exact loop, epoch, and expected revision.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ControlObservationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable observation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one non-authoritative control recommendation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlRecommendationId
{
    /// <summary>Creates a recommendation identity.</summary>
    /// <param name="value">Stable identity reused only for the same recommendation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ControlRecommendationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable recommendation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one invariant-preserving runtime application point.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlApplicationPointId
{
    /// <summary>Creates an application-point identity.</summary>
    /// <param name="value">
    /// Stable identity supplied by the Process or materialization runtime and unique within the exact loop, epoch,
    /// and expected revision.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ControlApplicationPointId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable application-point identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one applied control actuation receipt.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlActuationId
{
    /// <summary>Creates an actuation identity.</summary>
    /// <param name="value">Stable identity reused only for the same applied actuation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ControlActuationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable actuation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic durable revision of one control loop's state.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlRevision
{
    /// <summary>Initial control-state revision.</summary>
    public static ControlRevision Initial { get; } = new("1");

    /// <summary>Creates a positive canonical control revision.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not the canonical representation of a positive 64-bit integer.
    /// </exception>
    [JsonConstructor]
    public ControlRevision(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal <= 0
            || !string.Equals(value, ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A control revision must be a canonical positive 64-bit integer string.",
                nameof(value));
        }

        Value = value;
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive revision string.</summary>
    public string Value { get; }

    internal long Ordinal { get; }

    /// <summary>Returns the canonical revision string.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal ControlRevision Next()
    {
        RequireDefined(this, nameof(ControlRevision));
        return new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
    }

    internal static void RequireDefined(ControlRevision value, string parameterName)
    {
        if (value.Ordinal <= 0 || string.IsNullOrWhiteSpace(value.Value))
            throw new ArgumentException("A control revision must be defined and positive.", parameterName);
    }
}

/// <summary>Monotonic fence supplied by a Process or materialization safe-point authority.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ControlApplicationFence
{
    /// <summary>Creates a positive canonical application fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not the canonical representation of a positive 64-bit integer.
    /// </exception>
    [JsonConstructor]
    public ControlApplicationFence(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal <= 0
            || !string.Equals(value, ordinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A control application fence must be a canonical positive 64-bit integer string.",
                nameof(value));
        }

        Value = value;
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive fence string.</summary>
    public string Value { get; }

    internal long Ordinal { get; }

    /// <summary>Returns the canonical fence string.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal static void RequireDefined(ControlApplicationFence value, string parameterName)
    {
        if (value.Ordinal <= 0 || string.IsNullOrWhiteSpace(value.Value))
            throw new ArgumentException("A control application fence must be defined and positive.", parameterName);
    }
}

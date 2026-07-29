using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Stable identity of a canonical execution definition across its semantic revisions.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionDefinitionId
{
    /// <summary>Creates an execution-definition identity.</summary>
    /// <param name="value">Stable producer-assigned identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionDefinitionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw execution-definition identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw execution-definition identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of one semantic revision of an execution definition.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionRevisionId
{
    /// <summary>Creates an execution-revision identity.</summary>
    /// <param name="value">Stable producer-assigned revision identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionRevisionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw execution-revision identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw execution-revision identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of a semantic node within an execution definition.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionNodeId
{
    /// <summary>Creates an execution-node identity.</summary>
    /// <param name="value">Stable producer-assigned node identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionNodeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw execution-node identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw execution-node identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of a logical process instance across all of its attempts.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessInstanceId
{
    /// <summary>Creates a process-instance identity.</summary>
    /// <param name="value">Stable process-instance identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ProcessInstanceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw process-instance identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw process-instance identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of one recovery and continuity epoch of a logical process instance.
/// </summary>
/// <remarks>
/// Replay, operation retry, pause, and continue retain this identity. An explicit restart creates a new
/// process-attempt identity while retaining the owning <see cref="ProcessInstanceId"/>.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessAttemptId
{
    /// <summary>Creates a process-attempt identity.</summary>
    /// <param name="value">Stable process-attempt identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ProcessAttemptId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw process-attempt identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw process-attempt identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of one finite execution slice within a process attempt.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ActivationId
{
    /// <summary>Creates an activation identity.</summary>
    /// <param name="value">Stable activation identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ActivationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw activation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw activation identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of one durable control-flow token within a process attempt.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct TokenId
{
    /// <summary>Creates a token identity.</summary>
    /// <param name="value">Stable token identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public TokenId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw token identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw token identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of one durable wait occurrence registered by a Process token.
/// </summary>
/// <remarks>
/// A token may register the same semantic wait node more than once during one Process attempt. This identity
/// distinguishes those occurrences so an interaction can address an exact current or retained wait without
/// preventing deliberately unscoped early delivery to the token.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessWaitRegistrationId
{
    /// <summary>Creates a Process wait-registration identity.</summary>
    /// <param name="value">Replay-stable wait-occurrence identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ProcessWaitRegistrationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw wait-registration identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw wait-registration identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of one durable retry attempt to perform an external operation.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct OperationAttemptId
{
    /// <summary>Creates an operation-attempt identity.</summary>
    /// <param name="value">Stable operation-attempt identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public OperationAttemptId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw operation-attempt identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw operation-attempt identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Monotonically increasing ownership fence for one logical durable operation.
/// </summary>
/// <remarks>
/// A later fence supersedes every earlier claimant. The value is semantic ownership evidence, not a provider
/// lease token or physical transaction version.
/// </remarks>
[JsonConverter(typeof(OperationFenceJsonConverter))]
public readonly record struct OperationFence
{
    /// <summary>Creates an operation ownership fence.</summary>
    /// <param name="value">Positive monotonically increasing fence value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not positive.</exception>
    [JsonConstructor]
    public OperationFence(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "An operation fence must be positive.");
        }

        Value = value;
    }

    /// <summary>Positive monotonically increasing fence value.</summary>
    public long Value { get; }

    /// <summary>Returns the invariant-culture fence value.</summary>
    /// <returns>The positive fence value supplied at construction.</returns>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Portable string-encoded JSON converter for 64-bit operation fences.</summary>
/// <remarks>String encoding preserves the full 64-bit range across JavaScript and other JSON hosts.</remarks>
public sealed class OperationFenceJsonConverter : JsonConverter<OperationFence>
{
    /// <summary>Creates the canonical operation-fence JSON converter.</summary>
    public OperationFenceJsonConverter()
    {
    }

    /// <summary>Reads a positive invariant-culture fence from its canonical JSON string.</summary>
    /// <param name="reader">Reader positioned at the fence value.</param>
    /// <param name="typeToConvert">Requested fence type.</param>
    /// <param name="options">Active serializer options.</param>
    /// <returns>The parsed positive operation fence.</returns>
    /// <exception cref="JsonException">The value is not a canonical positive 64-bit integer string.</exception>
    public override OperationFence Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var encoded = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (encoded is null
            || !long.TryParse(
                encoded,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0
            || !string.Equals(encoded, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new JsonException("An operation fence must be encoded as a positive 64-bit integer string.");
        }

        return new(value);
    }

    /// <summary>Writes a fence as a canonical invariant-culture JSON string.</summary>
    /// <param name="writer">Destination JSON writer.</param>
    /// <param name="value">Positive operation fence.</param>
    /// <param name="options">Active serializer options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException"><paramref name="value"/> is the invalid default fence.</exception>
    public override void Write(
        Utf8JsonWriter writer,
        OperationFence value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Value <= 0)
        {
            throw new JsonException("A default operation fence cannot be serialized.");
        }

        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Composite identity that pins a durable continuation to one attempt of one logical process instance.
/// </summary>
/// <remarks>
/// A continuation admitted for an earlier attempt cannot be resumed after the process is explicitly restarted.
/// </remarks>
public sealed record ProcessContinuationIdentity
{
    /// <summary>Creates a process-continuation identity.</summary>
    /// <param name="processInstanceId">Logical process instance that owns the continuation.</param>
    /// <param name="processAttemptId">Specific process attempt that may resume the continuation.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="processInstanceId"/> or <paramref name="processAttemptId"/> is a default
    /// uninitialized identity.
    /// </exception>
    [JsonConstructor]
    public ProcessContinuationIdentity(
        ProcessInstanceId processInstanceId,
        ProcessAttemptId processAttemptId)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId.Value))
        {
            throw new ArgumentException(
                "A process continuation requires a non-default process-instance identity.",
                nameof(processInstanceId));
        }

        if (string.IsNullOrWhiteSpace(processAttemptId.Value))
        {
            throw new ArgumentException(
                "A process continuation requires a non-default process-attempt identity.",
                nameof(processAttemptId));
        }

        ProcessInstanceId = processInstanceId;
        ProcessAttemptId = processAttemptId;
    }

    /// <summary>Logical process instance that owns the continuation.</summary>
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Specific process attempt that may resume the continuation.</summary>
    public ProcessAttemptId ProcessAttemptId { get; }
}

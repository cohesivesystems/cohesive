using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Processes.IR;

/// <summary>Portable cancellation context supplied to an authored cancellation-finalizer Process.</summary>
/// <remarks>
/// The optional reason detail is the complete strict JSON representation of the original
/// <see cref="PortableValue"/>. This preserves its contract and semantic value state without making the fixed
/// cancellation input contract depend on an arbitrary application detail type.
/// </remarks>
public sealed record ProcessCancellationFinalizationContext
{
    /// <summary>Creates immutable cancellation context for one exact accepted control command.</summary>
    /// <param name="processInstanceId">Logical Process instance being cancelled.</param>
    /// <param name="attemptId">Exact Process attempt being cancelled.</param>
    /// <param name="commandId">Accepted cancellation command identity.</param>
    /// <param name="reasonCode">Stable machine-readable cancellation reason.</param>
    /// <param name="reasonDetail">Optional complete portable reason-detail JSON.</param>
    [JsonConstructor]
    public ProcessCancellationFinalizationContext(
        ProcessInstanceId processInstanceId,
        ProcessAttemptId attemptId,
        ProcessControlCommandId commandId,
        string reasonCode,
        JsonNode? reasonDetail = null)
    {
        if (string.IsNullOrWhiteSpace(processInstanceId.Value)
            || string.IsNullOrWhiteSpace(attemptId.Value)
            || string.IsNullOrWhiteSpace(commandId.Value))
        {
            throw new ArgumentException(
                "Cancellation finalization requires complete Process, attempt, and command identity evidence.");
        }

        ProcessInstanceId = processInstanceId;
        AttemptId = attemptId;
        CommandId = commandId;
        ReasonCode = Guard.RequireNotNullOrWhiteSpace(reasonCode);
        ReasonDetail = reasonDetail?.DeepClone();
    }

    /// <summary>Logical Process instance being cancelled.</summary>
    [JsonPropertyName("processInstanceId")]
    public ProcessInstanceId ProcessInstanceId { get; }

    /// <summary>Exact Process attempt being cancelled.</summary>
    [JsonPropertyName("attemptId")]
    public ProcessAttemptId AttemptId { get; }

    /// <summary>Accepted cancellation command identity.</summary>
    [JsonPropertyName("commandId")]
    public ProcessControlCommandId CommandId { get; }

    /// <summary>Stable machine-readable cancellation reason.</summary>
    [JsonPropertyName("reasonCode")]
    public string ReasonCode { get; }

    /// <summary>Optional complete strict JSON representation of the original portable reason detail.</summary>
    [JsonPropertyName("reasonDetail")]
    public JsonNode? ReasonDetail { get; }
}

/// <summary>Typed input to one authored cancellation-finalizer Process.</summary>
/// <typeparam name="TInput">CLR authoring type of the cancelled root Process input.</typeparam>
public sealed record ProcessCancellationFinalizationInput<TInput>
{
    /// <summary>Creates finalizer input from immutable root input and exact cancellation context.</summary>
    /// <param name="input">Immutable original root Process input.</param>
    /// <param name="cancellation">Exact accepted cancellation context.</param>
    [JsonConstructor]
    public ProcessCancellationFinalizationInput(
        TInput input,
        ProcessCancellationFinalizationContext cancellation)
    {
        Input = input;
        Cancellation = Guard.RequireNotNull(cancellation);
    }

    /// <summary>Immutable original root Process input.</summary>
    [JsonPropertyName("input")]
    public TInput Input { get; }

    /// <summary>Exact accepted cancellation context.</summary>
    [JsonPropertyName("cancellation")]
    public ProcessCancellationFinalizationContext Cancellation { get; }
}

/// <summary>Explicit acknowledgement returned by an authored cancellation-finalizer Process.</summary>
public sealed record ProcessCancellationAcknowledgement
{
    /// <summary>Creates an acknowledgement for one exact cancelled Process attempt.</summary>
    /// <param name="attemptId">Exact attempt whose cancellation work completed.</param>
    /// <exception cref="ArgumentException"><paramref name="attemptId"/> is default.</exception>
    [JsonConstructor]
    public ProcessCancellationAcknowledgement(ProcessAttemptId attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
            throw new ArgumentException("Cancellation acknowledgement requires an exact Process attempt.", nameof(attemptId));
        AttemptId = attemptId;
    }

    /// <summary>Exact attempt whose cancellation work completed.</summary>
    [JsonPropertyName("attemptId")]
    public ProcessAttemptId AttemptId { get; }
}

/// <summary>Canonical contracts for cancellation-finalizer child input and acknowledgement.</summary>
public static class ProcessCancellationFinalizationContracts
{
    static readonly TypeRef StringType = new ScalarTypeRef(ScalarTypeKind.String);
    static readonly ValueContract AcknowledgementValue = new(new ObjectTypeRef([
        new("attemptId", StringType)
    ]));

    static readonly TypeRef ContextType = new ObjectTypeRef([
        new("attemptId", StringType),
        new("commandId", StringType),
        new("processInstanceId", StringType),
        new("reasonCode", StringType),
        new(
            "reasonDetail",
            new JsonTypeRef(JsonTypeKind.Any),
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable)
    ]);

    /// <summary>Exact portable acknowledgement contract shared by every cancellation finalizer.</summary>
    public static ValueContract Acknowledgement => AcknowledgementValue;

    /// <summary>Derives the exact finalizer input contract from one root Process input contract.</summary>
    /// <param name="rootInput">Immutable root Process input contract.</param>
    /// <returns>A structural wrapper contract containing cancellation context and the root input occurrence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootInput"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootInput"/> has no portable element type.</exception>
    public static ValueContract Input(ValueContract rootInput)
    {
        ArgumentNullException.ThrowIfNull(rootInput);
        if (rootInput.Type is null)
            throw new ArgumentException("Cancellation finalization requires a typed root Process input.", nameof(rootInput));

        return new(new ObjectTypeRef([
            new("cancellation", ContextType),
            new(
                "input",
                rootInput.Type,
                rootInput.Cardinality,
                rootInput.Presence,
                rootInput.Nullability)
        ]));
    }
}

namespace Cohesive.Api.Execution.Services;

/// <summary>Caller-owned process admission intent. Preserve every field unchanged when retrying an ambiguous request.</summary>
/// <typeparam name="TInput">Declared process input.</typeparam>
/// <param name="CommandId">Stable native command identity.</param>
/// <param name="IdempotencyKey">Stable logical admission key.</param>
/// <param name="InstanceId">Requested process instance identity.</param>
/// <param name="AttemptId">Initial attempt identity.</param>
/// <param name="Input">Typed process input; contains no authorization authority.</param>
public sealed record ServiceProcessCommand<TInput>(string CommandId, string IdempotencyKey, string InstanceId,
    string AttemptId, TInput Input);

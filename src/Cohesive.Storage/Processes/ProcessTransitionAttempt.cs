using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>A native transition adapter whose exact operation identity can be safely replayed after an uncertain call.</summary>
/// <remarks>The target must atomically retain mutation and receipt. Implementing this interface is a capability
/// assertion, not permission to repeat arbitrary effects. The runtime persists dispatch before calling it.</remarks>
public interface IRecoverableProcessTransitionOperationAdapter : IProcessTransitionOperationAdapter
{
    /// <summary>Classifies a failed physical call without inferring non-commit from missing acknowledgement.</summary>
    /// <param name="exception">Observed physical failure.</param>
    /// <returns>Portable failure evidence; diagnostic text must not contain sensitive request contents.</returns>
    DurableOperationFailure DescribeFailure(Exception exception);
}

/// <summary>Native transition attempt evidence retained in the existing Process local-state ledger.</summary>
/// <param name="Invocation">Exact immutable operation intent, unchanged across physical retries.</param>
/// <param name="Attempt">Existing durable operation claim, fence, phase and failure evidence.</param>
/// <param name="Result">Acknowledged result, when durably retained; transport acceptance is insufficient.</param>
public sealed record ProcessTransitionAttempt(ProcessTransitionInvocation Invocation, DurableOperationAttempt Attempt,
    ProcessOperationResult? Result = null)
{
    internal const string Prefix = "process/transition-attempt/v1/";
    static readonly JsonSerializerOptions Json = ProcessDurableCheckpointJsonSerializer.CreateOptions();
    static readonly ValueContract Contract = new(new ScalarTypeRef(ScalarTypeKind.String));

    /// <summary>Reads native transition attempt evidence from one trusted Process snapshot.</summary>
    /// <param name="snapshot">Authoritative coherent aggregate, including its local-state ledger.</param>
    /// <returns>Attempts ordered by physical claim time and ordinal; no storage I/O is performed.</returns>
    /// <exception cref="InvalidOperationException">Retained attempt evidence has an invalid contract or identity.</exception>
    /// <exception cref="JsonException">Retained canonical JSON is malformed.</exception>
    /// <exception cref="ArgumentNullException">The snapshot is null.</exception>
    public static ImmutableArray<ProcessTransitionAttempt> Read(ProcessDurableStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = ImmutableArray.CreateBuilder<ProcessTransitionAttempt>();
        foreach (var state in snapshot.LocalState)
        {
            if (!state.Resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (state.Value.Contract != Contract || state.Value.State != PortableValueState.Concrete)
                throw new InvalidOperationException("Invalid native transition attempt contract.");
            var item = JsonSerializer.Deserialize<ProcessTransitionAttempt>(state.Value.Value!.Value.GetString()!, Json)
                ?? throw new InvalidOperationException("Missing native transition attempt.");
            if (item.Resource != state.Resource || item.Invocation.Continuation.ProcessInstanceId != snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId
                || item.Invocation.Process != snapshot.Checkpoint.Start.Request.Definition
                || item.Invocation.Context.AuthorityScope != snapshot.Checkpoint.Start.Request.Context.Authorization.AuthorityScope
                || (item.Result is not null) != (item.Attempt.Stage == DurableOperationAttemptStage.Acknowledged)
                || item.Result is { } outcome && !outcome.IsValidOutcome())
                throw new InvalidOperationException("Native transition attempt identity or outcome is invalid.");
            result.Add(item);
        }
        return [.. result.OrderBy(item => item.Attempt.Claim.ClaimedAtUtc).ThenBy(item => item.Attempt.Ordinal)];
    }

    internal string Resource => Prefix + ProcessStorageContentFingerprints.Value(
        new ProcessOperationOccurrence(Invocation.Continuation, Invocation.Activation, Invocation.Token, Invocation.Node, Invocation.Occurrence)).Value
        + "/" + Attempt.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal ProcessLocalMutation Mutation(long expectedVersion) => new(
        Resource + "/" + Attempt.Stage, Resource,
        PortableValue.Concrete(Contract, ObservationValue.FromObject(JsonSerializer.Serialize(this, Json))), expectedVersion);
}

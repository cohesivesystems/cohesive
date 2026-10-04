using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

static class ProcessStorageContentFingerprints
{
    static readonly System.Text.Json.JsonSerializerOptions Options = ProcessDurableCheckpointJsonSerializer.CreateOptions();

    internal static ProcessCommitFingerprint Input(ProcessActivationInput input) => Compute(input);

    internal static InteractionEnvelopeContentFingerprint Envelope(InteractionEnvelope envelope) =>
        InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope);

    // Snapshot identity, not logical Process identity, owns this derived evidence. Weak keys
    // release the digest with the immutable snapshot, without retaining payloads or canonical bytes.
    static readonly ConditionalWeakTable<ProcessContinuationState, Lazy<ProcessContinuationFingerprint>>
        ContinuationFingerprints = new();

    /// <summary>Returns exact sha256-v1 evidence for an immutable continuation snapshot.</summary>
    /// <remarks>
    /// Successful preparation is shared by object identity and concurrent calls. Weak ownership bounds
    /// retention to live snapshots; new snapshots and persisted projections prepare independent evidence.
    /// This operation does not replace checkpoint integrity or compatibility validation.
    /// </remarks>
    internal static ProcessContinuationFingerprint Continuation(ProcessContinuationState continuation)
    {
        var prepared = ContinuationFingerprints.GetValue(continuation, static snapshot => new(
            () => new(ComputeValue(snapshot)), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return prepared.Value;
        }
        catch
        {
            // Reuse successful evidence only; a failed preparation must remain retryable.
            ContinuationFingerprints.Remove(continuation);
            throw;
        }
    }

    internal static ProcessCommitFingerprint Control(ProcessControlState control) => Compute(control);

    internal static ProcessCommitFingerprint LocalMutation(ProcessLocalMutation mutation) => Compute(mutation);

    internal static ProcessCommitFingerprint Value<T>(T value) where T : class => Compute(value);

    internal static ProcessCommitFingerprint Value<T>(T value, System.Text.Json.JsonSerializerOptions options, string profile) where T : class =>
        new($"{profile}:{Convert.ToHexStringLower(SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(value, options)))}");

    static ProcessCommitFingerprint Compute<T>(T value) where T : class => new(ComputeValue(value));

    static string ComputeValue<T>(T value) where T : class => Value(value, Options, "sha256-v1").Value;
}

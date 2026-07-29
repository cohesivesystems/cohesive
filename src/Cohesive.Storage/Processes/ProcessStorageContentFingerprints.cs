using System.Security.Cryptography;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

static class ProcessStorageContentFingerprints
{
    static readonly System.Text.Json.JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    internal static ProcessCommitFingerprint Input(ProcessActivationInput input) => Compute(input);

    internal static InteractionEnvelopeContentFingerprint Envelope(InteractionEnvelope envelope) =>
        InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(envelope);

    internal static ProcessContinuationFingerprint Continuation(ProcessContinuationState continuation) =>
        new(ComputeValue(continuation));

    internal static ProcessCommitFingerprint Control(ProcessControlState control) => Compute(control);

    internal static ProcessCommitFingerprint LocalMutation(ProcessLocalMutation mutation) => Compute(mutation);

    internal static ProcessCommitFingerprint Value<T>(T value) where T : class => Compute(value);

    static ProcessCommitFingerprint Compute<T>(T value) where T : class => new(ComputeValue(value));

    static string ComputeValue<T>(T value) where T : class
    {
        var bytes = StrictDocumentJson.GetCanonicalBytes(value, Options);
        var digest = SHA256.HashData(bytes);
        return $"sha256-v1:{Convert.ToHexStringLower(digest)}";
    }
}

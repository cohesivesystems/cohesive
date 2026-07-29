using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Domain-separated deterministic identities used only by the Storage runtime interpretation.</summary>
static class ProcessDurableRuntimeIdentities
{
    internal static ProcessCommitId Initialization(ProcessStartReceipt start) =>
        new(Derive(
            "initialize",
            start.Request.Definition.DefinitionId.Value,
            start.Request.Definition.RevisionId.Value,
            start.Request.Context.CommandId.Value,
            start.Request.InitialContinuation.ProcessInstanceId.Value,
            start.Request.InitialContinuation.ProcessAttemptId.Value));

    internal static ProcessSafePointId SafePoint(
        ProcessContinuationIdentity continuation,
        ProcessActivation activation,
        ProcessContinuationFingerprint beforeContinuation) =>
        new(Derive(
            "safe-point",
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            activation.Id.Value,
            beforeContinuation.Value));

    internal static ProcessCommitId ActivationCommit(
        ProcessContinuationIdentity continuation,
        ProcessActivation activation,
        ProcessContinuationFingerprint beforeContinuation) =>
        new(Derive(
            "activation-commit",
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            activation.Id.Value,
            beforeContinuation.Value));

    internal static ProcessCommitId ControlCommit(
        ProcessInstanceId instanceId,
        ProcessControlCommandId commandId,
        ProcessControlRevision beforeRevision) =>
        new(Derive(
            "control-commit",
            instanceId.Value,
            commandId.Value,
            beforeRevision.Value));

    internal static ActivationId CancellationActivation(
        ProcessContinuationIdentity continuation,
        ProcessControlCommandId commandId) =>
        new(Derive(
            "cancellation-activation",
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            commandId.Value));

    internal static ProcessCommitId AffinityCommit(
        ProcessContinuationIdentity continuation,
        ProcessControlRevision beforeRevision,
        ProcessAttemptAffinity affinity) =>
        new(Derive(
            "affinity-commit",
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            beforeRevision.Value,
            affinity.Slot.Value,
            ProcessStorageContentFingerprints.Value(affinity).Value));

    internal static OperationAttemptId OperationAttempt(EmissionId operationId, int ordinal) =>
        new(Derive("operation-attempt", operationId.Value, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    internal static EmissionId OperationReply(EmissionId operationId) =>
        new(Derive("operation-reply", operationId.Value));

    internal static InteractionIdempotencyKey OperationReplyIdempotency(EmissionId operationId) =>
        new(Derive("operation-reply-idempotency", operationId.Value));

    internal static ProcessCommitId OperationLedgerCommit(DurableOperationState operation) =>
        new(Derive(
            "operation-ledger-commit",
            operation.OperationId.Value,
            ProcessStorageContentFingerprints.Value(operation).Value));

    static string Derive(string purpose, params string[] components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "cohesive.processes.runtime/v1");
        Append(hash, purpose);
        foreach (var component in components)
        {
            Append(hash, component);
        }

        return $"{purpose}/sha256-v1:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    static void Append(IncrementalHash hash, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }
}

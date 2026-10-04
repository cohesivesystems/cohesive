using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessContinuationFingerprintTests
{
    [Fact]
    public void RepeatedSnapshotReusesExactEvidenceWhileSuccessorsHaveIndependentEvidence()
    {
        var snapshot = ProcessDurabilityTestFixture.Create().Checkpoint.Continuation;
        var first = ProcessStorageContentFingerprints.Continuation(snapshot);
        Assert.Equal(Reference(snapshot), first.Value);
        Assert.Same(first.Value, ProcessStorageContentFingerprints.Continuation(snapshot).Value);
        var equivalent = Copy(snapshot);
        var equivalentFingerprint = ProcessStorageContentFingerprints.Continuation(equivalent);
        Assert.Equal(first, equivalentFingerprint);
        Assert.NotSame(first.Value, equivalentFingerprint.Value);
        var successor = Copy(snapshot, snapshot.CompletedActivationCount + 1);
        var successorFingerprint = ProcessStorageContentFingerprints.Continuation(successor);
        Assert.Equal(Reference(successor), successorFingerprint.Value);
        Assert.NotEqual(first, successorFingerprint);
    }

    [Fact]
    public void ConcurrentFirstUsePublishesOneSuccessfulDigest()
    {
        var snapshot = ProcessDurabilityTestFixture.Create().Checkpoint.Continuation;
        string[] results = new string[64];
        Parallel.For(0, results.Length, index =>
            results[index] = ProcessStorageContentFingerprints.Continuation(snapshot).Value);
        Assert.Equal(Reference(snapshot), results[0]);
        Assert.All(results, value => Assert.Same(results[0], value));
    }

    [Fact]
    public void WarmReuseDoesNotSerializeOrAllocatePayloadRepresentations()
    {
        var snapshot = ProcessDurabilityTestFixture.Create().Checkpoint.Continuation;
        var first = ProcessStorageContentFingerprints.Continuation(snapshot);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; index++)
            ProcessStorageContentFingerprints.Continuation(snapshot);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(snapshot);
        GC.KeepAlive(first);
        Assert.InRange(allocated, 0, 1024);
    }

    [Fact]
    public void MemoizationDoesNotRetainTheSnapshot()
    {
        var weak = PrepareUnrootedSnapshot();
        for (var attempt = 0; attempt < 3 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void SerializationFailuresRemainRetryable()
    {
        var original = ProcessDurabilityTestFixture.Create().Checkpoint.Continuation;
        var invalidValue = PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.Decimal)),
            ObservationValue.FromDouble(double.NaN));
        var token = original.Tokens[0];
        var snapshot = Copy(original, tokens: [NewToken(token.Id, token.Node, token.Disposition,
            token.Step, [new(new("invalid"), invalidValue)], token.RequestObligations,
            token.ForkMembership, token.Failure)]);
        var reference = Record.Exception(() => Reference(snapshot));
        var first = Record.Exception(() => ProcessStorageContentFingerprints.Continuation(snapshot));
        var second = Record.Exception(() => ProcessStorageContentFingerprints.Continuation(snapshot));
        Assert.NotNull(reference);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(reference.GetType(), first.GetType());
        Assert.Equal(first.GetType(), second.GetType());
        Assert.NotSame(first, second);
    }

    [Fact]
    public void PersistedCheckpointsStillValidateTheirDeclaredFingerprintEvidence()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation);
        var json = ProcessDurableCheckpointJsonSerializer.Serialize(fixture.Checkpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, fixture.Plan);
        var recomputed = ProcessStorageContentFingerprints.Continuation(restored.Continuation);
        Assert.Equal(original, recomputed);
        Assert.NotSame(original.Value, recomputed.Value);
        var tampered = JsonNode.Parse(json)!;
        tampered["activations"]![0]!["afterContinuation"] = "sha256-v1:" + new string('0', 64);
        var validation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            tampered.ToJsonString(), fixture.Plan, out _);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ProcessCheckpointJsonDiagnosticCodes.DeserializationInvalid);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference PrepareUnrootedSnapshot()
    {
        var snapshot = ProcessDurabilityTestFixture.Create().Checkpoint.Continuation;
        ProcessStorageContentFingerprints.Continuation(snapshot);
        return new(snapshot);
    }

    static string Reference(ProcessContinuationState snapshot) => "sha256-v1:" + Convert.ToHexStringLower(
        SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(snapshot, ProcessDurableCheckpointJsonSerializer.CreateOptions())));

    static ProcessContinuationState Copy(ProcessContinuationState snapshot, long? count = null,
        ImmutableArray<ProcessTokenState>? tokens = null) => NewContinuation(snapshot.Definition, snapshot.Continuation,
        count ?? snapshot.CompletedActivationCount, tokens ?? snapshot.Tokens, snapshot.Forks, snapshot.Children,
        snapshot.Partitions, snapshot.Recurrences, snapshot.Waits, snapshot.BufferedInputs, snapshot.InputReceipts,
        snapshot.OutstandingRequests, snapshot.Terminal, snapshot.CancellationFinalization);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessContinuationState NewContinuation(
        ExecutionDefinitionReference definition, ProcessContinuationIdentity continuation,
        long completedActivationCount, ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks, ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions, ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits, ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests, ExecutionTerminalOutcome terminal,
        ProcessCancellationFinalizationState? cancellationFinalization);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessTokenState NewToken(TokenId id, ExecutionNodeId node,
        ExecutionTokenDisposition disposition, long step, ImmutableArray<ProcessBindingValue> bindings,
        ImmutableArray<ProcessRequestObligation> requestObligations,
        ProcessForkMembership? forkMembership, DocumentValidationDiagnostic? failure);

}

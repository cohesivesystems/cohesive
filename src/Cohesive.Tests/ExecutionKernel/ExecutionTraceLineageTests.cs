using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionTraceLineageTests
{
    [Fact]
    public void ProcessOccurrenceEvidence_SeparatesDisclosureFromPayloadSafeIdentity()
    {
        var unavailable = new ProcessTraceOccurrenceEvidence(
            ExecutionTraceEvidenceDisclosure.Unavailable,
            ProcessTraceOccurrenceKind.Child);

        Assert.Equal(ExecutionTraceEvidenceDisclosure.Unavailable, unavailable.Disclosure);
        Assert.Null(unavailable.RegistrationId);
        Assert.Null(unavailable.Continuation);
        Assert.Throws<ArgumentException>(() => new ProcessTraceOccurrenceEvidence(
            ExecutionTraceEvidenceDisclosure.Unknown,
            ProcessTraceOccurrenceKind.Child,
            registrationId: "child/hidden"));
        Assert.Throws<ArgumentException>(() => new ProcessTraceOccurrenceEvidence(
            ExecutionTraceEvidenceDisclosure.Disclosed,
            ProcessTraceOccurrenceKind.Child,
            registrationId: "child/incomplete",
            ownerToken: new("token/owner"),
            occurrence: 0));
        Assert.Throws<ArgumentException>(() => new ProcessTraceOccurrenceEvidence(
            ExecutionTraceEvidenceDisclosure.Disclosed,
            ProcessTraceOccurrenceKind.Partition,
            registrationId: "partition/invalid-progress",
            ownerToken: new("token/owner"),
            occurrence: 0,
            progressIdentity: "item/1"));
    }

    [Fact]
    public void SemanticFingerprint_ChangesWithExactChildLineageAndRoundTripsStrictly()
    {
        var first = Trace("child/registration/1");
        var second = Trace("child/registration/2");

        Assert.NotEqual(
            ExecutionTraceFingerprinter.ComputeSemantic(first),
            ExecutionTraceFingerprinter.ComputeSemantic(second));
        var json = ExecutionTraceJsonSerializer.Serialize(first);
        Assert.Equal(json, ExecutionTraceJsonSerializer.Serialize(ExecutionTraceJsonSerializer.Deserialize(json)));
        Assert.Contains("child/registration/1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("child-private-input", json, StringComparison.Ordinal);
    }

    static NormalizedExecutionTrace Trace(string registrationId)
    {
        var continuation = new ProcessContinuationIdentity(
            new("process-instance/parent"),
            new("process-attempt/parent"));
        var childContinuation = new ProcessContinuationIdentity(
            new("process-instance/child"),
            new("process-attempt/child"));
        var child = Definition("process/child", 'b');
        var occurrence = new ProcessTraceOccurrenceEvidence(
            disclosure: ExecutionTraceEvidenceDisclosure.Disclosed,
            kind: ProcessTraceOccurrenceKind.Child,
            registrationId: registrationId,
            ownerToken: new("token/parent"),
            occurrence: 3,
            progressIdentity: "partition/item/42",
            definition: child,
            continuation: childContinuation);
        var traceEvent = new NormalizedExecutionTraceEvent(
            sequence: 0,
            kind: "childRegistered",
            node: new("invoke-child"),
            token: new("token/parent"),
            processOccurrence: occurrence,
            sourceReferences: ["tests/execution-trace-lineage"]);
        return new(
            schemaVersion: NormalizedExecutionTrace.CurrentSchemaVersion,
            kind: ProcessDefinitionDocuments.Kind,
            definition: Definition("process/parent", 'a'),
            continuation: continuation,
            activation: new("activation/parent"),
            disposition: "durableCut",
            safePointNode: new("invoke-child"),
            durableCommitSequence: null,
            events: [traceEvent]);
    }

    static ExecutionDefinitionReference Definition(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));
}

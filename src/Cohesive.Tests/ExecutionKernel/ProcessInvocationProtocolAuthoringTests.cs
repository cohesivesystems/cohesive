using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessInvocationProtocolAuthoringTests
{
    static readonly ExecutionDefinitionId RequestId = new("request/tests/child-invocation");
    static readonly ExecutionRevisionId RevisionId = new("revision/7");
    static readonly InteractionValueSchemaRevision InputRevision = new("schemas/tests/child-input/v3");
    static readonly InteractionValueSchemaRevision ResultRevision = new("schemas/tests/child-result/v4");
    static readonly ExecutionDefinitionId ReplyPrefix = new("reply/tests/child-invocation");
    static readonly ProcessChildOutcomeMapping CustomMapping = new(
        new("succeeded"),
        new("errored"),
        new("stopped"),
        new("killed"));

    [Fact]
    public void TypedProtocol_IsCanonicallyEquivalentToManualRequestAndReplies()
    {
        var process = ChildProcess();
        var policy = ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(30));

        var protocol = process.InvocationProtocol(
            RequestId,
            RevisionId,
            policy,
            Provenance(),
            InputRevision,
            ResultRevision,
            CustomMapping,
            ReplyPrefix,
            RevisionId);
        var manual = ManualDocuments(process, policy);

        Assert.Same(process, protocol.Process);
        Assert.Equal(CustomMapping, protocol.OutcomeMapping);
        Assert.Equal(manual.Length, protocol.Documents.Length);
        for (var index = 0; index < manual.Length; index++)
        {
            Assert.Equal(
                ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(manual[index]),
                ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(protocol.Documents[index]));
            Assert.Equal(manual[index].Metadata.Fingerprint, protocol.Documents[index].Metadata.Fingerprint);
        }

        Assert.Equal(Reference(protocol.RequestDocument), protocol.Request.Definition);
        Assert.Equal(Reference(protocol.Documents[1]), protocol.CompletedReply.Definition);
        Assert.Equal(Reference(protocol.Documents[2]), protocol.FailedReply.Definition);
        Assert.Equal(Reference(protocol.Documents[3]), protocol.CancelledReply.Definition);
        Assert.Equal(Reference(protocol.Documents[4]), protocol.TerminatedReply.Definition);
        Assert.True(protocol.Catalog.TryResolve(protocol.Request, out var request));
        Assert.IsType<RequestContractDefinition>(request);
    }

    [Fact]
    public void TypedProtocol_DerivesDeterministicDefaultsFromRequestAndProcess()
    {
        var process = ChildProcess();

        var first = process.InvocationProtocol(
            RequestId,
            RevisionId,
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(7)),
            Provenance());
        var second = process.InvocationProtocol(
            RequestId,
            RevisionId,
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(7)),
            Provenance());
        var request = Assert.IsType<RequestContractDefinition>(
            first.RequestDocument.GetDefinition<InteractionContractDefinition>());

        Assert.Equal(new RequestTerminalOutcomeId("completed"), first.OutcomeMapping.Completed);
        Assert.Equal(new RequestTerminalOutcomeId("failed"), first.OutcomeMapping.Failed);
        Assert.Equal(new RequestTerminalOutcomeId("cancelled"), first.OutcomeMapping.Cancelled);
        Assert.Equal(new RequestTerminalOutcomeId("terminated"), first.OutcomeMapping.Terminated);
        Assert.Equal(
            "request/tests/child-invocation/reply/completed",
            first.CompletedReply.Definition.DefinitionId.Value);
        Assert.Equal(
            "request/tests/child-invocation/input/revision/7",
            request.Payload.Revision.Value);
        Assert.Equal(
            "request/tests/child-invocation/result/revision/7",
            request.Response.Find(first.OutcomeMapping.Completed)!.Schema.Revision.Value);
        Assert.Equal(
            first.Documents.Select(static document => document.Metadata.Fingerprint),
            second.Documents.Select(static document => document.Metadata.Fingerprint));
    }

    [Fact]
    public void TypedProtocol_RejectsAProcessThatCannotBeJoinedAsAnExactChildAttempt()
    {
        var process = ChildProcess(ProcessRecoveryPolicy.RestartAttempt);

        var exception = Assert.Throws<InvalidOperationException>(() => process.InvocationProtocol(
            RequestId,
            RevisionId,
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(7)),
            Provenance()));

        Assert.Contains(nameof(ProcessRecoveryPolicy.ContinueAttempt), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ProcessRecoveryPolicy.RestartAttempt), exception.Message, StringComparison.Ordinal);
    }

    static Process<string, string> ChildProcess(
        ProcessRecoveryPolicy recoveryPolicy = ProcessRecoveryPolicy.ContinueAttempt) =>
        ProcessAuthoring.Create<string, string>(
            new(
                new("process/tests/invoked-child"),
                new("revision/2"),
                new("return"),
                recoveryPolicy,
                Provenance()),
            process => process.Return(new("return"), process.Input.Value));

    static ImmutableArray<ExecutionDefinitionDocument> ManualDocuments(
        Process<string, string> process,
        ProcessInvocationResponsePolicy policy)
    {
        var input = new InteractionValueSchema(process.Definition.Input, InputRevision);
        var result = new InteractionValueSchema(process.Definition.Result, ResultRevision);
        ImmutableArray<RequestTerminalOutcomeDefinition> outcomes =
        [
            new RequestResultDefinition(CustomMapping.Completed, result),
            new RequestFailureDefinition(CustomMapping.Failed, result),
            new RequestFailureDefinition(CustomMapping.Cancelled, result),
            new RequestFailureDefinition(CustomMapping.Terminated, result)
        ];
        var requestDocument = InteractionContractDocuments.Create(
            RequestId,
            RevisionId,
            new RequestContractDefinition(
                input,
                new(
                    outcomes,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    policy.LateResult,
                    policy.StaleResult,
                    policy.DuplicateResult,
                    policy.Retry,
                    policy.AmbiguousOutcome,
                    policy.UnresolvedOutcome,
                    policy.RetentionHorizon)),
            Provenance());
        RequestContractReference request = new(Reference(requestDocument));
        return
        [
            requestDocument,
            ReplyDocument(request, CustomMapping.Completed),
            ReplyDocument(request, CustomMapping.Failed),
            ReplyDocument(request, CustomMapping.Cancelled),
            ReplyDocument(request, CustomMapping.Terminated)
        ];
    }

    static ExecutionDefinitionDocument ReplyDocument(
        RequestContractReference request,
        RequestTerminalOutcomeId outcome) => InteractionContractDocuments.Create(
        new($"{ReplyPrefix.Value}/{outcome.Value}"),
        RevisionId,
        new ReplyContractDefinition(request, outcome),
        Provenance());

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance() => new(
        new("tests.process-invocation-protocol", "1"),
        new("tests/ari-366/process-invocation-protocol"),
        DocumentOrigin.User);
}

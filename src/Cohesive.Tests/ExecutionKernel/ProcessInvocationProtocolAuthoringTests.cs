using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Execution;
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
            "request/tests/child-invocation/failure/revision/7",
            request.Response.Find(first.OutcomeMapping.Failed)!.Schema.Revision.Value);
        Assert.Equal(
            new ValueContract(new DefaultClrTypeRefMapper().Map(typeof(ProcessChildFailure), null)),
            request.Response.Find(first.OutcomeMapping.Failed)!.Schema.Contract);
        Assert.Equal(
            new ValueContract(new DefaultClrTypeRefMapper().Map(typeof(ExecutionTerminalOutcomeKind), null)),
            request.Response.Find(first.OutcomeMapping.Cancelled)!.Schema.Contract);
        Assert.Equal(
            request.Response.Find(first.OutcomeMapping.Cancelled)!.Schema.Contract,
            request.Response.Find(first.OutcomeMapping.Terminated)!.Schema.Contract);
        Assert.Equal(
            first.Documents.Select(static document => document.Metadata.Fingerprint),
            second.Documents.Select(static document => document.Metadata.Fingerprint));
    }

    [Fact]
    public void TypedProtocol_DerivesExactDurableReplyMappingsWhileKeepingPhysicalPolicyExplicit()
    {
        var protocol = ChildProcess().InvocationProtocol(
            RequestId,
            RevisionId,
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(7)),
            Provenance(),
            outcomeMapping: CustomMapping);
        var reconciliation = new DurableOperationResolutionTarget(
            protocol.Process.Reference,
            new("reconcile"));

        var binding = protocol.BindDurably(
            maxAttempts: 3,
            claimLease: TimeSpan.FromMinutes(2),
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliationTarget: reconciliation);

        Assert.Equal(protocol.Request, binding.Request);
        Assert.Equal(3, binding.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(2), binding.ClaimLease);
        Assert.Equal(DurableOperationIdempotencyEvidence.TargetDeduplication, binding.IdempotencyEvidence);
        Assert.Equal(reconciliation, binding.ReconciliationTarget);
        Assert.Equal(
            new(CustomMapping.Completed, protocol.CompletedReply),
            binding.FindReply(CustomMapping.Completed));
        Assert.Equal(
            new(CustomMapping.Failed, protocol.FailedReply),
            binding.FindReply(CustomMapping.Failed));
        Assert.Equal(
            new(CustomMapping.Cancelled, protocol.CancelledReply),
            binding.FindReply(CustomMapping.Cancelled));
        Assert.Equal(
            new(CustomMapping.Terminated, protocol.TerminatedReply),
            binding.FindReply(CustomMapping.Terminated));
        Assert.True(new DurableOperationReferenceExecutor(protocol.Catalog).ValidateBinding(binding).IsValid);
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

    [Fact]
    public void TypedFinalizerProtocol_LowersThroughOneLifecycleDeclarationWithoutManualReferences()
    {
        var finalizerInput = ProcessCancellationFinalizationContracts.Input(
            new(new ScalarTypeRef(ScalarTypeKind.String)));
        var finalizer = ProcessAuthoring.Create<
            ProcessCancellationFinalizationInput<string>,
            ProcessCancellationAcknowledgement>(
            new(
                new("process/tests/cancellation-finalizer"),
                new("revision/1"),
                new("return"),
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            finalizerInput,
            ProcessCancellationFinalizationContracts.Acknowledgement,
            process => process.Return(
                new("return"),
                process.CanonicalValue<ProcessCancellationAcknowledgement>(
                    Expr.Const(ObservationValue.FromObject(
                        new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                        {
                            ["attemptId"] = ObservationValue.FromString("process-attempt/runtime")
                        })),
                    ProcessCancellationFinalizationContracts.Acknowledgement)));
        var protocol = finalizer.InvocationProtocol(
            new("request/tests/cancellation-finalizer"),
            new("revision/1"),
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(30)),
            Provenance());

        var parent = ProcessAuthoring.Create<string, string>(
            new(
                new("process/tests/cancellable-parent"),
                new("revision/1"),
                new("return"),
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            process =>
            {
                process.OnCancellation(new("cancel/finalize"), protocol);
                process.Return(new("return"), process.Input.Value);
            });

        var node = Assert.Single(parent.Definition.Nodes.OfType<CancellationFinalizerProcessNode>());
        Assert.Equal(protocol.Process.Reference, node.Process);
        Assert.Equal(protocol.Request, node.Contract);
        Assert.Equal(protocol.OutcomeMapping, node.OutcomeMapping);
        var linkValidation = ProcessDefinitionLink.TryCreateProcess(finalizer.Document, out var link);
        Assert.True(linkValidation.IsValid, Format(linkValidation));
        var compilation = parent.Compile(new ProcessDefinitionValidationContext(
            definitions: [Assert.IsType<ProcessDefinitionLink>(link)],
            interactionContracts: protocol.Catalog));
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
    }

    [Fact]
    public void ProcessChildFailure_RequiresTerminalNodeAndRetainsOptionalDiagnosticsStructurally()
    {
        var diagnostic = new DocumentValidationDiagnostic(
            ProcessExecutionDiagnosticCodes.OperationFailed,
            DiagnosticSeverity.Error,
            "Child operation failed.",
            "/operation");
        var first = new ProcessChildFailure(new("relation"), [diagnostic]);
        var second = new ProcessChildFailure(new("relation"), [diagnostic]);
        var authoredFailure = new ProcessChildFailure(new("fail"), default);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Empty(authoredFailure.Diagnostics);
        Assert.Throws<ArgumentException>(() => new ProcessChildFailure(default, []));
        Assert.Throws<ArgumentException>(() => new ProcessChildFailure(
            new("fail"),
            ImmutableArray.CreateRange<DocumentValidationDiagnostic>([null!])));
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
        var mapper = new DefaultClrTypeRefMapper();
        var failure = new InteractionValueSchema(
            new ValueContract(mapper.Map(typeof(ProcessChildFailure), null)),
            new($"{RequestId.Value}/failure/{RevisionId.Value}"));
        var cancelled = new InteractionValueSchema(
            new ValueContract(mapper.Map(typeof(ExecutionTerminalOutcomeKind), null)),
            new($"{RequestId.Value}/cancelled/{RevisionId.Value}"));
        var terminated = new InteractionValueSchema(
            new ValueContract(mapper.Map(typeof(ExecutionTerminalOutcomeKind), null)),
            new($"{RequestId.Value}/terminated/{RevisionId.Value}"));
        ImmutableArray<RequestTerminalOutcomeDefinition> outcomes =
        [
            new RequestResultDefinition(CustomMapping.Completed, result),
            new RequestFailureDefinition(CustomMapping.Failed, failure),
            new RequestFailureDefinition(CustomMapping.Cancelled, cancelled),
            new RequestFailureDefinition(CustomMapping.Terminated, terminated)
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

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}

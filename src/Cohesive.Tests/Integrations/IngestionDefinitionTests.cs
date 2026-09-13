using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.Integrations;

public sealed class IngestionDefinitionTests
{
    static readonly ValueContract TextContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PersistedDeclarationLowersDeterministicallyWithExactSourceAndRequestReferences()
    {
        var fixture = Fixture();
        var document = Declaration(fixture.Requests);
        var json = ExecutionDefinitionJsonSerializer.Serialize(document);
        Valid(IngestionDefinitionDocuments.TryDeserialize(json, out var reopened, out var declaration));
        Assert.Equal(new IngestionDefinition(fixture.Requests[0], fixture.Requests[1], fixture.Requests[2]), declaration);
        var lowered = IngestionDefinitionDocuments.TryLower(document, fixture.Catalog, out var first);
        Valid(lowered);
        Assert.Contains(lowered.Diagnostics, d => d.Code == "integrations.ingestion.realization.unqualified"
            && d.Severity == DiagnosticSeverity.Warning);
        Valid(IngestionDefinitionDocuments.TryLower(reopened!, fixture.Catalog, out var second));
        Assert.Equal(ExecutionDefinitionJsonSerializer.Serialize(first!), ExecutionDefinitionJsonSerializer.Serialize(second!));
        var source = first!.Metadata.Provenance.Source.Reference;
        Assert.Contains(document.Metadata.Fingerprint.Value, source);
        Assert.Equal(3, first.Metadata.SourceMap.Entries.Length);
        var compiled = ProcessStaticCompiler.Compile(first, new(interactionContracts: fixture.Catalog));
        Valid(compiled.Validation);
        Assert.True(compiled.IsSuccessful);
    }

    [Fact]
    public void SuccessOnlyOperationsDoNotIntroduceAnUnreachableFailureNode()
    {
        var fixture = Fixture(includeFailures: false);
        Compile(fixture);
    }

    [Fact]
    public void MultipleSuccessfulResultsRequireAnExplicitRoutingModel()
    {
        var fixture = Fixture(additionalSuccess: true);
        var validation = IngestionDefinitionDocuments.TryLower(Declaration(fixture.Requests), fixture.Catalog, out var process);
        Assert.Null(process);
        Assert.Contains(validation.Diagnostics, d => d.Code == "integrations.ingestion.result");
    }

    [Fact]
    public void SameShapeWithDifferentSchemaRevisionDoesNotSilentlyLink()
    {
        var fixture = Fixture(publishInputRevision: "different-meaning/v2");
        var validation = IngestionDefinitionDocuments.TryLower(Declaration(fixture.Requests), fixture.Catalog, out var process);
        Assert.Null(process);
        Assert.Contains(validation.Diagnostics, d => d.Code == "integrations.ingestion.payload" && d.Location == "/definition/publish");
    }

    [Fact]
    public void ChangedContractCannotSatisfyAnOldFingerprint()
    {
        var original = Fixture();
        var changed = Fixture(publishInputRevision: "changed/v2");
        var validation = IngestionDefinitionDocuments.TryLower(Declaration(original.Requests), changed.Catalog, out var process);
        Assert.False(validation.IsValid);
        Assert.Null(process);
    }

    [Fact]
    public void RetryWithoutStableIdentityIsRejectedBeforeProducingAProcess()
    {
        var fixture = Fixture(retry: RequestRetrySemantics.Never);
        var validation = IngestionDefinitionDocuments.TryLower(Declaration(fixture.Requests), fixture.Catalog, out var process);
        Assert.Null(process);
        Assert.Contains(validation.Diagnostics, d => d.Code == "integrations.ingestion.recovery");
    }

    [Fact]
    public void UnknownWireFieldsCannotIntroduceUninspectedSemantics()
    {
        var fixture = Fixture();
        var document = ExecutionDefinitionDocument.Create(IngestionDefinitionDocuments.Kind,
            new("ingestion/unknown-field"), new("v1"),
            new { Acquire = fixture.Requests[0], Publish = fixture.Requests[1], Settle = fixture.Requests[2], HiddenBehavior = true },
            Provenance());
        var validation = IngestionDefinitionDocuments.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(document), out _, out var definition);
        Assert.False(validation.IsValid);
        Assert.Null(definition);
    }

    [Fact]
    public void MissingRequestIsADiagnostic()
    {
        var fixture = Fixture();
        var document = IngestionDefinitionDocuments.Create(new("ingestion/test"), new("v1"),
            new(fixture.Requests[0], null!, fixture.Requests[2]), Provenance());
        var validation = IngestionDefinitionDocuments.TryLower(document, fixture.Catalog, out var process);
        Assert.False(validation.IsValid);
        Assert.Null(process);
    }

    [Theory]
    [InlineData("range:[2026-09-01,2026-09-07)")]
    [InlineData("cursor:opaque/source-owned/next-page")]
    public void ReferenceExecutionPassesSourceOwnedWorkAndSettlesOnlyAfterPublication(string selection)
    {
        var fixture = Fixture();
        var plan = Compile(fixture);
        var initial = ProcessReferenceInterpreter.Create(plan, new(new("instance/test"), new("attempt/1")), Text(selection));
        var activation = Activate(plan, "start", ProcessActivationCause.Start);
        var acquired = ProcessReferenceInterpreter.Activate(plan, initial, activation, RejectingHost.Instance);
        var first = Request(acquired, fixture.Requests[0]);
        Assert.Equal(Text(selection), first.Payload);
        var replay = ProcessReferenceInterpreter.Activate(plan, initial, activation, RejectingHost.Instance);
        var repeated = Request(replay, fixture.Requests[0]);
        Assert.Equal(first.Context.EmissionId, repeated.Context.EmissionId);
        Assert.Equal(first.Context.IdempotencyKey, repeated.Context.IdempotencyKey);

        var publish = Reply(plan, acquired, fixture.Replies[0, 0], "accepted", "complete-work:" + selection);
        Assert.Equal(Text("complete-work:" + selection), Request(publish, fixture.Requests[1]).Payload);
        var settle = Reply(plan, publish, fixture.Replies[1, 0], "accepted", "receipt:original-operation/1");
        Assert.Equal(Text("receipt:original-operation/1"), Request(settle, fixture.Requests[2]).Payload);
        // Re-evaluating the same accepted publication evidence produces the same settlement obligation.
        var replaySettle = Reply(plan, publish, fixture.Replies[1, 0], "accepted", "receipt:original-operation/1");
        Assert.Equal(Request(settle, fixture.Requests[2]).Context.EmissionId,
            Request(replaySettle, fixture.Requests[2]).Context.EmissionId);
        var completed = Reply(plan, settle, fixture.Replies[2, 0], "accepted", "settled");
        Assert.Equal(ProcessActivationDisposition.Completed, completed.Disposition);
        Assert.Empty(completed.Emissions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailureStopsTheFlowWithoutRepeatingEarlierEffects(int failedStage)
    {
        var fixture = Fixture();
        var plan = Compile(fixture);
        var initial = ProcessReferenceInterpreter.Create(plan, new(new("instance/failure"), new("attempt/1")), Text("selection"));
        var decision = ProcessReferenceInterpreter.Activate(plan, initial, Activate(plan, "start", ProcessActivationCause.Start), RejectingHost.Instance);
        for (var stage = 0; stage < failedStage; stage++)
            decision = Reply(plan, decision, fixture.Replies[stage, 0], "accepted", "evidence/" + stage);
        var failed = Reply(plan, decision, fixture.Replies[failedStage, 1], "rejected", "failure-evidence");
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Empty(failed.Emissions);
        Assert.Empty(failed.State.OutstandingRequests);
    }

    [Fact]
    public void AtomicWireDoesNotAcquireANullLedgerPropertyOrChangeItsKind()
    {
        var document = Declaration(Fixture().Requests);
        Assert.Equal(IngestionDefinitionDocuments.Kind, document.Kind);
        Assert.False(document.Definition.TryGetProperty("advanceLedger", out _));
    }

    [Fact]
    public void LedgerStepCannotBeSmuggledIntoTheAtomicProfile()
    {
        var fixture = Fixture(separateLedger: true);
        var invalid = ExecutionDefinitionDocument.Create(IngestionDefinitionDocuments.Kind, new("flow"), new("v1"),
            new IngestionDefinition(fixture.Requests[0], fixture.Requests[1], fixture.Requests[3], fixture.Requests[2]), Provenance());
        var validation = IngestionDefinitionDocuments.TryLower(invalid, fixture.Catalog, out var process);
        Assert.False(validation.IsValid);
        Assert.Null(process);
        Assert.Contains(validation.Diagnostics, x => x.Code == "integrations.ingestion.profile");
    }

    [Fact]
    public async Task SeparateLedgerFlowReconcilesLostAcknowledgmentsBeforeSettlement()
    {
        var fixture = Fixture(separateLedger: true);
        var source = Declaration(fixture.Requests);
        Assert.Equal(IngestionDefinitionDocuments.SeparateLedgerKind, source.Kind);
        Valid(IngestionDefinitionDocuments.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(source), out var reopened, out _));
        Valid(IngestionDefinitionDocuments.TryLower(reopened!, fixture.Catalog, out var process));
        Assert.Equal(4, process!.Metadata.SourceMap.Entries.Length);
        var compiled = ProcessStaticCompiler.Compile(process, new(interactionContracts: fixture.Catalog));
        Valid(compiled.Validation);
        var plan = compiled.Plan!;
        var initial = ProcessReferenceInterpreter.Create(plan, new(new("instance/ledger"), new("attempt/1")), Text("selection"));
        var acquire = ProcessReferenceInterpreter.Activate(plan, initial, Activate(plan, "start", ProcessActivationCause.Start), RejectingHost.Instance);
        var publish = Reply(plan, acquire, fixture.Replies[0, 0], "accepted", "prepared-work:operation/1");
        var publication = Request(publish, fixture.Requests[1]);
        // A reference sink retains the first result. Lost acknowledgment cannot authorize a new operation.
        var sink = new Dictionary<string, string>();
        var ledger = new InMemoryIngestionLedger();
        sink.Add(publication.Context.IdempotencyKey.Value, "original-sink-receipt/1");
        Assert.Null(await ledger.ReadAsync(new("flow", "source", "sink", "partition"), CancellationToken.None));
        var replayPublish = Reply(plan, acquire, fixture.Replies[0, 0], "accepted", "prepared-work:operation/1");
        Assert.Equal(publication.Context.IdempotencyKey, Request(replayPublish, fixture.Requests[1]).Context.IdempotencyKey);
        var ledgerRequest = Reply(plan, replayPublish, fixture.Replies[1, 0], "accepted", sink[publication.Context.IdempotencyKey.Value]);
        Request(ledgerRequest, fixture.Requests[2]);
        var intent = IngestionLedgerDocuments.Create(new(new("flow", "source", "sink", "partition"), Reference(source), 0,
            new IngestionDateRangePosition(new(2026, 9, 1), new(2026, 9, 2)),
            new("operation/1", "prepared-work-fingerprint/1", sink[publication.Context.IdempotencyKey.Value])), Provenance());
        var advanced = await ledger.AdvanceAsync(intent, CancellationToken.None);
        Assert.Equal(IngestionLedgerDisposition.Advanced, advanced.Disposition);
        // The ledger committed, but its reply was lost. Source settlement has not been emitted.
        Assert.Equal(fixture.Requests[2], Assert.IsType<RequestEnvelope>(Assert.Single(ledgerRequest.Emissions)).Contract);
        var recovered = await ledger.AdvanceAsync(intent, CancellationToken.None);
        Assert.Equal(IngestionLedgerDisposition.Replayed, recovered.Disposition);
        Assert.Equal(advanced.Receipt, recovered.Receipt);
        var settle = Reply(plan, ledgerRequest, fixture.Replies[2, 0], "accepted", "original-ledger-receipt/1");
        Request(settle, fixture.Requests[3]);
        Assert.Equal(ProcessActivationDisposition.Completed, Reply(plan, settle, fixture.Replies[3, 0], "accepted", "settled").Disposition);
        Assert.Single(sink);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SeparateLedgerTerminalFailureNeverEmitsALaterStage(int stage)
    {
        var fixture = Fixture(separateLedger: true);
        var plan = Compile(fixture);
        var initial = ProcessReferenceInterpreter.Create(plan, new(new("instance/failure"), new("attempt/1")), Text("selection"));
        var decision = ProcessReferenceInterpreter.Activate(plan, initial, Activate(plan, "start", ProcessActivationCause.Start), RejectingHost.Instance);
        for (var i = 0; i < stage; i++) decision = Reply(plan, decision, fixture.Replies[i, 0], "accepted", "evidence");
        var failed = Reply(plan, decision, fixture.Replies[stage, 1], "rejected", "conflict");
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Empty(failed.Emissions);
    }

    static CompiledProcessPlan Compile((InteractionContractCatalog Catalog, RequestContractReference[] Requests, ReplyContractReference[,] Replies) fixture)
    {
        Valid(IngestionDefinitionDocuments.TryLower(Declaration(fixture.Requests), fixture.Catalog, out var process));
        var compiled = ProcessStaticCompiler.Compile(process!, new(interactionContracts: fixture.Catalog));
        Valid(compiled.Validation);
        return Assert.IsType<CompiledProcessPlan>(compiled.Plan);
    }

    static RequestEnvelope Request(ProcessActivationDecision decision, RequestContractReference expected)
    {
        Assert.Empty(decision.Diagnostics);
        Assert.Equal(ProcessActivationDisposition.DurableCut, decision.Disposition);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));
        Assert.Equal(expected, request.Contract);
        return request;
    }

    static ProcessActivationDecision Reply(CompiledProcessPlan plan, ProcessActivationDecision waiting,
        ReplyContractReference contract, string outcome, string value)
    {
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(waiting.Emissions));
        var token = Assert.Single(waiting.State.Tokens);
        var emission = "reply/" + request.Context.EmissionId.Value;
        var context = new InteractionEnvelopeContext(new(emission),
            new ProcessInteractionOrigin(plan.DefinitionReference, new("source/reply"), waiting.State.Continuation, new("activation/source"), token.Id),
            new("correlation/test"), request.Context.EmissionId, new("authority/test", "tenant/test"),
            new("idempotency/" + emission), ordering: null,
            new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Provenance());
        RequestTerminalOutcome result = outcome == "accepted"
            ? new RequestResultOutcome(new(outcome), Text(value))
            : new RequestFailureOutcome(new(outcome), Text(value));
        var reply = new ReplyEnvelope(InteractionEnvelope.CurrentSchemaVersion, context, contract, request.Context.EmissionId, result);
        return ProcessReferenceInterpreter.Activate(plan, waiting.State,
            Activate(plan, emission, ProcessActivationCause.Interaction,
                [new(new ProcessTokenInteractionTarget(waiting.State.Continuation, token.Id), reply)]), RejectingHost.Instance);
    }

    static ProcessActivation Activate(CompiledProcessPlan plan, string id, ProcessActivationCause cause, ImmutableArray<ProcessActivationInput> inputs = default) =>
        new(new(id), cause, Now,
            new(new("authority/test", "tenant/test"), new("correlation/test"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), plan.Document.Metadata.Provenance), inputs);

    static (InteractionContractCatalog Catalog, RequestContractReference[] Requests, ReplyContractReference[,] Replies) Fixture(
        string publishInputRevision = "work/v1", RequestRetrySemantics retry = RequestRetrySemantics.StableIdentity,
        bool includeFailures = true, bool additionalSuccess = false, bool separateLedger = false)
    {
        string[] roles = separateLedger ? ["acquire", "publish", "advanceLedger", "settle"] : ["acquire", "publish", "settle"];
        string[] inputs = separateLedger ? ["selection/v1", publishInputRevision, "receipt/v1", "ledger-receipt/v1"] : ["selection/v1", publishInputRevision, "receipt/v1"];
        string[] results = separateLedger ? ["work/v1", "receipt/v1", "ledger-receipt/v1", "settled/v1"] : ["work/v1", "receipt/v1", "settled/v1"];
        string[] outcomes = ["accepted", "rejected"];
        var documents = new List<ExecutionDefinitionDocument>();
        var requests = new RequestContractReference[roles.Length];
        var replies = new ReplyContractReference[roles.Length, 2];
        for (var stage = 0; stage < roles.Length; stage++)
        {
            var terminals = ImmutableArray.CreateBuilder<RequestTerminalOutcomeDefinition>();
            terminals.Add(new RequestResultDefinition(new("accepted"), Schema(results[stage])));
            if (includeFailures) terminals.Add(new RequestFailureDefinition(new("rejected"), Schema("failure/v1")));
            if (additionalSuccess) terminals.Add(new RequestResultDefinition(new("alternate"), Schema(results[stage])));
            var request = InteractionContractDocuments.Create(new("request/" + roles[stage]), new("v1"),
                new RequestContractDefinition(Schema(inputs[stage]), new RequestResponseObligation(
                    terminals.ToImmutable(),
                    RequestOptionalTerminalSemantics.Unsupported, RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Observe, RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition, retry,
                    RequestResolutionSemantics.Reconcile, RequestResolutionSemantics.Escalate, TimeSpan.FromDays(30))), Provenance());
            documents.Add(request);
            requests[stage] = new(Reference(request));
            for (var outcome = 0; outcome < (includeFailures ? outcomes.Length : 1); outcome++)
            {
                var reply = InteractionContractDocuments.Create(new("reply/" + roles[stage] + "/" + outcomes[outcome]), new("v1"),
                    new ReplyContractDefinition(requests[stage], new(outcomes[outcome])), Provenance());
                documents.Add(reply);
                replies[stage, outcome] = new(Reference(reply));
            }
        }
        Valid(InteractionContractCatalog.TryCreate(documents, out var catalog));
        return (catalog!, requests, replies);
    }

    static ExecutionDefinitionDocument Declaration(RequestContractReference[] requests) =>
        IngestionDefinitionDocuments.Create(new("ingestion/source-to-destination"), new("v1"),
            new(requests[0], requests[1], requests[^1], AdvanceLedger: requests.Length == 4 ? requests[2] : null), Provenance());
    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);
    static InteractionValueSchema Schema(string revision) => new(TextContract, new(revision));
    static PortableValue Text(string value) => PortableValue.Concrete(TextContract, ObservationValue.FromString(value));
    static ExecutionProvenance Provenance() => new(new("ingestion-tests", "1"), new("tests/ingestion"), DocumentOrigin.Generated);
    static void Valid(DocumentValidationResult validation) =>
        Assert.True(validation.IsValid, string.Join("\n", validation.Diagnostics.Select(d => d.Code + ": " + d.Message)));

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static readonly RejectingHost Instance = new();
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new InvalidOperationException("Unexpected transition.");
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new InvalidOperationException("Unexpected relation.");
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Unexpected signal.");
    }
}

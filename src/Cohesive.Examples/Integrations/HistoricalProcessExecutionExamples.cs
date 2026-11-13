using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Configuration;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Cohesive.Model;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;
using static Cohesive.Examples.Integrations.TiingoCoverageEvidenceExamples;
using Layer = Cohesive.Configuration.SelectionLayer<Cohesive.Integrations.IntegrationChoice, Cohesive.Integrations.IntegrationSelectionPolicy>;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>Executes the authored Process through local evidence handlers; partial coverage follows its failure edge.</summary>
    /// <param name="sessions">Number of observed sessions in the synthetic response.</param>
    /// <returns>Completes after the Process reaches its coverage-dependent terminal state.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HistoricalProcessRequiresCompleteCoverageBeforeProgress(int sessions)
    {
        var fixture = new ProcessFixture(sessions);
        var decision = fixture.Start();
        while (decision.Disposition == ProcessActivationDisposition.DurableCut)
            decision = fixture.Reply(decision, await fixture.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions))));
        Assert.Equal(sessions == 2 ? ProcessActivationDisposition.Completed : ProcessActivationDisposition.Failed, decision.Disposition);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(sessions == 2 ? 1 : 0, fixture.Publications);
        Assert.Equal(sessions == 2 ? 1 : 0, fixture.Settlements);
        Assert.Equal(sessions == 2 ? 2 : 0, fixture.PublishedSessions);
        Assert.Equal(sessions == 2 ? 1L : null, (await fixture.Ledger.ReadAsync(fixture.Address, default))?.Revision);
        Assert.Equal(2 - sessions, fixture.Coverage!.MissingSessions.Length);
    }

    /// <summary>Retained replies survive a worker restart at every successful Request boundary without reacquisition.</summary>
    /// <param name="boundary">Completed handler whose reply has not yet been admitted to the Process.</param>
    /// <param name="change">Current dispatch condition changed while the worker was stopped.</param>
    /// <returns>Completes after the reopened Process consumes its retained reply and finishes.</returns>
    [Theory]
    [InlineData(0, "policy")]
    [InlineData(0, "expiry")]
    [InlineData(0, "default")]
    [InlineData(1, "policy")]
    [InlineData(2, "expiry")]
    [InlineData(3, "default")]
    public async Task HistoricalProcessResumesFromRetainedReply(int boundary, string change)
    {
        var fixture = new ProcessFixture(2);
        var worker = fixture;
        var decision = worker.Start();
        for (var index = 0; decision.Disposition == ProcessActivationDisposition.DurableCut; index++)
        {
            var request = Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));
            var outcome = await worker.Handle(request);
            ProcessContinuationState? reopened = null;
            if (index == boundary)
            {
                // Simulate a stopped worker after result retention but before reply admission.
                // No unknown effect is retried: only this exact retained reply is supplied to the reopened continuation.
                var options = InteractionEnvelopeJsonSerializer.CreateOptions();
                var state = JsonSerializer.Deserialize<ProcessContinuationState>(JsonSerializer.Serialize(decision.State, options), options)!;
                outcome = JsonSerializer.Deserialize<RequestTerminalOutcome>(JsonSerializer.Serialize(outcome, options), options)!;
                worker = new ProcessFixture(fixture, change);
                reopened = state;
            }
            decision = worker.Reply(decision, outcome, reopened);
        }
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(0, fixture.ReplacementCalls);
        Assert.Equal(1, fixture.Publications);
        Assert.Equal(1, fixture.Settlements);
        Assert.Equal(2, fixture.PublishedSessions);
        Assert.Equal(1, (await fixture.Ledger.ReadAsync(fixture.Address, default))!.Revision);
    }

    /// <summary>A changed preference cannot replace the connection pinned into the running Process.</summary>
    /// <returns>Completes after execution through the originally admitted connection.</returns>
    [Fact]
    public async Task HistoricalProcessKeepsAdmittedProviderWhenDefaultChanges()
    {
        var original = new ProcessFixture(2);
        var worker = new ProcessFixture(original, "default");
        var decision = worker.Start();
        while (decision.Disposition == ProcessActivationDisposition.DurableCut)
            decision = worker.Reply(decision, await worker.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions))));
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(1, original.ProviderCalls);
        Assert.Equal(0, original.ReplacementCalls);
        Assert.Equal(2, original.PublishedSessions);
        Assert.Equal(1, (await original.Ledger.ReadAsync(original.Address, default))!.Revision);
    }

    /// <summary>Current dispatch restrictions follow the authored failure edge without touching source or sink.</summary>
    /// <param name="change">Current admission restriction.</param>
    /// <returns>Completes after observing a failure without source, sink or ledger effects.</returns>
    [Theory]
    [InlineData("policy")]
    [InlineData("expiry")]
    public async Task HistoricalProcessRejectsNewAcquisitionAfterAdmissionChanges(string change)
    {
        var original = new ProcessFixture(2);
        var worker = new ProcessFixture(original, change);
        var waiting = worker.Start();
        var result = await worker.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(waiting.Emissions)));
        var failed = worker.Reply(waiting, result);
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Equal(0, original.ProviderCalls);
        Assert.Equal(0, original.Publications);
        Assert.Equal(0, original.Settlements);
        Assert.Null(await original.Ledger.ReadAsync(original.Address, default));
    }

    // Local host adapter: the canonical Process decides order and branching. This fixture supplies only
    // Request handlers and reply admission. Shared retained state models storage, not physical durability.
    sealed class ProcessFixture : IProcessReferenceHost
    {
        readonly ProcessFixture retained;
        readonly IntegrationCompilation compilation;
        internal IntegrationCompilation Compilation => compilation;
        readonly CompiledProcessPlan plan;
        readonly IngestionDefinition definition;
        readonly string change;
        readonly int sessions;
        readonly Dictionary<RequestContractReference, Func<RequestEnvelope, ValueTask<RequestTerminalOutcome>>> handlers;
        readonly IntegrationRuntime runtime;
        readonly HistoricalCoverageScope scope;
        byte[]? publicationBytes;
        readonly Dictionary<string, byte[]> sink = new();
        ExecutionDefinitionDocument? requested, acquired, prepared;
        IngestionPublicationReceipt? receipt;
        ProgressReceipt? progress;
        internal CoverageEvidence? Coverage;
        readonly LocalAcquisition first = new(), second = new();
        internal int PublishedSessions => sink.Values.Sum(bytes => JsonSerializer.Deserialize<DailyBar[]>(bytes)!.Length);
        internal int ProviderCalls => first.Calls;
        internal int ReplacementCalls => second.Calls;
        internal int Publications, Settlements;
        internal InMemoryIngestionLedger Ledger = new();
        internal IngestionLedgerAddress Address => compilation.Binding!.GetDefinition<IngestionProviderBinding>().Consumer;
        ExecutionProvenance Origin => plan.Document.Metadata.Provenance;

        internal ProcessFixture(int sessions) : this(null, "unchanged", sessions) { }
        internal ProcessFixture(ProcessFixture original, string change) : this(original, change, original.sessions) { }
        ProcessFixture(ProcessFixture? original, string change, int sessions)
        {
            retained = original?.retained ?? this;
            this.change = change;
            this.sessions = sessions;
            compilation = Reopen(original?.compilation ?? QualifyLocalHistorical().Compilation!);
            Assert.True(InteractionContractCatalog.TryCreate(compilation.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var contracts).IsValid);
            var compiled = ProcessStaticCompiler.Compile(compilation.Process!, new(interactionContracts: contracts));
            Assert.True(compiled.Validation.IsValid, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
            plan = compiled.Plan!;
            var binding = compilation.Binding!.GetDefinition<IngestionProviderBinding>();
            var property = compilation.Documents.Single(d => d.Reference == binding.Contract.Properties[0].Property)
                .GetDefinition<ContractPropertyDefinition>();
            scope = compilation.Documents.Single(d => d.Reference == property.Parameters).GetDefinition<HistoricalCoverageScope>();
            definition = compilation.Documents.Single(d => d.Reference == binding.Contract.Ingestion).GetDefinition<IngestionDefinition>();
            runtime = new IntegrationRuntime().Register(Profile, Connection("v1"), retained.first)
                .Register(Profile, Connection("v2"), retained.second);
            handlers = new()
            {
                [definition.Acquire] = Acquire,
                [definition.Publish] = Publish,
                [definition.AdvanceLedger!] = Advance,
                [definition.Settle] = Settle
            };
        }

        internal ProcessActivationDecision Start(ProcessContinuationIdentity? continuation = null) => ProcessReferenceInterpreter.Activate(plan,
            ProcessReferenceInterpreter.Create(plan, continuation ?? new(new("example/historical"), new("attempt/1")), Value(plan.Definition.Input, scope)),
            Activation("start", ProcessActivationCause.Start), this);

        internal ValueTask<RequestTerminalOutcome> Handle(RequestEnvelope request) => handlers[request.Contract](request);

        ValueTask<RequestTerminalOutcome> Acquire(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, scope), request.Payload);
            var current = Layer.Define("example/local-policy", "v2", "research-admin", new())
                .Prefer(IntegrationChoice.Select(Profile, Connection(change == "default" ? "v2" : "v1")));
            if (change == "policy") current = current.Enforce(new(AllowedProviders: []));
            LocalAcquisition provider;
            try
            {
                provider = runtime.ResolveForDispatch<LocalAcquisition>(compilation, [current.Document],
                    [new SelectionGrant("research-admin", new())], change == "expiry" ? At.AddDays(1) : At);
            }
            catch (InvalidOperationException)
            {
                return ValueTask.FromResult(Outcome(request, new IntegrationFailure("dispatch-denied"), success: false));
            }
            provider.Acquire();
            var bytes = Response(sessions);
            var content = IngestionContentReference.Describe("response.json", bytes, "application/json");
            retained.Coverage = TiingoCoverageEvidenceExamples.Evaluate(scope, content, bytes);
            if (!retained.Coverage.IsComplete)
                return ValueTask.FromResult(Outcome(request, new IntegrationFailure("missing-sessions"), success: false));
            var transformation = ContractPropertyDocuments.DeclareContext(new("example/process-normalizer"), new("v1"), scope.Normalizer, Origin);
            retained.requested = IngestionWorkDocuments.Create(new IngestionAcquisitionRequest(Address,
                compilation.Binding!.GetDefinition<IngestionProviderBinding>().Contract.Ingestion, transformation.Reference,
                "historical/1", "attempt/1", IngestionContentReference.Describe("selection.json", JsonSerializer.SerializeToUtf8Bytes(scope), "application/json"), 0, At), Origin);
            retained.acquired = IngestionWorkDocuments.Create(new IngestionAcquisitionReceipt(retained.requested.Reference, content, At.AddSeconds(1)), Origin);
            retained.publicationBytes = Prepare(retained.Coverage);
            retained.prepared = IngestionWorkDocuments.Create(new IngestionPreparedPublication(retained.acquired.Reference, transformation.Reference,
                IngestionContentReference.Describe("publication.json", retained.publicationBytes, "application/json"),
                new IngestionDateRangePosition(scope.StartDate, scope.EndDateInclusive.AddDays(1)), At.AddSeconds(2)), Origin);
            return ValueTask.FromResult(Outcome(request, new PreparedPublication(retained.prepared.Reference.DefinitionId.Value)));
        }

        ValueTask<RequestTerminalOutcome> Publish(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, new PreparedPublication(retained.prepared!.Reference.DefinitionId.Value)), request.Payload);
            Assert.True(IngestionWorkDocuments.ValidateChain(retained.requested!, retained.acquired!, retained.prepared!).IsValid);
            var prepared = Assert.IsType<IngestionPreparedPublication>(retained.prepared!.GetDefinition<IngestionWorkItem>());
            Assert.True(prepared.Content.Matches(retained.publicationBytes!));
            retained.sink.Add(prepared.Content.Locator, retained.publicationBytes!);
            retained.Publications++;
            retained.receipt = new("historical/1", prepared.Content.Sha256, "local-sink/receipt/1");
            return ValueTask.FromResult(Outcome(request, new PublicationReceipt(retained.receipt.ReceiptReference)));
        }

        async ValueTask<RequestTerminalOutcome> Advance(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, new PublicationReceipt(retained.receipt!.ReceiptReference)), request.Payload);
            var advance = IngestionWorkDocuments.CreateLedgerAdvance(retained.requested!, retained.acquired!, retained.prepared!, retained.receipt!, Origin);
            var result = await retained.Ledger.AdvanceAsync(advance, default);
            Assert.Equal(IngestionLedgerDisposition.Advanced, result.Disposition);
            retained.progress = new ProgressReceipt(result.Receipt!.Advance.DefinitionId.Value);
            return Outcome(request, retained.progress);
        }

        ValueTask<RequestTerminalOutcome> Settle(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, retained.progress!), request.Payload);
            retained.Settlements++;
            return ValueTask.FromResult(Outcome(request, new SettlementReceipt("settled/1")));
        }

        RequestContractDefinition Request(RequestEnvelope request) => compilation.Documents.Single(d => d.Reference == request.Contract.Definition)
            .GetDefinition<InteractionContractDefinition>() as RequestContractDefinition ?? throw new InvalidDataException();

        RequestTerminalOutcome Outcome(RequestEnvelope request, object payload, bool success = true)
        {
            var outcome = Request(request).Response.TerminalOutcomes.Single(d => success ? d is RequestResultDefinition : d is RequestFailureDefinition);
            var value = Value(outcome.Schema.Contract, payload);
            return success ? new RequestResultOutcome(outcome.Id, value) : new RequestFailureOutcome(outcome.Id, value);
        }

        internal ProcessActivationDecision Reply(ProcessActivationDecision waiting, RequestTerminalOutcome outcome, ProcessContinuationState? reopened = null)
        {
            var state = reopened ?? waiting.State;
            var request = Assert.IsType<RequestEnvelope>(Assert.Single(waiting.Emissions));
            var replyDocument = compilation.Documents.Single(d => d.Kind == InteractionContractDocuments.Kind
                && d.GetDefinition<InteractionContractDefinition>() is ReplyContractDefinition reply && reply.Request == request.Contract && reply.Outcome == outcome.Id);
            var token = Assert.Single(state.Tokens);
            var id = "reply/" + request.Context.EmissionId.Value;
            var context = new InteractionEnvelopeContext(new(id),
                new ProcessInteractionOrigin(plan.DefinitionReference, new("fixture/reply"), state.Continuation, new(id), token.Id),
                new("historical/1"), request.Context.EmissionId, new("example", "local"), new(id), null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Origin);
            var reply = new ReplyEnvelope(InteractionEnvelope.CurrentSchemaVersion, context, new(replyDocument.Reference), request.Context.EmissionId, outcome);
            return ProcessReferenceInterpreter.Activate(plan, state,
                Activation(id, ProcessActivationCause.Interaction, [new(new ProcessTokenInteractionTarget(state.Continuation, token.Id), reply)]), this);
        }

        ProcessActivation Activation(string id, ProcessActivationCause cause, ImmutableArray<ProcessActivationInput> inputs = default) =>
            new(new(id), cause, At, new(new("example", "local"), new("historical/1"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Origin), inputs);

        static PortableValue Value(ValueContract contract, object value) => PortableValue.Concrete(contract,
            ObservationValue.FromJsonElement(JsonSerializer.SerializeToElement(value)));

        /// <inheritdoc />
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new InvalidOperationException("Unexpected transition.");
        /// <inheritdoc />
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new InvalidOperationException("Unexpected relation.");
        /// <inheritdoc />
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Unexpected signal.");
    }
}

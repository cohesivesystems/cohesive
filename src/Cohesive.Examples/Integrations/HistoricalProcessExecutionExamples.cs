using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Configuration;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Cohesive.Model;
using Cohesive.Model.Serialization;
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
    [InlineData(0, DispatchChange.PolicyDenied)]
    [InlineData(0, DispatchChange.OfferExpired)]
    [InlineData(0, DispatchChange.DefaultChanged)]
    [InlineData(1, DispatchChange.PolicyDenied)]
    [InlineData(2, DispatchChange.OfferExpired)]
    [InlineData(3, DispatchChange.DefaultChanged)]
    public async Task HistoricalProcessResumesFromRetainedReply(int boundary, DispatchChange change)
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
        var worker = new ProcessFixture(original, DispatchChange.DefaultChanged);
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
    [InlineData(DispatchChange.PolicyDenied)]
    [InlineData(DispatchChange.OfferExpired)]
    public async Task HistoricalProcessRejectsNewAcquisitionAfterAdmissionChanges(DispatchChange change)
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

    sealed record FixtureSetup(IntegrationCompilation Compilation, int Sessions, HistoricalDestination Destination,
        long ExpectedLedgerRevision = 0);

    /// <summary>Explicit changes to current dispatch conditions in local recovery examples.</summary>
    public enum DispatchChange
    {
        /// <summary>Keep admitted conditions.</summary>
        Unchanged,
        /// <summary>Deny the current provider.</summary>
        PolicyDenied,
        /// <summary>Expire the admitted offer.</summary>
        OfferExpired,
        /// <summary>Prefer another connection for new selection.</summary>
        DefaultChanged,
        /// <summary>Remove the current selection grant.</summary>
        GrantRevoked
    }

    // One physical in-memory destination shared by both flows. Receipt objects and bytes are not crash durable.
    sealed class HistoricalDestination
    {
        internal readonly InMemoryIngestionLedger Ledger = new();
        internal readonly Dictionary<string, byte[]> Publications = new(StringComparer.Ordinal);
        internal DateOnly[] AcceptedDates => Publications.Values.SelectMany(bytes => JsonSerializer.Deserialize<DailyBar[]>(bytes)!)
            .Select(bar => DateOnly.FromDateTime(bar.Date.UtcDateTime)).Distinct().Order().ToArray();
    }

    // Local host adapter: the canonical Process decides order and branching. This fixture supplies only
    // Request handlers and reply admission. Shared retained state models storage, not physical durability.
    sealed partial class ProcessFixture : IProcessReferenceHost
    {
        readonly ProcessFixture retained;
        readonly IntegrationCompilation compilation;
        internal IntegrationCompilation Compilation => compilation;
        readonly CompiledProcessPlan plan;
        readonly IngestionDefinition definition;
        readonly DispatchChange change;
        readonly FixtureSetup setup;
        internal IngestionLedgerResult? LastAdvance;
        readonly Dictionary<RequestContractReference, Func<RequestEnvelope, ValueTask<RequestTerminalOutcome>>> handlers;
        readonly IntegrationRuntime runtime;
        readonly HistoricalCoverageScope scope;
        readonly IngestionDateRangePosition selectedPosition;
        byte[]? publicationBytes;
        ExecutionDefinitionDocument? requested, acquired, prepared;
        IngestionPublicationReceipt? receipt;
        ProgressReceipt? progress;
        internal CoverageEvidence? Coverage;
        readonly LocalAcquisition first = new(), second = new();
        internal byte[]? PublishedBytes => retained.prepared is null ? null
            : setup.Destination.Publications.GetValueOrDefault(((IngestionPreparedPublication)retained.prepared.GetDefinition<IngestionWorkItem>()).Content.Locator);
        internal IngestionPublicationReceipt? Publication => receipt;
        internal int PublishedSessions => PublishedBytes is { } bytes ? JsonSerializer.Deserialize<DailyBar[]>(bytes)!.Length : 0;
        internal int ProviderCalls => first.Calls;
        internal int ReplacementCalls => second.Calls;
        internal int Publications, Settlements;
        internal InMemoryIngestionLedger Ledger => setup.Destination.Ledger;
        internal IngestionLedgerAddress Address => compilation.Binding!.GetDefinition<IngestionProviderBinding>().Consumer;
        ExecutionProvenance Origin => plan.Document.Metadata.Provenance;

        internal ProcessFixture(int sessions)
            : this(new FixtureSetup(QualifyLocalHistorical().Compilation!, sessions, new HistoricalDestination()), null, DispatchChange.Unchanged) { }
        internal ProcessFixture(ProcessFixture original, DispatchChange change) : this(original.setup, original.retained, change) { }
        internal ProcessFixture(FixtureSetup setup) : this(setup, null, DispatchChange.Unchanged) { }
        ProcessFixture(FixtureSetup setup, ProcessFixture? original, DispatchChange change)
        {
            this.setup = setup;
            retained = original ?? this;
            this.change = change;
            compilation = Reopen(setup.Compilation);
            Assert.True(InteractionContractCatalog.TryCreate(compilation.Documents.Where(d => d.Kind == InteractionContractDocuments.Kind), out var contracts).IsValid);
            var compiled = ProcessStaticCompiler.Compile(compilation.Process!, new(interactionContracts: contracts));
            Assert.True(compiled.Validation.IsValid, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
            plan = compiled.Plan!;
            var binding = compilation.Binding!.GetDefinition<IngestionProviderBinding>();
            var property = compilation.Documents.Single(d => d.Reference == binding.Contract.Properties[0].Property)
                .GetDefinition<ContractPropertyDefinition>();
            scope = compilation.Documents.Single(d => d.Reference == property.Parameters).GetDefinition<HistoricalCoverageScope>();
            selectedPosition = new(scope.StartDate, scope.EndDateInclusive.AddDays(1));
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

        internal DocumentValidationResult? AcquisitionValidation;
        internal DocumentValidationResult? PublicationValidation;

        async ValueTask<RequestTerminalOutcome> Acquire(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, scope), request.Payload);
            var current = Layer.Define("example/local-policy", "v2", "research-admin", new())
                .Prefer(IntegrationChoice.Select(Profile, Connection(change == DispatchChange.DefaultChanged ? "v2" : "v1")));
            if (change == DispatchChange.PolicyDenied) current = current.Enforce(new(AllowedProviders: []));
            LocalAcquisition provider;
            try
            {
                provider = runtime.ResolveForDispatch<LocalAcquisition>(compilation, [current.Document],
                    [new SelectionGrant("research-admin", new())], change == DispatchChange.OfferExpired ? At.AddDays(1) : At);
            }
            catch (InvalidOperationException)
            {
                return Outcome(request, new IntegrationFailure("dispatch-denied"), success: false);
            }
            var transformation = ContractPropertyDocuments.DeclareContext(new("example/process-normalizer"), new("v1"), scope.Normalizer, Origin);
            retained.requested = IngestionWorkDocuments.Create(new IngestionAcquisitionRequest(Address,
                compilation.Binding!.GetDefinition<IngestionProviderBinding>().Contract.Ingestion, transformation.Reference,
                request.Context.EmissionId.Value, "attempt/1", IngestionContentReference.Describe("selection.json", JsonSerializer.SerializeToUtf8Bytes(scope), "application/json"), setup.ExpectedLedgerRevision, At), Origin);
            retained.AcquisitionValidation = IngestionLedgerReduction.ValidateSelection(
                await Ledger.ReadAsync(Address, default), retained.requested, selectedPosition);
            if (!retained.AcquisitionValidation.IsValid)
                return Outcome(request, new IntegrationFailure("acquisition-progress-rejected"), success: false);
            provider.Acquire();
            var bytes = Response(scope.ExpectedSessions.Take(setup.Sessions));
            var content = IngestionContentReference.Describe("response.json", bytes, "application/json");
            retained.Coverage = TiingoCoverageEvidenceExamples.Evaluate(scope, content, bytes);
            if (!retained.Coverage.IsComplete)
                return Outcome(request, new IntegrationFailure("missing-sessions"), success: false);
            retained.acquired = IngestionWorkDocuments.Create(new IngestionAcquisitionReceipt(retained.requested.Reference, content, At.AddSeconds(1)), Origin);
            retained.publicationBytes = Prepare(retained.Coverage);
            retained.prepared = IngestionWorkDocuments.Create(new IngestionPreparedPublication(retained.acquired.Reference, transformation.Reference,
                IngestionContentReference.Describe("publication/" + request.Context.EmissionId.Value + ".json", retained.publicationBytes, "application/json"),
                selectedPosition, At.AddSeconds(2)), Origin);
            return Outcome(request, new PreparedPublication(retained.prepared.Reference.DefinitionId.Value));
        }

        async ValueTask<RequestTerminalOutcome> Publish(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, new PreparedPublication(retained.prepared!.Reference.DefinitionId.Value)), request.Payload);
            Assert.True(IngestionWorkDocuments.ValidateChain(retained.requested!, retained.acquired!, retained.prepared!).IsValid);
            var prepared = Assert.IsType<IngestionPreparedPublication>(retained.prepared!.GetDefinition<IngestionWorkItem>());
            Assert.True(prepared.Content.Matches(retained.publicationBytes!));
            retained.PublicationValidation = IngestionLedgerReduction.ValidatePublication(
                await Ledger.ReadAsync(Address, default), retained.requested, retained.acquired, retained.prepared);
            if (!retained.PublicationValidation.IsValid)
                return Outcome(request, new IntegrationFailure("publication-progress-rejected"), success: false);
            retained.setup.Destination.Publications.Add(prepared.Content.Locator, retained.publicationBytes!);
            retained.Publications++;
            var acquisition = (IngestionAcquisitionRequest)retained.requested!.GetDefinition<IngestionWorkItem>();
            retained.receipt = new(acquisition.OperationId, prepared.Content.Sha256, "local-sink/receipt/" + acquisition.OperationId);
            return Outcome(request, new PublicationReceipt(retained.receipt.ReceiptReference));
        }

        async ValueTask<RequestTerminalOutcome> Advance(RequestEnvelope request)
        {
            Assert.Equal(Value(Request(request).Payload.Contract, new PublicationReceipt(retained.receipt!.ReceiptReference)), request.Payload);
            var advance = IngestionWorkDocuments.CreateLedgerAdvance(retained.requested!, retained.acquired!, retained.prepared!, retained.receipt!, Origin);
            var result = await retained.Ledger.AdvanceAsync(advance, default);
            retained.LastAdvance = result;
            if (result.Disposition == IngestionLedgerDisposition.Conflict)
                return Outcome(request, new IntegrationFailure(result.DiagnosticCode!), success: false);
            if (result.Disposition is not (IngestionLedgerDisposition.Advanced or IngestionLedgerDisposition.Replayed))
                throw new InvalidOperationException("Unknown ledger outcome requires exact reconciliation.");
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

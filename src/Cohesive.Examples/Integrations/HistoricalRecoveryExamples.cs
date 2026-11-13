using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>Reconciles a lost publication acknowledgement while paused without repeating the write.</summary>
    /// <returns>Completes after retained sink evidence advances the original Process and ends draining.</returns>
    [Fact]
    public async Task LostPublicationAcknowledgementReconcilesWhilePaused()
    {
        var original = new ProcessFixture(2);
        var control = new InMemoryIntegrationLifecycle(original.Address, (_, actor) => actor == Operator);
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admission = control.Admit(new(control.Bind(original.Compilation), Session, 1, Operator, At)).Receipt;
        var decision = original.Start();
        var acquire = Request(decision);
        decision = original.Reply(decision, await original.DispatchOnce(acquire));
        var publish = Request(decision);
        await Assert.ThrowsAsync<IOException>(async () => await original.DispatchOnce(publish, DispatchLoss.AfterEffect));
        Assert.Equal(1, original.Publications);
        Assert.Null(await original.Ledger.ReadAsync(original.Address, default));
        control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At));
        var worker = new ProcessFixture(original, DispatchChange.PolicyDenied);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.DispatchOnce(publish));
        Assert.True(control.Inspect().Draining);
        Assert.Equal(IntegrationCompletionOutcome.Unresolved, control.Complete(new(admission, decision, Operator)).Outcome);
        var intruder = new ProcessControlAuthorizationContext("intruder", Operator.AuthorityScope, "denied");
        Assert.Throws<UnauthorizedAccessException>(() => worker.ReconcilePublication(publish, intruder));
        Assert.Null(original.ReconciliationRecorder);
        var changed = new RequestEnvelope(publish.SchemaVersion, publish.Context, publish.Contract, publish.Payload,
            new ProcessTokenInteractionTarget(Session, new("different-recipient")));
        Assert.Null(worker.ReconcilePublication(changed, Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.DispatchOnce(changed));
        var outcome = worker.ReconcilePublication(publish, Operator);
        Assert.NotNull(outcome);
        Assert.Equal(Operator, original.ReconciliationRecorder);
        Assert.Same(outcome, worker.ReconcilePublication(publish, Operator));
        Assert.Throws<UnauthorizedAccessException>(() => worker.ReconcilePublication(publish, intruder));
        Assert.Same(outcome, await worker.DispatchOnce(publish));
        decision = worker.Reply(decision, outcome);
        while (decision.Disposition == ProcessActivationDisposition.DurableCut)
            decision = worker.Reply(decision, await worker.DispatchOnce(Request(decision)));
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.True(control.Complete(new(admission, decision, Operator)).IsAccepted);
        Assert.False(control.Inspect().Draining);
        Assert.False(control.Inspect().Accepting);
        Assert.Equal(1, original.ProviderCalls);
        Assert.Equal(0, original.ReplacementCalls);
        Assert.Equal(1, original.Publications);
        Assert.Equal(1, original.Settlements);
        Assert.Equal(1, (await original.Ledger.ReadAsync(original.Address, default))!.Revision);
    }

    /// <summary>Neither missing publication evidence nor altered destination content permits resolution.</summary>
    /// <param name="loss">Boundary at which the simulated worker loses its acknowledgement.</param>
    /// <returns>Completes with the original session still draining and no ledger advancement.</returns>
    [Theory]
    [InlineData(DispatchLoss.BeforeEffect)]
    [InlineData(DispatchLoss.AfterEffect)]
    public async Task PublicationWithoutMatchingEvidenceRemainsPending(DispatchLoss loss)
    {
        var original = new ProcessFixture(2);
        var control = new InMemoryIntegrationLifecycle(original.Address, (_, actor) => actor == Operator);
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admission = control.Admit(new(control.Bind(original.Compilation), Session, 1, Operator, At)).Receipt;
        var decision = original.Start();
        decision = original.Reply(decision, await original.DispatchOnce(Request(decision)));
        var publish = Request(decision);
        await Assert.ThrowsAsync<IOException>(async () => await original.DispatchOnce(publish, loss));
        if (loss == DispatchLoss.AfterEffect) original.PublishedBytes![0] ^= 1;
        control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At));
        var worker = new ProcessFixture(original, DispatchChange.PolicyDenied);
        Assert.Null(worker.ReconcilePublication(publish, Operator));
        Assert.Null(original.ReconciliationRecorder);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.DispatchOnce(publish));
        Assert.Equal(IntegrationCompletionOutcome.Unresolved, control.Complete(new(admission, decision, Operator)).Outcome);
        Assert.True(control.Inspect().Draining);
        Assert.Null(await original.Ledger.ReadAsync(original.Address, default));
        Assert.Equal(0, original.Settlements);
        Assert.Equal(1, original.ProviderCalls);
        Assert.Equal(loss == DispatchLoss.AfterEffect ? 1 : 0, original.Publications);
    }

    /// <summary>A claimed acquisition with no retained reply cannot be retried or resolved using sink evidence.</summary>
    /// <returns>Completes without another provider call or any destination progress.</returns>
    [Fact]
    public async Task UnknownAcquisitionDoesNotRefetch()
    {
        var original = new ProcessFixture(2);
        var request = Request(original.Start());
        await Assert.ThrowsAsync<IOException>(async () => await original.DispatchOnce(request, DispatchLoss.AfterEffect));
        var worker = new ProcessFixture(original, DispatchChange.DefaultChanged);
        Assert.Null(worker.ReconcilePublication(request, Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.DispatchOnce(request));
        Assert.Equal(1, original.ProviderCalls);
        Assert.Equal(0, original.ReplacementCalls);
        Assert.Equal(0, original.Publications);
        Assert.Null(await original.Ledger.ReadAsync(original.Address, default));
    }

    static RequestEnvelope Request(ProcessActivationDecision decision) =>
        Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));

    /// <summary>Local fault injection boundaries; these do not claim physical crash durability.</summary>
    public enum DispatchLoss
    {
        /// <summary>Retain the outcome normally.</summary>
        None,
        /// <summary>Lose the worker after claiming, before the handler is called.</summary>
        BeforeEffect,
        /// <summary>Lose the acknowledgement after the handler returns, before retaining its outcome.</summary>
        AfterEffect
    }

    // Deliberately test-local and single-threaded: a real host needs atomic durable claims, authorized
    // evidence retrieval and fenced writes. This is an executable recovery policy, not a second runtime.
    sealed partial class ProcessFixture
    {
        readonly Dictionary<string, (InteractionEnvelopeContentFingerprint Fingerprint, RequestTerminalOutcome? Outcome)> dispatches = new();
        InteractionEnvelopeContentFingerprint? publicationRequest;
        internal ProcessControlAuthorizationContext? ReconciliationRecorder;

        internal async ValueTask<RequestTerminalOutcome> DispatchOnce(RequestEnvelope request, DispatchLoss loss = DispatchLoss.None)
        {
            var id = request.Context.EmissionId.Value;
            var fingerprint = InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(request);
            if (retained.dispatches.TryGetValue(id, out var prior))
            {
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Dispatch identity conflicts with retained Request.");
                return prior.Outcome ?? throw new InvalidOperationException("Claimed dispatch requires evidence-only reconciliation.");
            }
            retained.dispatches.Add(id, (fingerprint, null));
            if (loss == DispatchLoss.BeforeEffect) throw new IOException("Simulated worker loss before effect.");
            var outcome = await Handle(request);
            if (request.Contract == definition.Publish) retained.publicationRequest = fingerprint;
            if (loss == DispatchLoss.AfterEffect) throw new IOException("Simulated lost acknowledgement.");
            retained.dispatches[id] = (fingerprint, outcome);
            return outcome;
        }

        internal RequestTerminalOutcome? ReconcilePublication(RequestEnvelope request, ProcessControlAuthorizationContext actor)
        {
            // Authentication is a fixture assumption; production hosts must not trust caller-supplied actor text.
            if (actor != Operator) throw new UnauthorizedAccessException();
            var id = request.Context.EmissionId.Value;
            var fingerprint = InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(request);
            if (request.Contract != definition.Publish || !retained.dispatches.TryGetValue(id, out var prior)
                || prior.Fingerprint != fingerprint) return null;
            if (prior.Outcome is not null) return prior.Outcome;
            if (retained.publicationRequest != fingerprint || retained.receipt is null || retained.prepared is null)
                return null;
            var prepared = (IngestionPreparedPublication)retained.prepared.GetDefinition<IngestionWorkItem>();
            if (!retained.setup.Destination.Publications.TryGetValue(prepared.Content.Locator, out var bytes) || !prepared.Content.Matches(bytes)
                || retained.receipt.ContentFingerprint != prepared.Content.Sha256) return null;
            var outcome = Outcome(request, new PublicationReceipt(retained.receipt.ReceiptReference));
            retained.dispatches[id] = (fingerprint, outcome);
            retained.ReconciliationRecorder = actor;
            return outcome;
        }
    }
}

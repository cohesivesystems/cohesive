using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.Api;

public sealed class ServiceMutationTests
{
    static readonly ExecutionProvenance Provenance = new(new("tests"), new("tests/service-mutation"), DocumentOrigin.Generated);
    static readonly HostedQuery<string, Facts> Hydration = HostedQuery<string, Facts>.Create(new("facts"), new("1"),
        new("test.facts", "1"), "v1", Provenance, evaluationSemantics: HostedQueryEvaluationSemantics.Observation);
    static readonly HostedQuery<string, string> Enrichment = HostedQuery<string, string>.Create(new("details"), new("1"),
        new("test.details", "1"), "v1", Provenance, evaluationSemantics: HostedQueryEvaluationSemantics.Observation);
    static Transition<Proposal, Facts, string> Approval => TransitionAuthoring.Create<Proposal, Facts, string>(
        Proposal.Instance.Definition.Shape, new(new("approve"), new("1"), new("body"), Provenance),
        transition => transition.Set(new("set"), entity => entity.Status, "approved")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "approved"));

    [Theory]
    [InlineData(false, false, ProcessActivationDisposition.Completed, 1, false)]
    [InlineData(true, false, ProcessActivationDisposition.Failed, 0, false)]
    [InlineData(false, true, ProcessActivationDisposition.Failed, 1, false)]
    [InlineData(false, false, ProcessActivationDisposition.Failed, 1, true)]
    public async Task HydrateApplyEnrichUsesCanonicalSequencingAndDoesNotUndoWrites(bool failHydration, bool failEnrichment,
        ProcessActivationDisposition expected, int expectedWrites, bool throwEnrichment)
    {
        var transition = Approval;
        var process = ServiceMutation.HydrateWith(Hydration)
            .Apply(transition, facts => facts.Id)
            .EnrichWith(Enrichment)
            .Build(new(new("review"), new("1"), ProcessRecoveryPolicy.ContinueAttempt, Provenance));
        Assert.True(process.IsValid, string.Join("; ", process.Validation.Diagnostics));
        Assert.Equal(Hydration.InputContract, process.Definition.Input);
        Assert.Equal(Enrichment.ResultContract, process.Definition.Result);
        var compiled = process.Compile(new(definitions: [Hydration.CreateProcessDefinitionLink(),
            new(transition.Reference, ProcessDefinitionLinkKind.Transition, transition.Definition.Input, transition.Definition.Outcome),
            Enrichment.CreateProcessDefinitionLink()]));
        Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics));
        var host = new Host(failHydration, failEnrichment, throwEnrichment);
        var pending = new EphemeralProcessExecutor(compiled.Plan!).ExecuteAsync(OperationContext.Create(),
            new(new("review/1"), new("attempt/1")), PortableValue.Concrete(process.Definition.Input, ObservationValue.FromString("proposal/1")),
            new(new("tests", "tenant-a"), new("review/1"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), Provenance),
            host, TimeSpan.FromSeconds(5)).AsTask();
        if (throwEnrichment)
        {
            var failure = await Assert.ThrowsAsync<EphemeralProcessExecutionException>(() => pending);
            Assert.IsType<IOException>(failure.InnerException);
            Assert.Equal(new ExecutionNodeId("step/2"), failure.Evidence.InterruptedOperation);
            Assert.Single(failure.Evidence.CompletedOperations.Values, result => result.ReceiptReference is not null);
            Assert.Equal(1, host.Writes);
            Assert.Equal(new[] { "hydrate", "apply", "enrich" }, host.Calls);
            return;
        }
        var execution = await pending;
        var decision = execution.Decision;
        Assert.Equal(expected, decision.Disposition);
        Assert.Equal(expectedWrites, execution.Evidence.CompletedOperations.Values.Count(result => result.ReceiptReference is not null));
        Assert.Equal(expectedWrites, host.Writes);
        if (!failHydration && !failEnrichment)
            Assert.Equal(ObservationValue.FromString("details:approved"), decision.State.Terminal.Detail!.Value!.Value);
        Assert.Equal(failHydration ? new[] { "hydrate" } : new[] { "hydrate", "apply", "enrich" }, host.Calls);
    }

    [Fact]
    public void DirectMutationInfersTransitionOutcomeAndRequiresExplicitProcessForAnotherWrite()
    {
        var transition = Approval;
        var mutation = ServiceMutation.Apply(transition, facts => facts.Id);
        var process = mutation.Build(new(new("direct"), new("1"), ProcessRecoveryPolicy.ContinueAttempt, Provenance));
        Assert.True(process.IsValid, string.Join("; ", process.Validation.Diagnostics));
        Assert.Equal(transition.Definition.Outcome, process.Definition.Result);
        Assert.IsType<InvokeTransitionProcessNode>(process.Definition.Nodes[0]);
        Assert.IsType<ReturnProcessNode>(process.Definition.Nodes[1]);
        var direct = ProcessDefinitionDocuments.Create(new("direct"), new("1"),
            new(transition.Definition.Input, transition.Definition.Outcome, new("step/0"),
            [
                new InvokeTransitionProcessNode(new("step/0"), transition.Reference,
                    Expr.Field(ProcessBindingIds.Input, nameof(Facts.Id)), Expr.BoundValue(ProcessBindingIds.Input),
                    new(new(new("edge/0"), new("step/1")), new(new("value/0"), transition.Definition.Outcome))),
                new ReturnProcessNode(new("step/1"), Expr.BoundValue(new("value/0")))
            ], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(direct),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(process.Document));
        // Return to Facts through an enrichment query so a second Apply is type-correct but semantically disallowed.
        Assert.Throws<InvalidOperationException>(() => mutation.EnrichWith(Hydration).Apply(transition, facts => facts.Id));
        Assert.Throws<InvalidOperationException>(() => ServiceMutation.HydrateWith(Hydration).Build(
            new(new("missing-mutation"), new("1"), ProcessRecoveryPolicy.ContinueAttempt, Provenance)));
    }

    public sealed record Facts(string Id);
    sealed class Proposal : Entity<Proposal>
    {
        public Proposal() => Status = MutableField<string>(nameof(Status));
        public Field<string> Status { get; }
    }

    sealed class Host(bool failHydration, bool failEnrichment, bool throwEnrichment) : IAsyncProcessReferenceHost
    {
        public List<string> Calls { get; } = [];
        public int Writes { get; private set; }
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation)
        {
            var hydrate = evaluation.Definition == Hydration.Reference;
            Calls.Add(hydrate ? "hydrate" : "enrich");
            if (!hydrate && throwEnrichment) throw new IOException("private backend error");
            if (hydrate ? failHydration : failEnrichment)
                return ValueTask.FromResult(ProcessOperationResult.Failed(new("test.query.failed", DiagnosticSeverity.Error, "Query failed.")));
            if (hydrate)
                return ValueTask.FromResult(ProcessOperationResult.Completed(PortableValue.Concrete(Hydration.ResultContract,
                    ObservationValue.FromObject(new Facts("proposal/1")))));
            Assert.Equal(ObservationValue.FromString("approved"), evaluation.Input.Value);
            return ValueTask.FromResult(ProcessOperationResult.Completed(PortableValue.Concrete(Enrichment.ResultContract,
                ObservationValue.FromString("details:approved"))));
        }
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            Calls.Add("apply");
            Assert.Equal(ObservationValue.FromString("proposal/1"), invocation.Subject.Value);
            Writes++;
            return ValueTask.FromResult(ProcessOperationResult.Completed(PortableValue.Concrete(Enrichment.InputContract, ObservationValue.FromString("approved")))
                .WithReceiptReference(PortableValue.Concrete(Enrichment.InputContract, ObservationValue.FromString("receipt/1"))));
        }
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}

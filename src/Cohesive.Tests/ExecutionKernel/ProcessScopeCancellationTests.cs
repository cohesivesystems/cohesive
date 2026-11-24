using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessScopeCancellationTests
{
    [Theory]
    [InlineData(ExecutionTerminalOutcomeKind.Completed)]
    [InlineData(ExecutionTerminalOutcomeKind.Cancelled)]
    [InlineData(ExecutionTerminalOutcomeKind.Failed)]
    [InlineData(ExecutionTerminalOutcomeKind.Terminated)]
    public void TwoChildren_PartialSettlementAndRestartPreserveExactEvidence(ExecutionTerminalOutcomeKind firstOutcome)
    {
        var runner = new ScopeRun();
        var active = runner.StartChildren();
        var cancelled = runner.Cancel(active);
        var intents = ProcessChildCancellationIntents.Project(cancelled.State);
        Assert.Equal(2, intents.Length);
        Assert.Empty(cancelled.Emissions);
        Assert.Equal(ProcessCancellationFinalizationPhase.WaitingForPropagatedChildren,
            cancelled.State.CancellationFinalization?.Phase);
        Assert.All(cancelled.State.Waits, wait => Assert.False(wait.Active));

        var restored = runner.Restore(cancelled.State);
        Assert.Equal<ProcessChildCancellationIntent>(intents, ProcessChildCancellationIntents.Project(restored));
        var repeated = runner.Activate(restored, cancellation: runner.Intent);
        Assert.Empty(repeated.Emissions);
        var first = runner.Closure(intents[0], firstOutcome);
        var partial = runner.Activate(repeated.State, closures: [first]);
        Assert.Empty(partial.Emissions);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, partial.State.Terminal.Kind);
        Assert.Equal(first, partial.State.Children.Single(child => child.Continuation == first.ChildContinuation).CancellationClosure);
        var duplicate = runner.Activate(runner.Restore(partial.State), closures: [first]);
        Assert.Empty(duplicate.Emissions);
        Assert.Single(ProcessChildCancellationIntents.Project(duplicate.State));

        var second = runner.Closure(intents[1], ExecutionTerminalOutcomeKind.Cancelled);
        var finalizing = runner.Activate(runner.Restore(duplicate.State), closures: [second]);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(finalizing.Emissions));
        Assert.Equal(runner.Fixture.Protocol.Process.Reference, request.ChildTarget?.Definition);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, finalizing.State.Terminal.Kind);
        Assert.Equal(ProcessCancellationFinalizationPhase.FinalizerActive, finalizing.State.CancellationFinalization?.Phase);
        Assert.Equal(2, finalizing.State.Children.Count(child => child.Disposition == ProcessChildDisposition.CancellationSettled));
        var replayed = runner.Activate(runner.Restore(finalizing.State), cancellation: runner.Intent, closures: [first, second]);
        Assert.Empty(replayed.Emissions);
        Assert.Equal(request.ChildTarget?.Continuation,
            replayed.State.Children.Single(child => child.Node.Value == "cancel/finalize").Continuation);
        var acknowledged = runner.Activate(replayed.State, inputs: [runner.FinalizerReply(request)]);
        Assert.Equal(ProcessActivationDisposition.Cancelled, acknowledged.Disposition);
        Assert.Equal(ProcessCancellationFinalizationPhase.Acknowledged, acknowledged.State.CancellationFinalization?.Phase);
        var terminal = runner.Restore(acknowledged.State);
        Assert.Equal(first, terminal.Children.Single(child => child.Continuation == first.ChildContinuation).CancellationClosure);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, terminal.Terminal.Kind);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("attempt")]
    [InlineData("stale")]
    [InlineData("future")]
    public void TwoChildren_InvalidClosureBatchDoesNotPartiallyCommit(string mismatch)
    {
        var runner = new ScopeRun();
        var cancelled = runner.Cancel(runner.StartChildren());
        var intents = ProcessChildCancellationIntents.Project(cancelled.State);
        var valid = runner.Closure(intents[0], ExecutionTerminalOutcomeKind.Completed);
        var invalid = new ProcessChildCancellationClosure(
            mismatch == "intent" ? "unknown-intent" : intents[1].IntentId,
            mismatch == "attempt"
                ? new(intents[1].ChildContinuation.ProcessInstanceId, new("wrong-attempt"))
                : intents[1].ChildContinuation,
            ExecutionTerminalOutcomeKind.Cancelled,
            mismatch switch
            {
                "stale" => runner.Fixture.Start.AcceptedAtUtc,
                "future" => runner.Fixture.Start.AcceptedAtUtc.AddDays(1),
                _ => valid.ObservedAtUtc
            });
        var rejected = runner.Activate(cancelled.State, closures: [valid, invalid]);
        Assert.Equal(ProcessActivationDisposition.Rejected, rejected.Disposition);
        Assert.Same(cancelled.State, rejected.State);
        Assert.Empty(rejected.Emissions);
        Assert.Equal(2, ProcessChildCancellationIntents.Project(rejected.State).Length);
    }

    [Fact]
    public void TwoChildren_ConflictingCancellationCannotReplaceRetainedOccurrence()
    {
        var runner = new ScopeRun();
        var cancelled = runner.Cancel(runner.StartChildren());
        var rejected = runner.Activate(runner.Restore(cancelled.State), cancellation:
            new(runner.Intent.AttemptId, new("other-reason"), new("other-command")));
        Assert.Equal(ProcessActivationDisposition.Rejected, rejected.Disposition);
        Assert.Equal(runner.Intent, rejected.State.CancellationFinalization?.Intent);
        Assert.Empty(rejected.Emissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoChildren_AbortedForkStillRequiresCancellationEvidenceAndClosedNormalWork(bool reopenCoordinator)
    {
        var runner = new ScopeRun();
        var state = runner.Cancel(runner.StartChildren()).State;
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var serialized = JsonSerializer.SerializeToNode(state, options)!;
        if (reopenCoordinator)
        {
            var owner = Assert.Single(state.Forks).Owner;
            var index = state.Tokens.IndexOf(state.Tokens.Single(token => token.Id == owner));
            serialized["tokens"]![index]!["disposition"] = JsonSerializer.SerializeToNode(ExecutionTokenDisposition.Waiting, options);
        }
        else
        {
            serialized["cancellationFinalization"] = null;
        }
        var invalid = Assert.IsType<ProcessContinuationState>(serialized.Deserialize<ProcessContinuationState>(options));
        var validation = ProcessContinuationValidator.Validate(runner.Fixture.Plan, invalid);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code ==
            (reopenCoordinator ? ProcessContinuationDiagnosticCodes.CancellationFinalizationInvalid
                : ProcessContinuationDiagnosticCodes.ForkStateMismatch));
    }

    [Fact]
    public void TwoChildren_ConflictingRetainedClosureCannotOverwriteCommittedOutcome()
    {
        var runner = new ScopeRun();
        var cancelled = runner.Cancel(runner.StartChildren());
        var intent = ProcessChildCancellationIntents.Project(cancelled.State)[0];
        var closure = runner.Closure(intent, ExecutionTerminalOutcomeKind.Completed);
        var partial = runner.Activate(cancelled.State, closures: [closure]);
        var rejected = runner.Activate(runner.Restore(partial.State), closures:
            [new(closure.IntentId, closure.ChildContinuation, ExecutionTerminalOutcomeKind.Cancelled, closure.ObservedAtUtc)]);
        Assert.Equal(ProcessActivationDisposition.Rejected, rejected.Disposition);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.InputIdentityConflict);
        Assert.Equal(closure, rejected.State.Children.Single(child => child.Continuation == closure.ChildContinuation).CancellationClosure);
    }

    [Fact]
    public void TwoChildren_CompletedChildRetainsResultWhenSiblingIsCancelled()
    {
        var runner = new ScopeRun();
        var active = runner.StartChildren();
        var first = runner.Requests[0];
        var completed = runner.Activate(active, inputs: [runner.ChildReply(first)]);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, completed.State.Terminal.Kind);
        var cancelled = runner.Cancel(completed.State);
        Assert.Single(ProcessChildCancellationIntents.Project(cancelled.State));
        var retained = runner.Restore(cancelled.State).Children.Single(child => child.RequestEmission == first.Context.EmissionId);
        Assert.Equal(ProcessChildDisposition.Completed, retained.Disposition);
        Assert.Equal(ProcessDurabilityTestFixture.StringValue("committed-child-result"), retained.Result);
    }

    [Fact]
    public void TwoChildren_CancellationWinsSameActivationReplyWithoutResumingNormalWork()
    {
        var runner = new ScopeRun();
        var active = runner.StartChildren();
        var reply = runner.ChildReply(runner.Requests[0]);
        var cancelled = runner.Activate(active, cancellation: runner.Intent, inputs: [reply]);
        Assert.Empty(cancelled.Emissions);
        Assert.Equal(2, ProcessChildCancellationIntents.Project(cancelled.State).Length);
        var replayed = runner.Activate(runner.Restore(cancelled.State), inputs: [reply]);
        Assert.Empty(replayed.Emissions);
        Assert.Equal(ProcessCancellationFinalizationPhase.WaitingForPropagatedChildren,
            replayed.State.CancellationFinalization?.Phase);
        Assert.All(replayed.State.Tokens, token => Assert.Equal(ExecutionTokenDisposition.Cancelled, token.Disposition));
        Assert.NotEmpty(replayed.State.InputReceipts);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, replayed.State.Terminal.Kind);
    }

    [Fact]
    public void TwoChildren_AuthoringProducesTheSameExactDefinition()
    {
        var first = CancellationFinalizerDurabilityTestFixture.CreateTwoChildScope().Scope;
        var second = CancellationFinalizerDurabilityTestFixture.CreateTwoChildScope().Scope;
        Assert.Equal(first.Plan.DefinitionReference, second.Plan.DefinitionReference);
    }

    internal sealed class ScopeRun
    {
        internal CancellationFinalizerDurabilityTestFixture Fixture { get; }
        internal ProcessInvocationProtocol<string, string> Child { get; }
        internal List<RequestEnvelope> Requests { get; } = [];

        internal ScopeRun() => (Fixture, Child) = CancellationFinalizerDurabilityTestFixture.CreateTwoChildScope();
        internal ProcessCancellationIntent Intent => new(
            Fixture.Start.Request.InitialContinuation.ProcessAttemptId, new("operator.cancel"), new("cancel/scope"));
        int sequence;

        internal ProcessContinuationState StartChildren()
        {
            var state = ProcessReferenceInterpreter.Create(Fixture.Plan,
                Fixture.Start.Request.InitialContinuation, Assert.IsType<PortableValue>(Fixture.Start.Request.Input));
            for (var i = 0; i < 4 && state.Children.Length < 2; i++)
            {
                var decision = Activate(state);
                Requests.AddRange(decision.Emissions.OfType<RequestEnvelope>());
                state = decision.State;
            }
            Assert.Equal(2, state.Children.Length);
            Assert.All(state.Children, child => Assert.Equal(ProcessChildDisposition.Active, child.Disposition));
            return state;
        }

        internal ProcessActivationDecision Cancel(ProcessContinuationState state) => Activate(state, cancellation: Intent);

        internal ProcessActivationDecision Activate(ProcessContinuationState state,
            ProcessCancellationIntent? cancellation = null,
            ImmutableArray<ProcessChildCancellationClosure> closures = default,
            ImmutableArray<ProcessActivationInput> inputs = default) =>
            ProcessReferenceInterpreter.Activate(Fixture.Plan, state,
                new(new($"activation/scope/{++sequence}"), ProcessActivationCause.Control,
                    Fixture.Start.AcceptedAtUtc.AddSeconds(sequence), Fixture.ActivationContext,
                    inputs.IsDefault ? [] : inputs, cancellation: cancellation,
                    admissionOperatingPoints: [], childCancellationClosures: closures.IsDefault ? [] : closures), RejectingHost.Instance);

        internal ProcessChildCancellationClosure Closure(ProcessChildCancellationIntent intent, ExecutionTerminalOutcomeKind outcome) =>
            new(intent.IntentId, intent.ChildContinuation, outcome, Fixture.Start.AcceptedAtUtc.AddSeconds(sequence));

        internal ProcessContinuationState Restore(ProcessContinuationState state)
        {
            var options = InteractionEnvelopeJsonSerializer.CreateOptions();
            var json = JsonSerializer.Serialize(state, options);
            var restored = Assert.IsType<ProcessContinuationState>(JsonSerializer.Deserialize<ProcessContinuationState>(json, options));
            Assert.Equal(json, JsonSerializer.Serialize(restored, options));
            var validation = ProcessContinuationValidator.Validate(Fixture.Plan, restored);
            Assert.True(validation.IsValid, string.Join("; ", validation.Diagnostics.Select(item => item.Message)));
            return restored;
        }

        internal ProcessActivationInput FinalizerReply(RequestEnvelope request) => Reply(request,
            Fixture.Protocol.CompletedReply, Fixture.Acknowledgement(Fixture.Start.Request.InitialContinuation.ProcessAttemptId));

        internal ProcessActivationInput ChildReply(RequestEnvelope request) => Reply(request,
            Child.CompletedReply, ProcessDurabilityTestFixture.StringValue("committed-child-result"));

        ProcessActivationInput Reply(RequestEnvelope request, ReplyContractReference contract, PortableValue result)
        {
            var target = Assert.IsType<ProcessChildRequestTarget>(request.ChildTarget);
            var reply = new ReplyEnvelope(InteractionEnvelope.CurrentSchemaVersion,
                new(new($"reply/{request.Context.EmissionId.Value}"),
                    new ProcessInteractionOrigin(target.Definition, new("return"), target.Continuation,
                        new("activation/finalizer"), new("token/finalizer")),
                    request.Context.CorrelationId, request.Context.EmissionId, request.Context.AuthorityScope,
                    new($"idempotency/{request.Context.EmissionId.Value}"), ordering: null, request.Context.Delivery,
                    ProcessControlTestFixture.Provenance()),
                contract, request.Context.EmissionId,
                new RequestResultOutcome(target.OutcomeMapping.Completed, result));
            return new(Assert.IsType<ProcessTokenInteractionTarget>(request.ResponseTarget), reply);
        }
    }

    internal sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new InvalidOperationException("Unexpected transition.");
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new InvalidOperationException("Unexpected relation.");
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Unexpected signal.");
    }
}

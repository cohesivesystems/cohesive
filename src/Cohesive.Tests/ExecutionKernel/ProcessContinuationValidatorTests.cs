using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessContinuationValidatorTests
{
    static readonly DateTimeOffset ObservedAtUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void CanonicalInterpreterStates_AreAdmittedWithoutDiagnostics()
    {
        var returnPlan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.Const("done"))]));
        var initial = ProcessReferenceInterpreter.Create(returnPlan, Continuation(), StringValue("input"));
        var completed = ProcessReferenceInterpreter.Activate(
            returnPlan,
            initial,
            Activation("activation/return"),
            RejectingHost.Instance).State;
        var cut = CreateCutState();
        var fork = CreateForkState();
        var request = CreateRequestState();

        Assert.True(ProcessContinuationValidator.Validate(returnPlan, initial).IsValid);
        Assert.True(ProcessContinuationValidator.Validate(returnPlan, completed).IsValid);
        Assert.True(ProcessContinuationValidator.Validate(cut.Plan, cut.State).IsValid);
        Assert.True(ProcessContinuationValidator.Validate(fork.Plan, fork.State).IsValid);
        Assert.True(ProcessContinuationValidator.Validate(request.Plan, request.State).IsValid);
    }

    [Fact]
    public void DefinitionMismatch_IsStructuredAndDoesNotThrow()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.Const("done"))]));
        var state = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var incompatible = CopyState(
            state,
            definition: new(
                plan.DefinitionReference.DefinitionId,
                plan.DefinitionReference.RevisionId,
                new(
                    ExecutionDefinitionFingerprinter.Algorithm,
                    ExecutionDefinitionFingerprinter.Canonicalization,
                    new string('f', 64))));

        var validation = ProcessContinuationValidator.Validate(plan, incompatible);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ProcessContinuationDiagnosticCodes.DefinitionMismatch, diagnostic.Code);
        Assert.Equal("/definition", diagnostic.Location);
        Assert.Equal("processContinuationRestore", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void DuplicateAndUnsortedDurableIdentityCollections_AreAllDiagnosed()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.Const("done"))]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var sourceToken = Assert.Single(initial.Tokens);
        var tokenZ = CopyToken(sourceToken, id: new("token/z"));
        var tokenA = CopyToken(sourceToken, id: new("token/a"));
        var waitZ = NewWait(
            new("wait/z"),
            tokenZ.Id,
            sourceToken.Node,
            occurrence: 0,
            ProcessWaitKind.DurableCut,
            ObservedAtUtc,
            [],
            active: false,
            winnerClause: null,
            winnerInput: null,
            obligationEmission: null);
        var waitA = NewWait(
            new("wait/a"),
            tokenA.Id,
            sourceToken.Node,
            occurrence: 0,
            ProcessWaitKind.DurableCut,
            ObservedAtUtc,
            [],
            active: false,
            winnerClause: null,
            winnerInput: null,
            obligationEmission: null);
        var forkZ = NewFork(
            "fork/z",
            tokenZ.Id,
            sourceToken.Node,
            sourceToken.Node,
            occurrence: 0,
            parentBindings: sourceToken.Bindings,
            parentRequestObligations: [],
            branches: [],
            selectedBranches: [],
            resolved: true);
        var forkA = NewFork(
            "fork/a",
            tokenA.Id,
            sourceToken.Node,
            sourceToken.Node,
            occurrence: 0,
            parentBindings: sourceToken.Bindings,
            parentRequestObligations: [],
            branches: [],
            selectedBranches: [],
            resolved: true);
        var receiptZ = Receipt(plan, initial, tokenZ.Id, "emission/z");
        var receiptA = Receipt(plan, initial, tokenA.Id, "emission/a");
        var requestContract = new RequestContractReference(Reference("request/contract", '1'));
        var requestZ = new ProcessOutstandingRequest(
            tokenZ.Id,
            sourceToken.Node,
            new("request/z"),
            requestContract,
            ObservedAtUtc);
        var requestA = new ProcessOutstandingRequest(
            tokenA.Id,
            sourceToken.Node,
            new("request/a"),
            requestContract,
            ObservedAtUtc);
        var malformed = CopyState(
            initial,
            tokens: [tokenZ, tokenA, tokenA],
            forks: [forkZ, forkA, forkA],
            waits: [waitZ, waitA, waitA],
            inputReceipts: [receiptZ, receiptA, receiptA],
            outstandingRequests: [requestZ, requestA, requestA]);

        var validation = ProcessContinuationValidator.Validate(plan, malformed);

        Assert.Equal(
            5,
            validation.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.IdentityDuplicate));
        Assert.Equal(
            5,
            validation.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.CanonicalOrderInvalid));
        Assert.All(
            new[] { "/tokens", "/forks", "/waits", "/inputReceipts", "/outstandingRequests" },
            collection =>
            {
                Assert.Contains(
                    validation.Diagnostics,
                    diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.IdentityDuplicate
                                  && diagnostic.Location!.StartsWith(collection, StringComparison.Ordinal));
                Assert.Contains(
                    validation.Diagnostics,
                    diagnostic => diagnostic.Code == ProcessContinuationDiagnosticCodes.CanonicalOrderInvalid
                                  && diagnostic.Location!.StartsWith(collection, StringComparison.Ordinal));
            });
    }

    [Fact]
    public void MissingPlanNodes_AreReportedForEveryNodeBearingStateFamily()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.Const("done"))]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var token = CopyToken(Assert.Single(initial.Tokens), node: new("missing/token"));
        var wait = NewWait(
            new("wait/missing"),
            token.Id,
            new("missing/wait"),
            occurrence: 0,
            ProcessWaitKind.DurableCut,
            ObservedAtUtc,
            [],
            active: false,
            winnerClause: null,
            winnerInput: null,
            obligationEmission: null);
        var fork = NewFork(
            "fork/missing",
            token.Id,
            new("missing/fork"),
            new("missing/join"),
            occurrence: 0,
            parentBindings: token.Bindings,
            parentRequestObligations: [],
            branches: [],
            selectedBranches: [],
            resolved: true);
        var request = new ProcessOutstandingRequest(
            token.Id,
            new("missing/request"),
            new("request/missing"),
            new(Reference("request/contract", '2')),
            ObservedAtUtc);
        var malformed = CopyState(
            initial,
            tokens: [token],
            forks: [fork],
            waits: [wait],
            outstandingRequests: [request]);

        var validation = ProcessContinuationValidator.Validate(plan, malformed);

        Assert.Equal(
            5,
            validation.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.NodeUnresolved));
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/tokens/0/node");
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/forks/0/fork");
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/forks/0/join");
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/waits/0/node");
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/outstandingRequests/0/node");
    }

    [Fact]
    public void ActiveWait_RequiresItsExactWaitingTokenAndNode()
    {
        var valid = CreateCutState();
        var waiting = Assert.Single(valid.State.Tokens);
        var malformedToken = CopyToken(waiting, disposition: ExecutionTokenDisposition.Ready);
        var malformed = CopyState(valid.State, tokens: [malformedToken]);

        var validation = ProcessContinuationValidator.Validate(valid.Plan, malformed);

        var diagnostic = Assert.Single(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.WaitTokenMismatch);
        Assert.Equal("/waits/0", diagnostic.Location);
    }

    [Theory]
    [InlineData(ExecutionTokenDisposition.Unspecified)]
    [InlineData(ExecutionTokenDisposition.Active)]
    public void RestoredToken_RejectsNonDurableLifecycleDispositions(
        ExecutionTokenDisposition disposition)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var source = Assert.Single(initial.Tokens);
        var malformed = CopyState(
            initial,
            tokens: [CopyToken(source, disposition: disposition)]);

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, malformed);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TokenStateInvalid
            && diagnostic.Location == "/tokens/0/disposition");
    }

    [Fact]
    public void RestoredToken_RejectsNegativeExecutionStep()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var source = Assert.Single(initial.Tokens);
        var malformed = CopyState(
            initial,
            tokens: [CopyToken(source, step: -1)]);

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, malformed);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TokenStateInvalid
            && diagnostic.Location == "/tokens/0/step");
    }

    [Theory]
    [InlineData("duplicate", "/tokens/0/bindings/1/binding")]
    [InlineData("out-of-order", "/tokens/0/bindings/1/binding")]
    [InlineData("undeclared", "/tokens/0/bindings/1/value")]
    [InlineData("unknown", "/tokens/0/bindings/0/value")]
    [InlineData("failed", "/tokens/0/bindings/0/value")]
    [InlineData("contract", "/tokens/0/bindings/0/value")]
    public void RestoredToken_RejectsNonCanonicalOrInvalidBindingValues(
        string mutation,
        string expectedLocation)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var source = Assert.Single(initial.Tokens);
        var input = Assert.Single(source.Bindings);
        var relation = new ProcessBindingValue(
            new("relation.result"),
            ProcessDurabilityTestFixture.StringValue("relation"));
        var failure = new DocumentValidationDiagnostic(
            "tests.process.binding.failed",
            DiagnosticSeverity.Error,
            "The restored binding failed.");
        var booleanContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool));
        ImmutableArray<ProcessBindingValue> bindings = mutation switch
        {
            "duplicate" => [input, input],
            "out-of-order" => [relation, input],
            "undeclared" =>
            [
                input,
                new(new("zz.undeclared"), ProcessDurabilityTestFixture.StringValue("undeclared"))
            ],
            "unknown" =>
            [
                new(input.Binding, PortableValue.Unknown(input.Value.Contract)),
                relation
            ],
            "failed" =>
            [
                new(input.Binding, PortableValue.Failed(input.Value.Contract, failure)),
                relation
            ],
            "contract" =>
            [
                new(
                    input.Binding,
                    PortableValue.Concrete(booleanContract, ObservationValue.FromBool(true))),
                relation
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown binding mutation.")
        };
        var malformed = CopyState(
            initial,
            tokens: [CopyToken(source, bindings: bindings)]);

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, malformed);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TokenStateInvalid
            && diagnostic.Location == expectedLocation);
    }

    [Theory]
    [InlineData("failed-without-evidence")]
    [InlineData("non-failed-with-evidence")]
    public void RestoredToken_RequiresFailureEvidenceToMatchDisposition(string mutation)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var source = Assert.Single(initial.Tokens);
        var failure = new DocumentValidationDiagnostic(
            "tests.process.token.failed",
            DiagnosticSeverity.Error,
            "The restored token failed.");
        var token = mutation switch
        {
            "failed-without-evidence" => CopyToken(
                source,
                disposition: ExecutionTokenDisposition.Failed,
                failure: null,
                replaceFailure: true),
            "non-failed-with-evidence" => CopyToken(
                source,
                disposition: ExecutionTokenDisposition.Ready,
                failure: failure,
                replaceFailure: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown failure mutation.")
        };
        var malformed = CopyState(initial, tokens: [token]);

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, malformed);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TokenStateInvalid
            && diagnostic.Location == "/tokens/0/failure");
    }

    [Fact]
    public void RestoredToken_RejectsDuplicateMalformedRequestObligations()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var source = Assert.Single(initial.Tokens);
        var malformedObligation = new ProcessRequestObligation(
            new("undeclared.request"),
            fixture.Request);
        var malformed = CopyState(
            initial,
            tokens:
            [
                CopyToken(
                    source,
                    requestObligations: [malformedObligation, malformedObligation])
            ]);

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, malformed);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.TokenStateInvalid
            && diagnostic.Location == "/tokens/0/requestObligations/1/binding");
        Assert.Equal(
            2,
            validation.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.TokenStateInvalid
                && diagnostic.Location!.EndsWith("/request", StringComparison.Ordinal)));
    }

    [Fact]
    public void WaitingToken_RequiresExactlyOneCoordinationRegistration()
    {
        var valid = CreateCutState();
        var malformed = CopyState(valid.State, waits: []);

        var validation = ProcessContinuationValidator.Validate(valid.Plan, malformed);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessContinuationDiagnosticCodes.WaitTokenMismatch);
        Assert.Equal("/tokens/0", diagnostic.Location);
        Assert.Equal("1", diagnostic.Evidence?.Expected);
        Assert.Equal("0", diagnostic.Evidence?.Observed);
    }

    [Fact]
    public void WaitKind_MustMatchItsExactCompiledNodeBeforeResume()
    {
        var valid = CreateCutState();
        var wait = Assert.Single(valid.State.Waits);
        var malformedWait = NewWait(
            wait.RegistrationId,
            wait.Token,
            wait.Node,
            wait.Occurrence,
            ProcessWaitKind.Timer,
            wait.RegisteredAtUtc,
            [new(wait.Node, ObservedAtUtc.AddMinutes(1), Priority: 0)],
            wait.Active,
            wait.WinnerClause,
            wait.WinnerInput,
            obligationEmission: null);
        var malformed = CopyState(valid.State, waits: [malformedWait]);

        var validation = ProcessContinuationValidator.Validate(valid.Plan, malformed);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessContinuationDiagnosticCodes.WaitShapeMismatch);
        Assert.Equal("/waits/0/kind", diagnostic.Location);
    }

    [Fact]
    public void RequestWaitAndOutstandingRequest_MustMatchExactly()
    {
        var valid = CreateRequestState();
        var outstanding = Assert.Single(valid.State.OutstandingRequests);
        var malformed = CopyState(
            valid.State,
            outstandingRequests:
            [outstanding with { Emission = new("emission/different") }]);

        var validation = ProcessContinuationValidator.Validate(valid.Plan, malformed);

        Assert.Equal(
            2,
            validation.Diagnostics.Count(static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.RequestStateMismatch));
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/waits/0");
        Assert.Contains(validation.Diagnostics, static diagnostic => diagnostic.Location == "/outstandingRequests/0");
    }

    [Fact]
    public void ForkBranchAndChildMembershipContradictions_AreReported()
    {
        var valid = CreateForkState();
        var fork = Assert.Single(valid.State.Forks);
        var branch = fork.Branches[0];
        var childIndex = Enumerable.Range(0, valid.State.Tokens.Length)
            .Single(index => valid.State.Tokens[index].Id == branch.Token);
        var child = valid.State.Tokens[childIndex];
        var malformedChild = CopyToken(
            child,
            forkMembership: new(fork.RegistrationId, new("branch/not-owned")),
            replaceForkMembership: true);
        var tokens = valid.State.Tokens.SetItem(childIndex, malformedChild);
        var malformed = CopyState(valid.State, tokens: tokens);

        var validation = ProcessContinuationValidator.Validate(valid.Plan, malformed);

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location!.StartsWith("/forks/0/branches", StringComparison.Ordinal));
        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == $"/tokens/{childIndex}/forkMembership");
    }

    [Fact]
    public void ForkOccurrence_MustMatchOwnerHistoryAndDerivedIdentity()
    {
        var valid = CreateForkState();
        var fork = Assert.Single(valid.State.Forks);
        var changedOccurrence = CopyFork(fork, occurrence: fork.Occurrence + 1);

        var occurrenceValidation = ProcessContinuationValidator.Validate(
            valid.Plan,
            CopyState(valid.State, forks: [changedOccurrence]));
        var negativeValidation = ProcessContinuationValidator.Validate(
            valid.Plan,
            CopyState(valid.State, forks: [CopyFork(fork, occurrence: -1)]));

        Assert.Contains(occurrenceValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/occurrence");
        Assert.Contains(occurrenceValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/registrationId");
        Assert.Contains(negativeValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/occurrence");
    }

    [Fact]
    public void ForkCompletionSequences_MustMatchPolicyAndContiguousTerminalHistory()
    {
        var unobservable = CreateResolvedForkState(
            ProcessJoinCompletionOrder.Unobservable,
            ProcessJoinTieBreak.BranchIdentity);
        var unobservableFork = Assert.Single(unobservable.State.Forks);
        var unexpectedSequence = unobservableFork.Branches.SetItem(
            0,
            unobservableFork.Branches[0] with { CompletionSequence = 1 });

        var unobservableValidation = ProcessContinuationValidator.Validate(
            unobservable.Plan,
            CopyState(
                unobservable.State,
                forks: [CopyFork(unobservableFork, branches: unexpectedSequence)]));

        Assert.Contains(unobservableValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/branches/0/completionSequence");

        var observable = CreateResolvedForkState(
            ProcessJoinCompletionOrder.Observable,
            ProcessJoinTieBreak.CompletionThenBranchIdentity);
        Assert.True(ProcessContinuationValidator.Validate(observable.Plan, observable.State).IsValid);
        var observableFork = Assert.Single(observable.State.Forks);
        var firstSequence = Assert.IsType<long>(observableFork.Branches[0].CompletionSequence);
        var duplicateSequence = observableFork.Branches.SetItem(
            1,
            observableFork.Branches[1] with { CompletionSequence = firstSequence });
        var missingSequence = observableFork.Branches.SetItem(
            0,
            observableFork.Branches[0] with { CompletionSequence = null });

        foreach (var corruptedBranches in new[] { duplicateSequence, missingSequence })
        {
            var validation = ProcessContinuationValidator.Validate(
                observable.Plan,
                CopyState(
                    observable.State,
                    forks: [CopyFork(observableFork, branches: corruptedBranches)]));

            Assert.Contains(validation.Diagnostics, static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
                && diagnostic.Location!.Contains("completionSequence", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ForkSelection_MustContainUniqueCompletedMembersInCanonicalPolicyOrder()
    {
        var all = CreateResolvedForkState(
            ProcessJoinCompletionOrder.Unobservable,
            ProcessJoinTieBreak.BranchIdentity);
        var allFork = Assert.Single(all.State.Forks);
        var reversed = CopyFork(
            allFork,
            selectedBranches: [.. allFork.SelectedBranches.Reverse()]);
        var duplicated = CopyFork(
            allFork,
            selectedBranches: [allFork.SelectedBranches[0], allFork.SelectedBranches[0]]);
        var missing = CopyFork(
            allFork,
            selectedBranches: [allFork.SelectedBranches[0], new("branch/missing")]);

        foreach (var corruptedFork in new[] { reversed, duplicated, missing })
        {
            var validation = ProcessContinuationValidator.Validate(
                all.Plan,
                CopyState(all.State, forks: [corruptedFork]));

            Assert.Contains(validation.Diagnostics, static diagnostic =>
                diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
                && diagnostic.Location!.StartsWith("/forks/0/selectedBranches", StringComparison.Ordinal));
        }

        var any = CreateResolvedAnyForkState();
        var anyFork = Assert.Single(any.State.Forks);
        var cancelled = Assert.Single(
            anyFork.Branches,
            static branch => branch.Disposition == ExecutionTokenDisposition.Cancelled);
        var incompleteSelection = CopyFork(anyFork, selectedBranches: [cancelled.Branch]);
        var incompleteValidation = ProcessContinuationValidator.Validate(
            any.Plan,
            CopyState(any.State, forks: [incompleteSelection]));

        Assert.Contains(incompleteValidation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/selectedBranches/0");
    }

    [Fact]
    public void ForkSelection_MustRetainTheCanonicalObservablePolicyWinners()
    {
        var valid = CreateResolvedRequiredCountForkState();
        Assert.True(ProcessContinuationValidator.Validate(valid.Plan, valid.State).IsValid);
        var fork = Assert.Single(valid.State.Forks);
        var unselected = Assert.Single(
            fork.Branches,
            branch => !fork.SelectedBranches.Contains(branch.Branch));
        var corrupted = CopyFork(
            fork,
            selectedBranches: [fork.SelectedBranches[0], unselected.Branch]);

        var validation = ProcessContinuationValidator.Validate(
            valid.Plan,
            CopyState(valid.State, forks: [corrupted]));

        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/selectedBranches");
    }

    [Fact]
    public void ForkResolvedState_MustAgreeWithCoordinatorAndJoinEvidence()
    {
        var resolved = CreateResolvedForkState(
            ProcessJoinCompletionOrder.Unobservable,
            ProcessJoinTieBreak.BranchIdentity);
        var resolvedFork = Assert.Single(resolved.State.Forks);
        var falselyUnresolved = ProcessContinuationValidator.Validate(
            resolved.Plan,
            CopyState(resolved.State, forks: [CopyFork(resolvedFork, resolved: false)]));

        var unresolved = CreateForkState();
        var unresolvedFork = Assert.Single(unresolved.State.Forks);
        var falselyResolved = ProcessContinuationValidator.Validate(
            unresolved.Plan,
            CopyState(unresolved.State, forks: [CopyFork(unresolvedFork, resolved: true)]));

        Assert.Contains(falselyUnresolved.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/resolved");
        Assert.Contains(falselyResolved.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessContinuationDiagnosticCodes.ForkStateMismatch
            && diagnostic.Location == "/forks/0/resolved");
    }

    [Theory]
    [InlineData(ProcessInputAdmissionReason.Unspecified)]
    [InlineData(ProcessInputAdmissionReason.Early)]
    [InlineData((ProcessInputAdmissionReason)999)]
    public void InputReceipt_RequiresAClosedReasonCompatibleWithItsPolicyDisposition(
        ProcessInputAdmissionReason reason)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var token = Assert.Single(initial.Tokens);
        var receipt = Receipt(fixture.Plan, initial, token.Id, "emission/invalid-reason") with
        {
            Reason = reason
        };
        var malformed = CopyState(initial, inputReceipts: [receipt]);

        var validation = ProcessContinuationValidator.Validate(fixture.Plan, malformed);

        var diagnostic = Assert.Single(validation.Diagnostics, static candidate =>
            candidate.Code == ProcessContinuationDiagnosticCodes.InputStateMismatch
            && candidate.Location == "/inputReceipts/0/reason");
        Assert.Equal("processContinuationRestore", diagnostic.Evidence?.Stage);
    }

    [Fact]
    public void InputAdmissionEvidence_RequiresTheExactReasonDispositionAndWaitOccurrenceMatrix()
    {
        ProcessWaitRegistrationId wait = new("wait/input-admission-matrix");
        HashSet<(ProcessInputAdmissionDisposition Disposition, ProcessInputAdmissionReason Reason)> validPairs =
        [
            (ProcessInputAdmissionDisposition.Buffered, ProcessInputAdmissionReason.Early),
            (ProcessInputAdmissionDisposition.Buffered, ProcessInputAdmissionReason.WaitCandidate),
            (ProcessInputAdmissionDisposition.Consumed, ProcessInputAdmissionReason.Consumed),
            (ProcessInputAdmissionDisposition.Buffered, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Consumed, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Duplicate, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Late, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Stale, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.MissingTarget, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Observed, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.DeadLettered, ProcessInputAdmissionReason.Duplicate),
            (ProcessInputAdmissionDisposition.Late, ProcessInputAdmissionReason.Late),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.Late),
            (ProcessInputAdmissionDisposition.Observed, ProcessInputAdmissionReason.Late),
            (ProcessInputAdmissionDisposition.Consumed, ProcessInputAdmissionReason.Late),
            (ProcessInputAdmissionDisposition.Stale, ProcessInputAdmissionReason.Stale),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.Stale),
            (ProcessInputAdmissionDisposition.Observed, ProcessInputAdmissionReason.Stale),
            (ProcessInputAdmissionDisposition.Consumed, ProcessInputAdmissionReason.Stale),
            (ProcessInputAdmissionDisposition.MissingTarget, ProcessInputAdmissionReason.MissingTarget),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.MissingTarget),
            (ProcessInputAdmissionDisposition.Observed, ProcessInputAdmissionReason.MissingTarget),
            (ProcessInputAdmissionDisposition.DeadLettered, ProcessInputAdmissionReason.MissingTarget),
            (ProcessInputAdmissionDisposition.Late, ProcessInputAdmissionReason.Superseded),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.Superseded),
            (ProcessInputAdmissionDisposition.Observed, ProcessInputAdmissionReason.Superseded),
            (ProcessInputAdmissionDisposition.Consumed, ProcessInputAdmissionReason.Superseded),
            (ProcessInputAdmissionDisposition.IdentityConflict, ProcessInputAdmissionReason.IdentityConflict),
            (ProcessInputAdmissionDisposition.TerminalUnconsumed, ProcessInputAdmissionReason.TerminalUnconsumed),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.InvalidEnvelope),
            (ProcessInputAdmissionDisposition.Rejected, ProcessInputAdmissionReason.ContractMismatch)
        ];
        HashSet<ProcessInputAdmissionReason> waitRequired =
        [
            ProcessInputAdmissionReason.WaitCandidate,
            ProcessInputAdmissionReason.Consumed,
            ProcessInputAdmissionReason.Late,
            ProcessInputAdmissionReason.Superseded,
            ProcessInputAdmissionReason.ContractMismatch
        ];
        HashSet<ProcessInputAdmissionReason> waitForbidden =
        [
            ProcessInputAdmissionReason.Early,
            ProcessInputAdmissionReason.TerminalUnconsumed
        ];

        foreach (var disposition in Enum.GetValues<ProcessInputAdmissionDisposition>())
        {
            foreach (var reason in Enum.GetValues<ProcessInputAdmissionReason>())
            {
                foreach (var waitRegistrationId in new ProcessWaitRegistrationId?[] { null, wait })
                {
                    var expected = validPairs.Contains((disposition, reason))
                        && (!waitRequired.Contains(reason) || waitRegistrationId is not null)
                        && (!waitForbidden.Contains(reason) || waitRegistrationId is null);
                    Assert.Equal(
                        expected,
                        ProcessInputReceipt.IsValidAdmissionEvidence(disposition, reason, waitRegistrationId));
                }
            }
        }
    }

    [Fact]
    public void TerminalContinuation_CannotRetainBufferedInputs()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.Const("done"))]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var terminal = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/terminal"),
            RejectingHost.Instance).State;
        var token = Assert.Single(terminal.Tokens);
        var buffered = new ProcessBufferedInput(
            Receipt(plan, terminal, token.Id, "emission/stranded").Input,
            ObservedAtUtc);
        var malformed = CopyState(terminal, bufferedInputs: [buffered]);

        var validation = ProcessContinuationValidator.Validate(plan, malformed);

        var diagnostic = Assert.Single(
            validation.Diagnostics,
            static candidate => candidate.Code == ProcessContinuationDiagnosticCodes.TerminalStateInvalid);
        Assert.Equal("/bufferedInputs", diagnostic.Location);
        Assert.Single(
            validation.Diagnostics,
            static candidate => candidate.Code == ProcessContinuationDiagnosticCodes.InputStateMismatch);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreateCutState()
    {
        var plan = Compile(Definition(
            "cut",
            [
                new DurableCutProcessNode(new("cut"), Edge("edge/cut-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var state = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/cut"),
            RejectingHost.Instance).State;
        return (plan, state);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreateForkState()
    {
        var plan = Compile(Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/alpha"), Edge("edge/alpha-cut", "cut/alpha")),
                        new(new("branch/beta"), Edge("edge/beta-cut", "cut/beta"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("cut/alpha"), Edge("edge/alpha-join", "join")),
                new DurableCutProcessNode(new("cut/beta"), Edge("edge/beta-join", "join")),
                new JoinProcessNode(new("join"), new("fork"), JoinAll(), Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var state = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/fork"),
            RejectingHost.Instance).State;
        return (plan, state);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreateResolvedForkState(
        ProcessJoinCompletionOrder completionOrder,
        ProcessJoinTieBreak tieBreak)
    {
        var plan = Compile(Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/alpha"), Edge("edge/alpha-join", "join")),
                        new(new("branch/beta"), Edge("edge/beta-join", "join"))
                    ],
                    new("join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new(
                        ProcessJoinMode.All,
                        requiredCount: 0,
                        ProcessJoinFailurePolicy.FailFast,
                        ProcessJoinCancellationPolicy.AwaitRemaining,
                        completionOrder,
                        tieBreak),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var state = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/fork/resolved"),
            RejectingHost.Instance).State;
        return (plan, state);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreateResolvedAnyForkState()
    {
        var plan = Compile(Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/alpha"), Edge("edge/alpha-join", "join")),
                        new(new("branch/beta"), Edge("edge/beta-timer", "timer/beta"))
                    ],
                    new("join")),
                new TimerProcessNode(
                    new("timer/beta"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(ObservedAtUtc.AddDays(1))),
                    Edge("edge/beta-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new(
                        ProcessJoinMode.Any,
                        requiredCount: 0,
                        ProcessJoinFailurePolicy.FailFast,
                        ProcessJoinCancellationPolicy.CancelRemaining,
                        ProcessJoinCompletionOrder.Unobservable,
                        ProcessJoinTieBreak.BranchIdentity),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var cut = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/fork/any/cut"),
            RejectingHost.Instance).State;
        var resolved = ProcessReferenceInterpreter.Activate(
            plan,
            cut,
            Activation(
                "activation/fork/any/resolved",
                ProcessActivationCause.Timer,
                ObservedAtUtc.AddMinutes(1)),
            RejectingHost.Instance).State;
        return (plan, resolved);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreateResolvedRequiredCountForkState()
    {
        var plan = Compile(Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/alpha"), Edge("edge/alpha-join", "join")),
                        new(new("branch/beta"), Edge("edge/beta-join", "join")),
                        new(new("branch/gamma"), Edge("edge/gamma-join", "join"))
                    ],
                    new("join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new(
                        ProcessJoinMode.RequiredCount,
                        requiredCount: 2,
                        ProcessJoinFailurePolicy.FailFast,
                        ProcessJoinCancellationPolicy.ContinueRemaining,
                        ProcessJoinCompletionOrder.Observable,
                        ProcessJoinTieBreak.CompletionThenBranchIdentity),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var state = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/fork/required/resolved"),
            RejectingHost.Instance).State;
        return (plan, state);
    }

    static (CompiledProcessPlan Plan, ProcessContinuationState State) CreateRequestState()
    {
        var response = new RequestResponseObligation(
            [new RequestResultDefinition(new("accepted"), StringSchema("request-result/v1"))],
            RequestOptionalTerminalSemantics.Unsupported,
            RequestOptionalTerminalSemantics.Unsupported,
            RequestResultDisposition.Observe,
            RequestResultDisposition.Reject,
            RequestResultDisposition.ReusePriorDisposition,
            RequestRetrySemantics.StableIdentity,
            RequestResolutionSemantics.Reconcile,
            RequestResolutionSemantics.Escalate,
            TimeSpan.FromDays(1));
        var requestDocument = InteractionContractDocuments.Create(
            new("interaction/request/continuation-validator"),
            new("revision/1"),
            new RequestContractDefinition(StringSchema("request/v1"), response),
            Provenance());
        var catalogValidation = InteractionContractCatalog.TryCreate([requestDocument], out var catalog);
        Assert.True(catalogValidation.IsValid, FormatDiagnostics(catalogValidation));
        var request = new RequestContractReference(new(
            requestDocument.Metadata.DefinitionId,
            requestDocument.Metadata.RevisionId,
            requestDocument.Metadata.Fingerprint));
        var plan = Compile(
            Definition(
                "request",
                [
                    new RequestProcessNode(
                        new("request"),
                        request,
                        Expr.Const("payload"),
                        [new(new("outcome/accepted"), new("accepted"), new(Edge("edge/request-return", "return")))]),
                    new ReturnProcessNode(new("return"), Expr.Const("done"))
                ]),
            Assert.IsType<InteractionContractCatalog>(catalog));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var state = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/request"),
            RejectingHost.Instance).State;
        return (plan, state);
    }

    static ProcessInputReceipt Receipt(
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        TokenId token,
        string emission)
    {
        var target = new ProcessTokenInteractionTarget(state.Continuation, token);
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new(emission),
                new ProcessInteractionOrigin(
                    plan.DefinitionReference,
                    plan.Definition.Entry,
                    state.Continuation,
                    new("activation/source"),
                    token),
                new("correlation/continuation-validator"),
                causationId: null,
                new("authority/tests", "tenant/cohesive"),
                new($"idempotency/{emission}"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()),
            new(Reference("event/contract", '3')),
            StringValue("payload"));
        return new(
            new(target, envelope),
            ProcessInputAdmissionDisposition.Observed,
            ProcessInputAdmissionReason.MissingTarget,
            ObservedAtUtc);
    }

    static ProcessContinuationState CopyState(
        ProcessContinuationState source,
        ExecutionDefinitionReference? definition = null,
        ImmutableArray<ProcessTokenState> tokens = default,
        ImmutableArray<ProcessForkState> forks = default,
        ImmutableArray<ProcessChildState> children = default,
        ImmutableArray<ProcessPartitionState> partitions = default,
        ImmutableArray<ProcessRecurrenceState> recurrences = default,
        ImmutableArray<ProcessWaitState> waits = default,
        ImmutableArray<ProcessBufferedInput> bufferedInputs = default,
        ImmutableArray<ProcessInputReceipt> inputReceipts = default,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests = default,
        ExecutionTerminalOutcome? terminal = null) => NewContinuation(
            definition ?? source.Definition,
            source.Continuation,
            source.CompletedActivationCount,
            tokens.IsDefault ? source.Tokens : tokens,
            forks.IsDefault ? source.Forks : forks,
            children.IsDefault ? source.Children : children,
            partitions.IsDefault ? source.Partitions : partitions,
            recurrences.IsDefault ? source.Recurrences : recurrences,
            waits.IsDefault ? source.Waits : waits,
            bufferedInputs.IsDefault ? source.BufferedInputs : bufferedInputs,
            inputReceipts.IsDefault ? source.InputReceipts : inputReceipts,
            outstandingRequests.IsDefault ? source.OutstandingRequests : outstandingRequests,
            terminal ?? source.Terminal);

    static ProcessTokenState CopyToken(
        ProcessTokenState source,
        TokenId? id = null,
        ExecutionNodeId? node = null,
        ExecutionTokenDisposition? disposition = null,
        long? step = null,
        ImmutableArray<ProcessBindingValue> bindings = default,
        ImmutableArray<ProcessRequestObligation> requestObligations = default,
        ProcessForkMembership? forkMembership = null,
        bool replaceForkMembership = false,
        DocumentValidationDiagnostic? failure = null,
        bool replaceFailure = false) => NewToken(
            id ?? source.Id,
            node ?? source.Node,
            disposition ?? source.Disposition,
            step ?? source.Step,
            bindings.IsDefault ? source.Bindings : bindings,
            requestObligations.IsDefault ? source.RequestObligations : requestObligations,
            replaceForkMembership ? forkMembership : source.ForkMembership,
            replaceFailure ? failure : source.Failure);

    static ProcessForkState CopyFork(
        ProcessForkState source,
        long? occurrence = null,
        ImmutableArray<ProcessForkBranchState> branches = default,
        ImmutableArray<ExecutionNodeId> selectedBranches = default,
        bool? resolved = null) => NewFork(
            source.RegistrationId,
            source.Owner,
            source.Fork,
            source.Join,
            occurrence ?? source.Occurrence,
            source.ParentBindings,
            source.ParentRequestObligations,
            branches.IsDefault ? source.Branches : branches,
            selectedBranches.IsDefault ? source.SelectedBranches : selectedBranches,
            resolved ?? source.Resolved);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessContinuationState NewContinuation(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions,
        ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessTokenState NewToken(
        TokenId id,
        ExecutionNodeId node,
        ExecutionTokenDisposition disposition,
        long step,
        ImmutableArray<ProcessBindingValue> bindings,
        ImmutableArray<ProcessRequestObligation> requestObligations,
        ProcessForkMembership? forkMembership,
        DocumentValidationDiagnostic? failure);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessWaitState NewWait(
        ProcessWaitRegistrationId registrationId,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        ProcessWaitKind kind,
        DateTimeOffset registeredAtUtc,
        ImmutableArray<ProcessTimerState> timers,
        bool active,
        ExecutionNodeId? winnerClause,
        EmissionId? winnerInput,
        EmissionId? obligationEmission);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessForkState NewFork(
        string registrationId,
        TokenId owner,
        ExecutionNodeId fork,
        ExecutionNodeId join,
        long occurrence,
        ImmutableArray<ProcessBindingValue> parentBindings,
        ImmutableArray<ProcessRequestObligation> parentRequestObligations,
        ImmutableArray<ProcessForkBranchState> branches,
        ImmutableArray<ExecutionNodeId> selectedBranches,
        bool resolved);

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? catalog = null)
    {
        var document = ProcessDefinitionDocuments.Create(
            new("process/continuation-validator-tests"),
            new("revision/1"),
            definition,
            Provenance());
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(interactionContracts: catalog));
        Assert.True(compilation.IsSuccessful, FormatDiagnostics(compilation.Validation));
        return Assert.IsType<CompiledProcessPlan>(compilation.Plan);
    }

    static CanonicalProcessDefinition Definition(
        string entry,
        params ReadOnlySpan<CanonicalProcessNode> nodes) => new(
        StringContract,
        StringContract,
        new(entry),
        [.. nodes],
        ProcessRecoveryPolicy.ContinueAttempt);

    static ProcessJoinPolicy JoinAll() => new(
        ProcessJoinMode.All,
        requiredCount: 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static ProcessContinuationIdentity Continuation() => new(
        new("process-instance/continuation-validator"),
        new("process-attempt/1"));

    static ProcessActivation Activation(
        string id,
        ProcessActivationCause cause = ProcessActivationCause.Start,
        DateTimeOffset? observedAtUtc = null) => new(
        new(id),
        cause,
        observedAtUtc ?? ObservedAtUtc,
        new(
            new("authority/tests", "tenant/cohesive"),
            new("correlation/continuation-validator"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()));

    static ExecutionDefinitionReference Reference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static InteractionValueSchema StringSchema(string revision) =>
        new(StringContract, new(revision));

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static ExecutionProvenance Provenance() => new(
        new("process-continuation-validator-tests", "1"),
        new("tests/execution-kernel/process-continuation-validator"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed class RejectingHost : IProcessReferenceHost
    {
        public static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}

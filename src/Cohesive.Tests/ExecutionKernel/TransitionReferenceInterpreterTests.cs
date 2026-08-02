using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TransitionReferenceInterpreterTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract Int64Contract = new(new ScalarTypeRef(ScalarTypeKind.Int64));

    [Theory]
    [InlineData(true, "approve", TransitionDecisionKind.Applied, "approved", true)]
    [InlineData(true, "hold", TransitionDecisionKind.Applied, "held", false)]
    [InlineData(false, "approve", TransitionDecisionKind.NoChange, null, false)]
    public void Decide_Ek01Branches_FullAndSparseProduceEquivalentDecisions(
        bool approved,
        string decision,
        TransitionDecisionKind expectedKind,
        string? expectedStatus,
        bool expectedEmission)
    {
        var fixture = Ek01Fixture();
        var input = Object(
            fixture.Definition.Input,
            ("approved", ObservationValue.FromBool(approved)),
            ("decision", ObservationValue.FromString(decision)));
        var state = Object(
            fixture.Definition.Observation,
            ("status", ObservationValue.FromString("pending")),
            ("eligible", ObservationValue.FromBool(true)),
            ("caseId", ObservationValue.FromString("case-1")),
            ("unused", ObservationValue.FromString("not-read")));
        var sparse = new List<TransitionObservationEntry>
        {
            Entry(fixture.Plan, "status", ObservationValue.FromString("pending")),
            Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
        };
        if (expectedEmission)
            sparse.Add(Entry(fixture.Plan, "caseId", ObservationValue.FromString("case-1")));

        var fullDecision = TransitionReferenceInterpreter.DecideFullState(
            fixture.Plan,
            new("ek-01"),
            input,
            state);
        var sparseDecision = TransitionReferenceInterpreter.DecideSparse(
            fixture.Plan,
            new("ek-01"),
            input,
            sparse);

        Assert.Equivalent(fullDecision, sparseDecision, strict: true);
        Assert.Equal(expectedKind, fullDecision.Kind);
        Assert.Equal(expectedEmission ? 1 : 0, fullDecision.Emissions.Length);
        Assert.Equal(expectedEmission || expectedStatus is not null, fullDecision.GuaranteeDemands.CommitRequired);
        if (expectedStatus is null)
        {
            Assert.Empty(fullDecision.Patch);
        }
        else
        {
            var patch = Assert.Single(fullDecision.Patch);
            Assert.Equal("status", patch.Path.ToString());
            Assert.Equal(expectedStatus, patch.After.Value?.String);
        }
        Assert.DoesNotContain(
            fullDecision.Evidence.ActualReads,
            static access => access.Path?.Matches("unused") == true);
        if (expectedEmission)
        {
            Assert.Equal(
                ["status", "eligible", "caseId"],
                fullDecision.Evidence.ActualReads.Select(static access => access.Path!.Value.ToString()).ToArray());
            Assert.Equal(["status"], fullDecision.Evidence.ExecutedWrites.Select(static path => path.ToString()).ToArray());
            Assert.Equal(["status"], fullDecision.Evidence.ChangedPaths.Select(static path => path.ToString()).ToArray());
            Assert.Equal(
                ["eligible", "approve-case"],
                fullDecision.Evidence.SelectedCases.Select(static selectedCase => selectedCase.Value).ToArray());
            Assert.Equal(
                ["approved-event"],
                fullDecision.Evidence.EmittedIntents.Select(static node => node.Value).ToArray());
            Assert.Equal(
                ["status", "eligible", "caseId"],
                fullDecision.GuaranteeDemands.ConcurrencyObservations
                    .Select(static access => access.Path!.Value.ToString())
                    .ToArray());
        }
    }

    [Fact]
    public void Decide_AdmissionRejectedShortCircuitsBodyAndCommit()
    {
        var fixture = Ek01Fixture();
        var decision = TransitionReferenceInterpreter.DecideSparse(
            fixture.Plan,
            new("admission-rejected"),
            Object(
                fixture.Definition.Input,
                ("approved", ObservationValue.FromBool(true)),
                ("decision", ObservationValue.FromString("approve"))),
            [Entry(fixture.Plan, "status", ObservationValue.FromString("closed"))]);

        Assert.Equal(TransitionDecisionKind.AdmissionRejected, decision.Kind);
        Assert.Equal("notPending", decision.Outcome?.Value?.String);
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.Emissions);
        Assert.False(decision.GuaranteeDemands.CommitRequired);
        Assert.DoesNotContain(
            decision.Evidence.Trace,
            static item => item.Kind is TransitionTraceEventKind.CaseSelected
                or TransitionTraceEventKind.InvariantEvaluated);
    }

    [Fact]
    public void Decide_CommitValidationConflictsOnlyOnActualReads()
    {
        var fixture = Ek01Fixture();
        var input = Object(
            fixture.Definition.Input,
            ("approved", ObservationValue.FromBool(true)),
            ("decision", ObservationValue.FromString("hold")));
        var observed = new[]
        {
            Entry(fixture.Plan, "status", ObservationValue.FromString("pending")),
            Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
        };
        var fresh = new[]
        {
            Entry(fixture.Plan, "status", ObservationValue.FromString("changed")),
            Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true)),
            Entry(fixture.Plan, "unused", ObservationValue.FromString("also-changed"))
        };

        var conflict = TransitionReferenceInterpreter.DecideSparse(
            fixture.Plan,
            new("conflict"),
            input,
            observed,
            fresh);

        Assert.Equal(TransitionDecisionKind.Conflict, conflict.Kind);
        var mismatch = Assert.Single(conflict.Conflicts);
        Assert.Equal("status", mismatch.Access.Path?.ToString());
        Assert.Empty(conflict.Patch);
        Assert.Empty(conflict.Emissions);
        Assert.False(conflict.GuaranteeDemands.CommitRequired);
    }

    [Fact]
    public void Decide_MissingFreshEvidenceForActualReadFailsClosed()
    {
        var fixture = Ek01Fixture();
        var decision = TransitionReferenceInterpreter.DecideSparse(
            fixture.Plan,
            new("missing-fresh"),
            Object(
                fixture.Definition.Input,
                ("approved", ObservationValue.FromBool(true)),
                ("decision", ObservationValue.FromString("hold"))),
            [
                Entry(fixture.Plan, "status", ObservationValue.FromString("pending")),
                Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
            ],
            [Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))]);

        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, decision.Kind);
        Assert.Equal(
            TransitionExecutionDiagnosticCodes.CommitObservationUnavailable,
            Assert.Single(decision.Diagnostics).Code);
        AssertNoCommitArtifacts(decision);
    }

    [Fact]
    public void Decide_IndeterminateFreshEvidenceFailsClosedInsteadOfBecomingConflict()
    {
        var fixture = Ek01Fixture();
        var input = Object(
            fixture.Definition.Input,
            ("approved", ObservationValue.FromBool(true)),
            ("decision", ObservationValue.FromString("hold")));
        TransitionObservationEntry[] observed =
        [
            Entry(fixture.Plan, "status", ObservationValue.FromString("pending")),
            Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
        ];
        var statusContract = ContractAt(fixture.Plan, "status");
        var sourceFailure = new DocumentValidationDiagnostic(
            "test.commit.read.failed",
            DiagnosticSeverity.Error,
            "The commit-time status read failed.");

        var unknown = TransitionReferenceInterpreter.DecideSparse(
            fixture.Plan,
            new("fresh-unknown"),
            input,
            observed,
            [
                new(
                    TransitionObservationAccess.At(FieldPath.FromField("status")),
                    PortableValue.Unknown(statusContract)),
                Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
            ]);
        var failed = TransitionReferenceInterpreter.DecideSparse(
            fixture.Plan,
            new("fresh-failed"),
            input,
            observed,
            [
                new(
                    TransitionObservationAccess.At(FieldPath.FromField("status")),
                    PortableValue.Failed(statusContract, sourceFailure)),
                Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
            ]);

        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, unknown.Kind);
        Assert.Equal(
            TransitionExecutionDiagnosticCodes.CommitObservationUnknown,
            Assert.Single(unknown.Diagnostics).Code);
        Assert.Empty(unknown.Conflicts);
        AssertNoCommitArtifacts(unknown);
        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, failed.Kind);
        Assert.Equal(sourceFailure, Assert.Single(failed.Diagnostics));
        Assert.Empty(failed.Conflicts);
        AssertNoCommitArtifacts(failed);
    }

    [Fact]
    public void Decide_DomainRejectedDiscardsCandidatePatchButRetainsEmissionIntent()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var definition = Definition(
            Sequence(
                "root",
                Set("attempted", "status", Expr.Const("candidate")),
                new EmitTransitionNode(new("rejection-event"), InteractionReference(), Expr.Const("rejected")),
                Outcome("outcome", TransitionOutcomeDisposition.DomainRejected, "rejected")),
            observation: observation);
        var plan = Compile(definition);

        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("domain-rejected"),
            EmptyInput(definition.Input),
            [Entry(plan, "status", ObservationValue.FromString("pending"))]);

        Assert.Equal(TransitionDecisionKind.DomainRejected, decision.Kind);
        Assert.Empty(decision.Patch);
        Assert.Single(decision.Emissions);
        Assert.True(decision.GuaranteeDemands.CommitRequired);
        Assert.False(decision.GuaranteeDemands.AtomicPatchAndEmissions);
        Assert.Equal([FieldPath.FromField("status")], decision.Evidence.ChangedPaths.ToArray());
    }

    [Fact]
    public void Decide_IdempotentAppliedPatchBecomesNoChange()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var definition = Definition(
            Sequence(
                "root",
                Set("set-status", "status", Expr.Const("same")),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "ok")),
            observation: observation);
        var plan = Compile(definition);

        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("no-change"),
            EmptyInput(definition.Input),
            [Entry(plan, "status", ObservationValue.FromString("same"))]);

        Assert.Equal(TransitionDecisionKind.NoChange, decision.Kind);
        Assert.Empty(decision.Patch);
        Assert.False(decision.GuaranteeDemands.CommitRequired);
        Assert.Equal([FieldPath.FromField("status")], decision.Evidence.ExecutedWrites.ToArray());
        Assert.Empty(decision.Evidence.ChangedPaths);
    }

    [Fact]
    public void Decide_AuthoredNoChangeThatMutatesStateFailsClosed()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var definition = Definition(
            Sequence(
                "root",
                Set("set-status", "status", Expr.Const("changed")),
                Outcome("outcome", TransitionOutcomeDisposition.NoChange, "ok")),
            observation: observation);
        var plan = Compile(definition);
        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("invalid-no-change"),
            EmptyInput(definition.Input),
            [Entry(plan, "status", ObservationValue.FromString("original"))]);

        Assert.Equal(TransitionDecisionKind.InvalidDefinition, decision.Kind);
        Assert.Contains(
            decision.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.NoChangeModifiedState);
        AssertNoCommitArtifacts(decision);
    }

    [Fact]
    public void Decide_AllSparsePatchOperationsReturnExactBeforeAfterEvidence()
    {
        var child = new ObjectTypeRef([new("id", StringContract.Type!), new("name", StringContract.Type!)]);
        var input = ObjectContract(
            new("childId", StringContract.Type!),
            new("child", child));
        var observation = ObjectContract(
            new("status", StringContract.Type!),
            new("legacy", StringContract.Type!, presence: FieldPresence.Optional),
            new("score", Int64Contract.Type!),
            new("tags", StringContract.Type!, cardinality: FieldCardinality.Many),
            new("notes", StringContract.Type!, cardinality: FieldCardinality.Many),
            new("children", child, cardinality: FieldCardinality.Many),
            new("removedChildren", child, cardinality: FieldCardinality.Many));
        var definition = new CanonicalTransitionDefinition(
            input,
            observation,
            StringContract,
            [],
            Sequence(
                "root",
                Set("set", "status", Expr.Const("ready")),
                new UpdateTransitionNode(new("remove"), FieldPath.FromField("legacy"), new RemoveTransitionPatch()),
                new UpdateTransitionNode(new("increment"), FieldPath.FromField("score"), new IncrementTransitionPatch(Expr.Const(2))),
                new UpdateTransitionNode(new("add"), FieldPath.FromField("tags"), new AddToSetTransitionPatch(Expr.Const("b"))),
                new UpdateTransitionNode(new("append"), FieldPath.FromField("notes"), new AppendTransitionPatch(Expr.Const("second"))),
                new UpdateTransitionNode(
                    new("upsert"),
                    FieldPath.FromField("children"),
                    new UpsertOwnedChildTransitionPatch(
                        FieldPath.FromField("id"),
                        Expr.Param("childId"),
                        Expr.Param("child"))),
                new UpdateTransitionNode(
                    new("remove-child"),
                    FieldPath.FromField("removedChildren"),
                    new RemoveOwnedChildTransitionPatch(FieldPath.FromField("id"), Expr.Param("childId"))),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "ok")));
        var plan = Compile(definition);
        var childValue = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["id"] = ObservationValue.FromString("child-2"),
            ["name"] = ObservationValue.FromString("second")
        });
        var firstChild = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["id"] = ObservationValue.FromString("child-1"),
            ["name"] = ObservationValue.FromString("first")
        });
        var executionInput = Object(
            input,
            ("childId", ObservationValue.FromString("child-2")),
            ("child", childValue));
        var entries = new[]
        {
            Entry(plan, "status", ObservationValue.FromString("new")),
            Entry(plan, "legacy", ObservationValue.FromString("old")),
            Entry(plan, "score", ObservationValue.FromInt64(3)),
            Entry(plan, "tags", ObservationValue.FromArray([ObservationValue.FromString("a")])),
            Entry(plan, "notes", ObservationValue.FromArray([ObservationValue.FromString("first")])),
            Entry(plan, "children", ObservationValue.FromArray([firstChild])),
            Entry(plan, "removedChildren", ObservationValue.FromArray([firstChild, childValue]))
        };

        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("patch-algebra"),
            executionInput,
            entries);

        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        Assert.Collection(
            decision.Patch,
            static patch => Assert.IsType<EvaluatedSetTransitionPatch>(patch.Operation),
            static patch => Assert.IsType<EvaluatedRemoveTransitionPatch>(patch.Operation),
            static patch => Assert.IsType<EvaluatedIncrementTransitionPatch>(patch.Operation),
            static patch => Assert.IsType<EvaluatedAddToSetTransitionPatch>(patch.Operation),
            static patch => Assert.IsType<EvaluatedAppendTransitionPatch>(patch.Operation),
            static patch => Assert.IsType<EvaluatedUpsertOwnedChildTransitionPatch>(patch.Operation),
            static patch => Assert.IsType<EvaluatedRemoveOwnedChildTransitionPatch>(patch.Operation));
        Assert.All(decision.Patch, static patch => Assert.True(patch.Changed));
        Assert.Equal(7, decision.Evidence.ExecutedWrites.Length);
        Assert.Equal(7, decision.Evidence.ChangedPaths.Length);
    }

    [Fact]
    public void Decide_OwnedChildUpsertRejectsReplacementWhoseIdentityDiffersFromSelector()
    {
        var child = new ObjectTypeRef([new("id", StringContract.Type!), new("name", StringContract.Type!)]);
        var input = ObjectContract(
            new("childId", StringContract.Type!),
            new("child", child));
        var observation = ObjectContract(
            new ObjectFieldTypeDef("children", child, cardinality: FieldCardinality.Many));
        var definition = new CanonicalTransitionDefinition(
            input,
            observation,
            StringContract,
            [],
            Sequence(
                "root",
                new UpdateTransitionNode(
                    new("upsert"),
                    FieldPath.FromField("children"),
                    new UpsertOwnedChildTransitionPatch(
                        FieldPath.FromField("id"),
                        Expr.Param("childId"),
                        Expr.Param("child"))),
                Outcome("outcome", TransitionOutcomeDisposition.Applied)));
        var plan = Compile(definition);
        var replacement = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["id"] = ObservationValue.FromString("replacement"),
            ["name"] = ObservationValue.FromString("replacement child")
        });
        var existingWithReplacementIdentity = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["id"] = ObservationValue.FromString("replacement"),
            ["name"] = ObservationValue.FromString("existing child")
        });

        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("owned-child-identity-mismatch"),
            Object(
                input,
                ("childId", ObservationValue.FromString("selected")),
                ("child", replacement)),
            [
                Entry(
                    plan,
                    "children",
                    ObservationValue.FromArray([existingWithReplacementIdentity]))
            ]);

        Assert.Equal(TransitionDecisionKind.InvalidDefinition, decision.Kind);
        Assert.Equal(
            TransitionExecutionDiagnosticCodes.ResultContractViolated,
            Assert.Single(decision.Diagnostics).Code);
        AssertNoCommitArtifacts(decision);
    }

    [Fact]
    public void Decide_DerivedFieldsRecomputeBeforeInvariant()
    {
        var graph = DerivedGraph();
        var shape = graph.GetShape(new ShapeId("review"));
        var observation = ValueContract.FromShape(shape, graph.Qualify(shape.Id));
        var definition = new CanonicalTransitionDefinition(
            new(new ObjectTypeRef([])),
            observation,
            StringContract,
            [],
            Sequence(
                "root",
                new UpdateTransitionNode(new("set-raw"), FieldPath.FromField("raw"), new SetTransitionPatch(Expr.Const(10))),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "ok")),
            [new(new("eligible-invariant"), Expr.Eq(Expr.Field("eligible"), Expr.Const(true)))]);
        var plan = Compile(definition, graph);

        var full = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("derived"),
            EmptyInput(definition.Input),
            Object(
                observation,
                ("raw", ObservationValue.FromInt64(1)),
                ("normalized", ObservationValue.FromInt64(2)),
                ("eligible", ObservationValue.FromBool(false))));
        var sparse = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("derived"),
            EmptyInput(definition.Input),
            [
                Entry(plan, "raw", ObservationValue.FromInt64(1)),
                Entry(plan, "normalized", ObservationValue.FromInt64(2)),
                Entry(plan, "eligible", ObservationValue.FromBool(false))
            ]);

        Assert.Equivalent(full, sparse, strict: true);
        Assert.Equal(TransitionDecisionKind.Applied, full.Kind);
        Assert.Equal(["raw", "normalized", "eligible"], full.Patch.Select(static patch => patch.Path.ToString()));
        AssertTraceOrder(
            full,
            TransitionTraceEventKind.PatchExecuted,
            TransitionTraceEventKind.DerivedFieldRecomputed,
            TransitionTraceEventKind.InvariantEvaluated);
    }

    [Fact]
    public void Decide_InvariantFailureClearsPatchAndEmissionButRetainsTrace()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var definition = new CanonicalTransitionDefinition(
            new(new ObjectTypeRef([])),
            observation,
            StringContract,
            [],
            Sequence(
                "root",
                Set("set", "status", Expr.Const("invalid")),
                new EmitTransitionNode(new("emit"), InteractionReference(), Expr.Const("payload")),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "ok")),
            [new(new("invariant"), Expr.Ne(Expr.Field("status"), Expr.Const("invalid")))]);
        var plan = Compile(definition);

        var decision = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("invariant"),
            EmptyInput(definition.Input),
            [Entry(plan, "status", ObservationValue.FromString("pending"))]);

        Assert.Equal(TransitionDecisionKind.InvalidDefinition, decision.Kind);
        Assert.Contains(
            decision.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.InvariantViolated);
        AssertNoCommitArtifacts(decision);
        Assert.Contains(decision.Evidence.Trace, static item => item.Kind == TransitionTraceEventKind.PatchExecuted);
        Assert.Contains(decision.Evidence.Trace, static item => item.Kind == TransitionTraceEventKind.EmissionProduced);
        Assert.Contains(decision.Evidence.Trace, static item => item.Kind == TransitionTraceEventKind.InvariantEvaluated);
    }

    [Theory]
    [InlineData(PortableValueState.Absent, "absent")]
    [InlineData(PortableValueState.Null, "null")]
    public void Decide_ObservedAbsentAndNullRemainDistinct(
        PortableValueState state,
        string expectedOutcome)
    {
        var optional = new ValueContract(
            StringContract.Type,
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        var definition = Definition(
            Sequence(
                "root",
                new MatchTransitionNode(
                    new("match"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    Expr.Field("value"),
                    optional,
                    [
                        new(new("absent"), PortableValue.Absent(optional), Sequence("absent-body", Outcome("absent-outcome", value: "absent"))),
                        new(new("null"), PortableValue.Null(optional), Sequence("null-body", Outcome("null-outcome", value: "null")))
                    ],
                    new(new("concrete"), Sequence("concrete-body", Outcome("concrete-outcome", value: "concrete"))))),
            observation: ObjectContract(new ObjectFieldTypeDef(
                "value",
                StringContract.Type!,
                presence: FieldPresence.Optional,
                nullability: FieldNullability.Nullable)));
        var plan = Compile(definition);
        var contract = ContractAt(plan, "value");
        var value = state == PortableValueState.Absent
            ? PortableValue.Absent(contract)
            : PortableValue.Null(contract);

        var sparse = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("states"),
            EmptyInput(definition.Input),
            [new(TransitionObservationAccess.At(FieldPath.FromField("value")), value)]);
        var fullState = state == PortableValueState.Absent
            ? Object(definition.Observation)
            : Object(definition.Observation, ("value", ObservationValue.Null));
        var full = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("states"),
            EmptyInput(definition.Input),
            fullState);

        Assert.Equivalent(full, sparse, strict: true);
        Assert.Equal(TransitionDecisionKind.NoChange, sparse.Kind);
        Assert.Equal(expectedOutcome, sparse.Outcome?.Value?.String);
    }

    [Fact]
    public void Decide_NestedExactAndCoveringSparseObservationsMatchFullState()
    {
        var profileType = new ObjectTypeRef(
        [
            new("status", StringContract.Type!),
            new("unused", StringContract.Type!)
        ]);
        var observation = ObjectContract(new ObjectFieldTypeDef("profile", profileType));
        var definition = new CanonicalTransitionDefinition(
            new(new ObjectTypeRef([])),
            observation,
            StringContract,
            [
                new(
                    new("pending-only"),
                    Expr.Eq(Expr.Field("profile.status"), Expr.Const("pending")),
                    Expr.Const("notPending"))
            ],
            Sequence(
                "root",
                new UpdateTransitionNode(
                    new("approve"),
                    FieldPath.Parse("profile.status"),
                    new SetTransitionPatch(Expr.Const("approved"))),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "approved")));
        var plan = Compile(definition);
        var profile = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["status"] = ObservationValue.FromString("pending"),
            ["unused"] = ObservationValue.FromString("not-read")
        });
        var input = EmptyInput(definition.Input);
        var full = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("nested"),
            input,
            Object(observation, ("profile", profile)));
        var exact = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("nested"),
            input,
            [Entry(plan, "profile.status", ObservationValue.FromString("pending"))]);
        var covering = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("nested"),
            input,
            [
                new(
                    TransitionObservationAccess.At(FieldPath.FromField("profile")),
                    PortableValue.Concrete(ContractAt(plan, "profile"), profile))
            ]);

        Assert.Equivalent(full, exact, strict: true);
        Assert.Equivalent(full, covering, strict: true);
        Assert.Equal(
            ["profile.status"],
            full.Evidence.ActualReads.Select(static access => access.Path!.Value.ToString()).ToArray());
        Assert.Equal(
            ["profile.status"],
            full.Evidence.ChangedPaths.Select(static path => path.ToString()).ToArray());
    }

    [Fact]
    public void Decide_UnobservedAndUnknownValuesFailClosedDistinctly()
    {
        var optional = new ValueContract(StringContract.Type, presence: FieldPresence.Optional);
        var definition = Definition(
            Sequence(
                "root",
                new ChoiceTransitionNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [new(new("present"), Expr.Eq(Expr.Field("value"), Expr.Const("yes")), Sequence("yes", Outcome("yes-outcome")))],
                    new(new("fallback"), Sequence("no", Outcome("no-outcome"))))),
            observation: ObjectContract(new ObjectFieldTypeDef(
                "value",
                StringContract.Type!,
                presence: FieldPresence.Optional)));
        var plan = Compile(definition);

        var unobserved = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("unobserved"),
            EmptyInput(definition.Input),
            []);
        var unknown = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("unknown"),
            EmptyInput(definition.Input),
            [new(TransitionObservationAccess.At(FieldPath.FromField("value")), PortableValue.Unknown(optional))]);

        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, unobserved.Kind);
        Assert.Equal(TransitionExecutionDiagnosticCodes.ObservationUnavailable, Assert.Single(unobserved.Diagnostics).Code);
        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, unknown.Kind);
        Assert.Equal(TransitionExecutionDiagnosticCodes.ObservationUnknown, Assert.Single(unknown.Diagnostics).Code);
    }

    [Fact]
    public void Decide_UnknownAndElementSparsePathsReturnActivationDiagnostics()
    {
        var observation = ObjectContract(
            new("status", StringContract.Type!),
            new("tags", StringContract.Type!, cardinality: FieldCardinality.Many));
        var definition = Definition(
            Sequence("root", Outcome("outcome")),
            observation: observation);
        var plan = Compile(definition);
        var unknownPath = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("unknown-path"),
            EmptyInput(definition.Input),
            [
                new(
                    TransitionObservationAccess.At(FieldPath.FromField("not-in-contract")),
                    PortableValue.Concrete(StringContract, ObservationValue.FromString("value")))
            ]);
        var elementPath = new FieldPath(
            [FieldPathSegment.ForField("tags"), FieldPathSegment.Element()]);
        var element = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("element-path"),
            EmptyInput(definition.Input),
            [
                new(
                    TransitionObservationAccess.At(elementPath),
                    PortableValue.Concrete(StringContract, ObservationValue.FromString("tag")))
            ]);

        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, unknownPath.Kind);
        Assert.Equal(
            TransitionExecutionDiagnosticCodes.ActivationInvalid,
            Assert.Single(unknownPath.Diagnostics).Code);
        AssertNoCommitArtifacts(unknownPath);
        Assert.Equal(TransitionDecisionKind.InfrastructureFailure, element.Kind);
        Assert.Equal(
            TransitionExecutionDiagnosticCodes.ActivationInvalid,
            Assert.Single(element.Diagnostics).Code);
        AssertNoCommitArtifacts(element);
    }

    [Fact]
    public void Decide_LinkedMachineEdgeEnforcesSourceAndTargetConfigurations()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var machine = MachineReference();
        var edge = new ExecutionNodeId("approve");
        var definition = Definition(
            Sequence(
                "root",
                new MoveMachineTransitionNode(new("move"), machine, edge, Expr.Const("illegal")),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "moved")),
            observation: observation);
        var validLink = MachineLink(machine, edge, target: "approved");
        var validPlan = Compile(definition, machineLinks: new([validLink]));

        var full = TransitionReferenceInterpreter.DecideFullState(
            validPlan,
            new("machine"),
            EmptyInput(definition.Input),
            Object(observation, ("status", ObservationValue.FromString("pending"))));
        var sparse = TransitionReferenceInterpreter.DecideSparse(
            validPlan,
            new("machine"),
            EmptyInput(definition.Input),
            [Entry(validPlan, "status", ObservationValue.FromString("pending"))]);
        var illegal = TransitionReferenceInterpreter.DecideSparse(
            validPlan,
            new("machine-illegal"),
            EmptyInput(definition.Input),
            [Entry(validPlan, "status", ObservationValue.FromString("closed"))]);

        Assert.Equivalent(full, sparse, strict: true);
        Assert.Equal(TransitionDecisionKind.Applied, full.Kind);
        Assert.Single(full.MachineMovements);
        Assert.Equal("approved", Assert.Single(full.Patch).After.Value?.String);
        Assert.Equal(TransitionDecisionKind.AdmissionRejected, illegal.Kind);
        Assert.Equal("illegal", illegal.Outcome?.Value?.String);
        Assert.Empty(illegal.Patch);
        Assert.Empty(illegal.MachineMovements);

        var invalidTargetPlan = Compile(
            definition,
            machineLinks: new([MachineLink(machine, edge, target: "different")]));
        var invalidTarget = TransitionReferenceInterpreter.DecideSparse(
            invalidTargetPlan,
            new("machine-target"),
            EmptyInput(definition.Input),
            [Entry(invalidTargetPlan, "status", ObservationValue.FromString("pending"))]);
        Assert.Equal(TransitionDecisionKind.InvalidDefinition, invalidTarget.Kind);
        Assert.Contains(
            invalidTarget.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionExecutionDiagnosticCodes.MachineTargetViolated);
        AssertNoCommitArtifacts(invalidTarget);
    }

    [Fact]
    public void Decide_NoOpMachineSelfEdgeStillRequiresCommitAndFreshness()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var machine = MachineReference();
        var edge = new ExecutionNodeId("refresh");
        var definition = Definition(
            Sequence(
                "root",
                new MoveMachineTransitionNode(new("move"), machine, edge, Expr.Const("illegal")),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "moved")),
            observation: observation);
        var pending = PortableValue.Concrete(StringContract, ObservationValue.FromString("pending"));
        var link = new TransitionMachineEdgeLink(
            machine,
            edge,
            Expr.Eq(Expr.Field("status"), Expr.Const("pending")),
            Expr.Eq(Expr.Field("status"), Expr.Const("pending")),
            [new(FieldPath.FromField("status"), pending)]);
        var plan = Compile(definition, machineLinks: new([link]));
        var observed = new[] { Entry(plan, "status", ObservationValue.FromString("pending")) };

        var moved = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("machine-self-edge"),
            EmptyInput(definition.Input),
            observed,
            [Entry(plan, "status", ObservationValue.FromString("pending"))]);
        var conflict = TransitionReferenceInterpreter.DecideSparse(
            plan,
            new("machine-self-edge-conflict"),
            EmptyInput(definition.Input),
            observed,
            [Entry(plan, "status", ObservationValue.FromString("changed"))]);

        Assert.Equal(TransitionDecisionKind.Applied, moved.Kind);
        Assert.Empty(moved.Patch);
        var movement = Assert.Single(moved.MachineMovements);
        Assert.False(Assert.Single(movement.Assignments).Changed);
        Assert.True(moved.GuaranteeDemands.CommitRequired);
        Assert.Equal(
            ["status"],
            moved.GuaranteeDemands.ConcurrencyObservations
                .Select(static access => access.Path!.Value.ToString())
                .ToArray());
        Assert.Equal(
            ["status"],
            moved.Evidence.ActualReads.Select(static access => access.Path!.Value.ToString()).ToArray());
        Assert.Equal(TransitionDecisionKind.Conflict, conflict.Kind);
        Assert.Equal("status", Assert.Single(conflict.Conflicts).Access.Path?.ToString());
        AssertNoCommitArtifacts(conflict);
    }

    [Fact]
    public void Compile_MachineLinkLookupRequiresExactFingerprint()
    {
        var observation = ObjectContract(new ObjectFieldTypeDef("status", StringContract.Type!));
        var referencedMachine = MachineReference();
        var edge = new ExecutionNodeId("approve");
        var definition = Definition(
            Sequence(
                "root",
                new MoveMachineTransitionNode(
                    new("move"),
                    referencedMachine,
                    edge,
                    Expr.Const("illegal")),
                Outcome("outcome", TransitionOutcomeDisposition.Applied, "moved")),
            observation: observation);
        var mismatchedFingerprint = MachineReference('9');

        var result = TransitionStaticCompiler.Compile(
            Document(definition),
            machineLinks: new([MachineLink(mismatchedFingerprint, edge, target: "approved")]));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        Assert.Contains(
            result.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionCompilationDiagnosticCodes.MachineEdgeUnresolved);
    }

    [Fact]
    public void Decide_IsDirectSynchronousAndDeterministic()
    {
        var method = Assert.Single(
            typeof(TransitionReferenceInterpreter).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == nameof(TransitionReferenceInterpreter.Decide));
        Assert.Equal(typeof(TransitionDecision), method.ReturnType);
        Assert.DoesNotContain(method.GetParameters(), static parameter =>
            parameter.ParameterType == typeof(IServiceProvider)
            || parameter.ParameterType == typeof(CancellationToken)
            || typeof(Delegate).IsAssignableFrom(parameter.ParameterType));

        var fixture = Ek01Fixture();
        var activation = new TransitionActivation(
            new("repeat"),
            Object(
                fixture.Definition.Input,
                ("approved", ObservationValue.FromBool(true)),
                ("decision", ObservationValue.FromString("hold"))),
            TransitionObservationFrame.Sparse(
            [
                Entry(fixture.Plan, "status", ObservationValue.FromString("pending")),
                Entry(fixture.Plan, "eligible", ObservationValue.FromBool(true))
            ]));

        var first = TransitionReferenceInterpreter.Decide(fixture.Plan, activation);
        for (var iteration = 0; iteration < 64; iteration++)
            Assert.Equivalent(
                first,
                TransitionReferenceInterpreter.Decide(fixture.Plan, activation),
                strict: true);
        Assert.Equal(Enumerable.Range(0, first.Evidence.Trace.Length), first.Evidence.Trace.Select(static item => item.Sequence));
    }

    [Fact]
    public void Decide_FixedSeedEk01FullSparseAndConflictSemanticsRemainEquivalent()
    {
        const int seed = 158_108;
        var random = new Random(seed);
        var fixture = Ek01Fixture();
        for (var iteration = 0; iteration < 96; iteration++)
        {
            var isPending = random.Next(4) != 0;
            var approved = random.Next(2) == 0;
            var eligible = random.Next(2) == 0;
            var decisionValue = random.Next(2) == 0 ? "approve" : "hold";
            var input = Object(
                fixture.Definition.Input,
                ("approved", ObservationValue.FromBool(approved)),
                ("decision", ObservationValue.FromString(decisionValue)));
            var status = isPending ? "pending" : "closed";
            var state = Object(
                fixture.Definition.Observation,
                ("status", ObservationValue.FromString(status)),
                ("eligible", ObservationValue.FromBool(eligible)),
                ("caseId", ObservationValue.FromString("case-1")),
                ("unused", ObservationValue.FromString("unused")));
            List<TransitionObservationEntry> sparse =
            [Entry(fixture.Plan, "status", ObservationValue.FromString(status))];
            if (isPending && approved)
                sparse.Add(Entry(fixture.Plan, "eligible", ObservationValue.FromBool(eligible)));
            if (isPending && approved && eligible && decisionValue == "approve")
                sparse.Add(Entry(fixture.Plan, "caseId", ObservationValue.FromString("case-1")));

            var willCommit = isPending && approved && eligible;
            var conflict = willCommit && random.Next(3) == 0;
            PortableValue? freshState = null;
            List<TransitionObservationEntry>? freshSparse = null;
            if (willCommit)
            {
                var freshStatus = conflict ? "concurrently-changed" : status;
                freshState = Object(
                    fixture.Definition.Observation,
                    ("status", ObservationValue.FromString(freshStatus)),
                    ("eligible", ObservationValue.FromBool(eligible)),
                    ("caseId", ObservationValue.FromString("case-1")),
                    ("unused", ObservationValue.FromString("fresh-unused")));
                freshSparse =
                [
                    Entry(fixture.Plan, "status", ObservationValue.FromString(freshStatus)),
                    Entry(fixture.Plan, "eligible", ObservationValue.FromBool(eligible))
                ];
                if (decisionValue == "approve")
                    freshSparse.Add(Entry(fixture.Plan, "caseId", ObservationValue.FromString("case-1")));
            }

            var activationId = new ActivationId($"property-{iteration}");
            var full = TransitionReferenceInterpreter.DecideFullState(
                fixture.Plan,
                activationId,
                input,
                state,
                freshState);
            var sparseDecision = TransitionReferenceInterpreter.DecideSparse(
                fixture.Plan,
                activationId,
                input,
                sparse,
                freshSparse);

            Assert.Equivalent(full, sparseDecision, strict: true);
            Assert.Equal(conflict ? TransitionDecisionKind.Conflict : full.Kind, sparseDecision.Kind);
        }
    }

    static (CanonicalTransitionDefinition Definition, CompiledTransitionPlan Plan) Ek01Fixture()
    {
        var decisionContract = new ValueContract(new EnumTypeRef("Decision", ["approve", "hold"]));
        var input = ObjectContract(
            new("approved", BooleanContract.Type!),
            new("decision", decisionContract.Type!));
        var observation = ObjectContract(
            new("status", StringContract.Type!),
            new("eligible", BooleanContract.Type!),
            new("caseId", StringContract.Type!),
            new("unused", StringContract.Type!));
        ValueBindingId selected = new("selected-decision");
        var match = new MatchTransitionNode(
            new("decision-match"),
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            Expr.BoundValue(selected),
            decisionContract,
            [
                new(
                    new("approve-case"),
                    PortableValue.Concrete(decisionContract, ObservationValue.FromString("approve")),
                    Sequence(
                        "approve-body",
                        Set("approve-status", "status", Expr.Const("approved")),
                        new EmitTransitionNode(new("approved-event"), InteractionReference(), Expr.Field("caseId")),
                        Outcome("approve-outcome", TransitionOutcomeDisposition.Applied, "approved"))),
                new(
                    new("hold-case"),
                    PortableValue.Concrete(decisionContract, ObservationValue.FromString("hold")),
                    Sequence(
                        "hold-body",
                        Set("hold-status", "status", Expr.Const("held")),
                        Outcome("hold-outcome", TransitionOutcomeDisposition.Applied, "held")))
            ],
            new(new("invalid-decision"), Sequence(
                "invalid-decision-body",
                Outcome("invalid-decision-outcome", TransitionOutcomeDisposition.DomainRejected, "invalidDecision"))));
        var definition = new CanonicalTransitionDefinition(
            input,
            observation,
            StringContract,
            [new(new("pending-only"), Expr.Eq(Expr.Field("status"), Expr.Const("pending")), Expr.Const("notPending"))],
            Sequence(
                "root",
                new LetTransitionNode(new("bind-decision"), selected, decisionContract, Expr.Param("decision")),
                new ChoiceTransitionNode(
                    new("eligibility"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [new(new("eligible"), Expr.And(Expr.Param("approved"), Expr.Field("eligible")), Sequence("eligible-body", match))],
                    new(new("not-eligible"), Sequence(
                        "not-eligible-body",
                        Outcome("not-eligible-outcome", TransitionOutcomeDisposition.NoChange, "notEligible"))))),
            [new(new("status-valid"), Expr.Ne(Expr.Field("status"), Expr.Const("invalid")))]);
        return (definition, Compile(definition));
    }

    static ShapeGraph DerivedGraph()
    {
        var shape = new Shape(
            new("review"),
            [
                new(new("raw"), Int64Contract.Type!),
                new(
                    new("normalized"),
                    Int64Contract.Type!,
                    role: FieldRole.Computed,
                    mutability: FieldMutability.Computed,
                    compute: new(Expr.Add(Expr.Field("raw"), Expr.Const(1)))),
                new(
                    new("eligible"),
                    BooleanContract.Type!,
                    role: FieldRole.Computed,
                    mutability: FieldMutability.Computed,
                    compute: new(Expr.Gt(Expr.Field("normalized"), Expr.Const(10))))
            ]);
        return new(new("review-graph"), [shape]);
    }

    static TransitionMachineEdgeLink MachineLink(
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        string target) => new(
        machine,
        edge,
        Expr.Eq(Expr.Field("status"), Expr.Const("pending")),
        Expr.Eq(Expr.Field("status"), Expr.Const(target)),
        [new(FieldPath.FromField("status"), PortableValue.Concrete(StringContract, ObservationValue.FromString("approved")))]);

    static ExecutionDefinitionReference MachineReference(char fingerprintDigit = '1') => new(
        new("machine/review"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static ExecutionDefinitionReference InteractionReference() => new(
        new("interaction/reviewed"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('2', 64)));

    static CanonicalTransitionDefinition Definition(
        SequenceTransitionNode body,
        ValueContract? input = null,
        ValueContract? observation = null) => new(
        input ?? new(new ObjectTypeRef([])),
        observation ?? new(new ObjectTypeRef([])),
        StringContract,
        [],
        body);

    static CompiledTransitionPlan Compile(
        CanonicalTransitionDefinition definition,
        ShapeGraph? graph = null,
        TransitionMachineLinkCatalog? machineLinks = null)
    {
        var result = TransitionStaticCompiler.Compile(Document(definition), graph, machineLinks);
        Assert.True(result.IsSuccessful, Format(result.Validation));
        return Assert.IsType<CompiledTransitionPlan>(result.Plan);
    }

    static ExecutionDefinitionDocument Document(CanonicalTransitionDefinition definition) =>
        TransitionDefinitionDocuments.Create(
            new("transition/reference-interpreter"),
            new("revision/1"),
            definition,
            new(
                new("transition-reference-interpreter-tests", "1"),
                new("tests/execution-kernel/transition-reference-interpreter"),
                DocumentOrigin.Generated));

    static SequenceTransitionNode Sequence(string id, params TransitionNode[] nodes) => new(new(id), [.. nodes]);

    static OutcomeTransitionNode Outcome(
        string id,
        TransitionOutcomeDisposition disposition = TransitionOutcomeDisposition.NoChange,
        string value = "ok") => new(new(id), disposition, Expr.Const(value));

    static UpdateTransitionNode Set(string id, string path, Expr value) => new(
        new(id),
        FieldPath.FromField(path),
        new SetTransitionPatch(value));

    static ValueContract ObjectContract(params ObjectFieldTypeDef[] fields) => new(new ObjectTypeRef([.. fields]));

    static PortableValue Object(
        ValueContract contract,
        params (string Name, ObservationValue Value)[] fields) => PortableValue.Concrete(
        contract,
        ObservationValue.FromObject(fields.ToDictionary(static field => field.Name, static field => field.Value)));

    static PortableValue EmptyInput(ValueContract contract) =>
        PortableValue.Concrete(contract, ObservationValue.EmptyObject);

    static TransitionObservationEntry Entry(
        CompiledTransitionPlan plan,
        string path,
        ObservationValue value)
    {
        var fieldPath = FieldPath.Parse(path);
        return new(
            TransitionObservationAccess.At(fieldPath),
            PortableValue.Concrete(ContractAt(plan, path), value));
    }

    static ValueContract ContractAt(CompiledTransitionPlan plan, string path)
    {
        var segments = FieldPath.Parse(path).Segments;
        var current = plan.Definition.Observation;
        foreach (var segment in segments)
        {
            var objectType = Assert.IsType<ObjectTypeRef>(current.Type);
            var field = Assert.Single(objectType.Fields, candidate => candidate.Name == segment.Segment);
            current = new(
                field.Type,
                cardinality: field.Cardinality,
                presence: field.Presence,
                nullability: field.Nullability);
        }
        return current;
    }

    static void AssertNoCommitArtifacts(TransitionDecision decision)
    {
        Assert.Empty(decision.Patch);
        Assert.Empty(decision.Emissions);
        Assert.Empty(decision.MachineMovements);
        Assert.False(decision.GuaranteeDemands.CommitRequired);
    }

    static void AssertTraceOrder(
        TransitionDecision decision,
        TransitionTraceEventKind first,
        TransitionTraceEventKind second,
        TransitionTraceEventKind third)
    {
        var firstIndex = decision.Evidence.Trace.First(item => item.Kind == first).Sequence;
        var secondEvents = decision.Evidence.Trace.Where(item => item.Kind == second).ToArray();
        Assert.NotEmpty(secondEvents);
        var thirdIndex = decision.Evidence.Trace.First(item => item.Kind == third).Sequence;
        Assert.True(firstIndex < secondEvents[0].Sequence);
        Assert.True(secondEvents[^1].Sequence < thirdIndex);
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}

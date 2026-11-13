using Cohesive.Assurance;
using Cohesive.Authoring;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Assurance.Tests;

public sealed class ScenarioAssuranceTests
{
    public sealed record Driver(bool Active, bool OriginalResolved, int RemainingBlockers);
    public sealed record Parameters(int Blockers, int Noise);
    static ClaimReference R(string name) => new(new("fixture://" + name), "1", name);
    static readonly ClaimReference Mapping = R("atomic-driver-observation/v1");
    static ScenarioAssuranceBinding Binding(string interpreter = "dq/faulty/v1") =>
        new(Mapping, R("sequential-atomic-actor/closed-schedule"), R(interpreter), interpreter, "driver");

    static ClaimDocument Safety()
    {
        var driver = new ExpressionAuthoring().Input<Driver>("driver");
        return Claim.Define("dq.all-gates", "1", R("driver"), Mapping, R("source"), R("config"))
            .AtEveryCommittedState(R("single-driver"), InitialStateSelection.Include)
            .Require(driver, ClaimInputRole.State, d => !d.Active || d.RemainingBlockers == 0).Build();
    }

    static (ScenarioWorldSnapshot World, GraphShapeId Shape) Scenario(Parameters parameters)
    {
        var drivers = Simulation.Define<Driver>(b => b
            .Member(d => d.Active, Gen.Constant(false))
            .Member(d => d.OriginalResolved, Gen.Constant(false))
            .Member(d => d.RemainingBlockers, Gen.Constant(parameters.Blockers)));
        var world = Simulation.DefineWorld("dq/world", "1", b => b
            .Population("drivers", 1, drivers).Exemplar("driver", "drivers", 0));
        var manifest = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 42);
        var scenario = Simulation.DefineScenario("dq/reactivation", "1", manifest, DateTimeOffset.UnixEpoch, b =>
        {
            b.Actor("driver", "driver").Operation<bool, bool>("resolve").Operation<bool, bool>("reactivate").Operation<bool, bool>("noise");
            for (var i = 0; i < parameters.Noise; i++)
                b.Action("noise-" + i, TimeSpan.FromTicks(i), "driver", "noise", true);
            b.Action("resolve-original", TimeSpan.FromTicks(parameters.Noise), "driver", "resolve", true);
            b.Action("reactivate", TimeSpan.FromTicks(parameters.Noise + 1), "driver", "reactivate", true);
        });
        return (ScenarioWorldSnapshot.FromCoreWorld(ScenarioDefinitionDocument.FromDefinition(scenario)), drivers.OutputShape);
    }

    sealed class Candidate(GraphShapeId shape, bool repaired = false, bool yield = false) : IScenarioActionInterpreter
    {
        public string Identity => repaired ? "dq/repaired/v2" : "dq/faulty/v1";
        public int Calls { get; private set; }
        public async ValueTask<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken cancellationToken)
        {
            if (yield) await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var current = context.ActorSnapshot.Observation.Value.Deserialize<Driver>()!;
            var next = context.Operation.Id switch
            {
                "resolve" => current with { OriginalResolved = true, RemainingBlockers = Math.Max(0, current.RemainingBlockers - 1) },
                "reactivate" => current with { Active = repaired ? current.RemainingBlockers == 0 : current.OriginalResolved },
                _ => current
            };
            var output = PortableValue.Concrete(context.Operation.Output, ObservationValue.FromBool(true));
            return current == next ? ScenarioActionResult.Unchanged(output)
                : new(output, [ScenarioActorStateChange.Replace(context.ActorSnapshot,
                    Observation.Create(shape, ObservationValue.FromObject(next)))]);
        }
    }

    [Fact]
    public async Task GeneratedDefectShrinksToTwoBlockersAndReplaysAgainstFaultyAndRepairedCandidates()
    {
        var claim = Safety();
        var generator = Simulation.Define<Parameters>(b => b.Member(p => p.Blockers, Gen.Int32(0, 20))
            .Member(p => p.Noise, Gen.Int32(0, 20))).Compile();
        var search = generator.CheckProperty(seed: 42, evaluate: parameters =>
        {
            var (world, shape) = Scenario(parameters);
            var execution = ScenarioAssurance.ExecuteAsync(claim, world, new Candidate(shape), Binding());
            // This test-only candidate is entirely synchronous; never block a pending task in property search.
            Assert.True(execution.IsCompletedSuccessfully);
            return execution.GetAwaiter().GetResult().ToPropertyEvaluation();
        });
        Assert.Equal(PropertyCaseRunStatus.CounterexampleFound, search.Status);
        var counterexample = Assert.IsType<PropertyCase>(search.BestCounterexample);
        var minimal = generator.ReplayPropertyCase(counterexample.Replay.ToToken());
        Assert.Equal(new Parameters(Blockers: 2, Noise: 0), minimal);
        Assert.NotEmpty(counterexample.Replay.ShrinkChoices);

        var (world, shape) = Scenario(minimal);
        var faulty = new Candidate(shape, yield: true);
        var first = await ScenarioAssurance.ExecuteAsync(claim, world, faulty, Binding());
        Assert.Equal(2, faulty.Calls);
        Assert.Equal(ScenarioAssuranceMethod.CandidateExecution, first.Method);
        Assert.Equal(ClaimVerdict.Violated, first.Evaluation.Verdict);
        Assert.Contains(first.Evaluation.Alternatives[0].Obligations,
            x => x.Verdict == ClaimVerdict.Violated && x.Occurrence == "action/reactivate/actor/driver");
        Assert.NotEmpty(first.Evaluation.Claim.SourceMap);

        var retained = ScenarioExecutionTraceJsonSerializer.Deserialize(ScenarioExecutionTraceJsonSerializer.Serialize(first.Trace));
        var reevaluated = ScenarioAssurance.Reevaluate(claim, retained, Binding());
        Assert.Equal(ScenarioAssuranceMethod.TraceReevaluation, reevaluated.Method);
        Assert.Equal(first.Evaluation.Serialize(), reevaluated.Evaluation.Serialize());
        Assert.Equal(2, faulty.Calls); // Re-evaluation did not invoke any candidate operation.
        var reproduced = await ScenarioAssurance.ReplayAsync(claim, retained, faulty, Binding());
        Assert.Equal(4, faulty.Calls);
        Assert.Equal(first.Trace.Fingerprint, reproduced.Trace.Fingerprint);
        Assert.Equal(ClaimVerdict.Violated, reproduced.Evaluation.Verdict);

        var repaired = new Candidate(shape, repaired: true, yield: true);
        var corrected = await ScenarioAssurance.ReplayAsync(claim, retained, repaired, Binding(repaired.Identity));
        Assert.Equal(2, repaired.Calls);
        Assert.Equal(ClaimVerdict.Satisfied, corrected.Evaluation.Verdict);
        Assert.NotEqual(first.Trace.Fingerprint, corrected.Trace.Fingerprint);
        Assert.Equal(first.Evaluation.Claim.Fingerprint, corrected.Evaluation.Claim.Fingerprint);
        Assert.Equal(ClaimVerdict.Violated, reevaluated.Evaluation.Verdict);

        // The general evidence model preserves the target boundary and never promotes sampled success to proof.
        ClaimAssessment AssessRun(ScenarioAssuranceResult run)
        {
            var subject = new EvidenceSubject(EvidenceTargetKind.Implementation, run.Binding.Target,
                R("candidate-configuration"), R("all-reactivation-inputs"), ProofObligation.Realization);
            var evidence = ClaimEvidence.FromTrace(run.Evaluation, subject, R("atomic-mapping-config"),
                dependenciesComplete: true);
            return ClaimAssessment.Evaluate(evidence, R("review-policy"),
                [EvidenceAcceptance.Of(evidence)], DateTimeOffset.UnixEpoch);
        }
        Assert.Equal(ProofState.Disproven, AssessRun(first).State);
        Assert.Equal(ProofState.Unknown, AssessRun(corrected).State);
        Assert.Equal(ProofState.Unknown, ClaimAssessment.Reassess(AssessRun(corrected).Serialize()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedArtifactRechecksWithoutExecutingAndPreservesExactEvidence(bool repaired)
    {
        var (world, shape) = Scenario(new(2, 0));
        var candidate = new Candidate(shape, repaired: repaired);
        var result = await ScenarioAssurance.ExecuteAsync(Safety(), world, candidate, Binding(candidate.Identity));
        var json = ScenarioAssuranceArtifact.Serialize(result);
        var restored = ScenarioAssuranceArtifact.Reevaluate(json);
        Assert.Equal(2, candidate.Calls);
        Assert.Equal(ScenarioAssuranceMethod.TraceReevaluation, restored.Method);
        Assert.Equal(result.Evaluation.Serialize(), restored.Evaluation.Serialize());
        Assert.Equal(result.Trace.Fingerprint, restored.Trace.Fingerprint);
        Assert.Equal(result.Binding, restored.Binding);
        Assert.Equal(json, ScenarioAssuranceArtifact.Serialize(result));
        var recheckJson = ScenarioAssuranceArtifact.Serialize(restored);
        Assert.NotEqual(json, recheckJson); // The recorded method is part of the artifact.
        Assert.Equal(recheckJson, ScenarioAssuranceArtifact.Serialize(ScenarioAssuranceArtifact.Reevaluate(recheckJson)));
    }

    [Fact]
    public async Task RetainedBudgetExhaustionRemainsInconclusive()
    {
        var (world, shape) = Scenario(new(2, 0));
        var result = await ScenarioAssurance.ExecuteAsync(Safety(), world, new Candidate(shape), Binding(),
            limits: new TraceEvaluationLimits(MaxObservations: 1));
        var restored = ScenarioAssuranceArtifact.Reevaluate(ScenarioAssuranceArtifact.Serialize(result));
        Assert.Equal(AssuranceAttemptStatus.BudgetExceeded, restored.Evaluation.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, restored.Evaluation.Verdict);
        Assert.Equal(PropertyCaseDisposition.Discarded, restored.ToPropertyEvaluation().Disposition);
        Assert.Equal(result.Evaluation.Serialize(), restored.Evaluation.Serialize());
    }

    [Fact]
    public async Task RetainedArtifactRejectsDuplicateAndUnknownEnvelopeFields()
    {
        var (world, shape) = Scenario(new(2, 0));
        var result = await ScenarioAssurance.ExecuteAsync(Safety(), world, new Candidate(shape), Binding());
        var json = ScenarioAssuranceArtifact.Serialize(result);
        Assert.Throws<System.Text.Json.JsonException>(() => ScenarioAssuranceArtifact.Reevaluate(
            "{\"schema\":\"cohesive-simulation-assurance/v1\"," + json[1..]));
        Assert.Throws<System.Text.Json.JsonException>(() => ScenarioAssuranceArtifact.Reevaluate(
            "{\"unexpected\":true," + json[1..]));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("evaluation")]
    [InlineData("trace")]
    [InlineData("schema")]
    [InlineData("recordedMethod")]
    [InlineData("missing")]
    public async Task RetainedArtifactRejectsMismatchedOrMalformedEvidence(string tamper)
    {
        var (world, shape) = Scenario(new(2, 0));
        var faulty = await ScenarioAssurance.ExecuteAsync(Safety(), world, new Candidate(shape), Binding());
        var repaired = await ScenarioAssurance.ExecuteAsync(Safety(), world, new Candidate(shape, repaired: true), Binding("dq/repaired/v2"));
        var document = System.Text.Json.Nodes.JsonNode.Parse(ScenarioAssuranceArtifact.Serialize(faulty))!;
        var other = System.Text.Json.Nodes.JsonNode.Parse(ScenarioAssuranceArtifact.Serialize(repaired))!;
        if (tamper == "missing") document.AsObject().Remove("trace");
        else if (tamper is "binding" or "evaluation" or "trace") document[tamper] = other[tamper]!.DeepClone();
        else document[tamper] = "unsupported";
        Assert.Throws<System.Text.Json.JsonException>(() => ScenarioAssuranceArtifact.Reevaluate(document.ToJsonString()));
    }

    [Theory]
    [InlineData(AssuranceAttemptStatus.Completed, ClaimVerdict.Violated, PropertyCaseDisposition.Failed)]
    [InlineData(AssuranceAttemptStatus.Completed, ClaimVerdict.Satisfied, PropertyCaseDisposition.Discarded)]
    [InlineData(AssuranceAttemptStatus.Completed, ClaimVerdict.Inconclusive, PropertyCaseDisposition.Discarded)]
    [InlineData(AssuranceAttemptStatus.BudgetExceeded, ClaimVerdict.Violated, PropertyCaseDisposition.Discarded)]
    public async Task PropertyProjectionDoesNotLetVacuityOverrideADefinitiveViolation(
        AssuranceAttemptStatus status, ClaimVerdict verdict, PropertyCaseDisposition expected)
    {
        var (world, shape) = Scenario(new(2, 0));
        var run = await ScenarioAssurance.ExecuteAsync(Safety(), world, new Candidate(shape), Binding());
        // Synthetic projection fixture: current batch semantics cannot emit a definitive violation
        // together with a satisfied/vacuous compatible alternative. Test precedence without weakening that rule.
        var evaluation = run.Evaluation with { Status = status, Verdict = verdict,
            Alternatives = [.. run.Evaluation.Alternatives, new(R("vacuous"), ClaimVerdict.Satisfied, [], 0, true, true)] };
        var result = new ScenarioAssuranceResult(run.Method, run.Binding, run.Trace, evaluation);
        Assert.Equal(expected, result.ToPropertyEvaluation().Disposition);
    }

    [Fact]
    public async Task BindingMismatchAndUnsupportedFormsFailBeforeRunningTheCandidate()
    {
        var (world, shape) = Scenario(new(2, 0));
        var candidate = new Candidate(shape);
        await Assert.ThrowsAsync<ArgumentException>(() => ScenarioAssurance.ExecuteAsync(Safety(), world, candidate,
            Binding() with { Interpreter = "unrelated" }));
        var author = new ExpressionAuthoring();
        var a = author.Input<Driver>("a"); var b = author.Input<Driver>("b");
        var history = Claim.Define("history", "1", R("driver"), Mapping, R("source"), R("config"))
            .RequireEarlier(R("a"), R("b")).Require(a, ClaimInputRole.Trigger, b, ClaimInputRole.Witness,
                (x, y) => x.Active == y.Active).Build();
        await Assert.ThrowsAsync<NotSupportedException>(() => ScenarioAssurance.ExecuteAsync(history, world, candidate, Binding()));
        Assert.Equal(0, candidate.Calls);
    }

    [Fact]
    public async Task UnchangedTransitionIsUnknownRatherThanVacuousSuccess()
    {
        var (world, shape) = Scenario(new(2, 1));
        var value = new ExpressionAuthoring().Input<Driver>("after");
        var claim = Claim.Define("no-op", "1", R("driver"), Mapping, R("source"), R("config"))
            .ForEachCommitted(R("noise")).Require(value, ClaimInputRole.After, d => !d.Active).Build();
        var result = await ScenarioAssurance.ExecuteAsync(claim, world, new Candidate(shape), Binding() with { OperationId = "noise" });
        Assert.Equal(ClaimVerdict.Inconclusive, result.Evaluation.Verdict);
        Assert.Equal(PropertyCaseDisposition.Discarded, result.ToPropertyEvaluation().Disposition);
    }

    [Fact]
    public async Task TransitionContractUsesTheExactReactivationPrestateAndCommand()
    {
        var (world, shape) = Scenario(new(2, 0));
        var author = new ExpressionAuthoring();
        var before = author.Input<Driver>("before");
        var command = new ClaimInput(new("command"), ClaimInputRole.Command, new(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var after = author.Input<Driver>("after");
        var claim = Claim.Define("reactivate.all-gates", "1", R("driver"), Mapping, R("source"), R("config"))
            .ForEachCommitted(R("reactivate"))
            .Require(Expr.And(Expr.BoundValue(command.Id), Expr.Or(Expr.Not(Expr.Field(after.Id, "Active")),
                Expr.Eq(Expr.Field(before.Id, "RemainingBlockers"), Expr.Const(0)))),
                [new(before.Id, ClaimInputRole.Before, new(before.Type)), command,
                 new(after.Id, ClaimInputRole.After, new(after.Type))]).Build();
        var result = await ScenarioAssurance.ExecuteAsync(claim, world, new Candidate(shape), Binding() with { OperationId = "reactivate" });
        Assert.Equal(ClaimVerdict.Violated, result.Evaluation.Verdict);
        Assert.Single(result.Evaluation.Alternatives[0].Obligations);
    }

    [Fact]
    public async Task UnexercisedOperationIsReportedAndDoesNotCountAsAPassedPropertyCase()
    {
        var (world, shape) = Scenario(new(2, 0));
        var value = new ExpressionAuthoring().Input<Driver>("after");
        var claim = Claim.Define("noise", "1", R("driver"), Mapping, R("source"), R("config"))
            .ForEachCommitted(R("noise")).Require(value, ClaimInputRole.After, d => !d.Active).Build();
        var result = await ScenarioAssurance.ExecuteAsync(claim, world, new Candidate(shape), Binding() with { OperationId = "noise" });
        Assert.True(result.Evaluation.Alternatives[0].Vacuous);
        Assert.Equal(PropertyCaseDisposition.Discarded, result.ToPropertyEvaluation().Disposition);
    }

    [Fact]
    public async Task ReEvaluationRejectsAnotherContractOrCandidateIdentity()
    {
        var (world, shape) = Scenario(new(2, 0));
        var result = await ScenarioAssurance.ExecuteAsync(Safety(), world, new Candidate(shape), Binding());
        Assert.Throws<ArgumentException>(() => ScenarioAssurance.Reevaluate(Safety(), result.Trace,
            Binding() with { ObservationContract = R("other") }));
        Assert.Throws<ArgumentException>(() => ScenarioAssurance.Reevaluate(Safety(), result.Trace, Binding("dq/repaired/v2")));
        Assert.Equal(ClaimVerdict.Violated, result.Evaluation.Verdict);
    }

    [Fact]
    public void AssuranceAndSimulationRemainIndependentCorePackages()
    {
        Assert.DoesNotContain(typeof(Claim).Assembly.GetReferencedAssemblies(), x => x.Name!.Contains("Simulation"));
        Assert.DoesNotContain(typeof(ScenarioRunner).Assembly.GetReferencedAssemblies(), x => x.Name!.Contains("Assurance"));
    }

    [Fact]
    public async Task CancellationCannotProduceSuccessfulEvidence()
    {
        var (world, shape) = Scenario(new(2, 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScenarioAssurance.ExecuteAsync(Safety(), world,
            new Candidate(shape), Binding(), cancellationToken: cancellation.Token));
    }
}

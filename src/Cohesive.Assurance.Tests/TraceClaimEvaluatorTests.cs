using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Authoring;
using Cohesive.Model;

namespace Cohesive.Assurance.Tests;

public class TraceClaimEvaluatorTests
{
    public sealed record Inventory(decimal OnHand, decimal Reserved);
    public sealed record Order(decimal Quantity);
    public sealed record OrderEvent(string OrderId);
    public sealed record Flag(bool Ready);
    public sealed record Numbers(decimal[] Values);

    static ClaimReference R(string name, string revision = "1") => new(new("test://" + name), revision, "hash:" + name + revision);
    static readonly ClaimReference Selection = R("selection"), Witness = R("witness"), Contract = R("contract");
    static Claim.Declaration Start() => Claim.Define("orders.claim", "1", R("orders"), Contract, R("source"), R("config"));
    static ClaimDocument Invariant(InitialStateSelection initial = InitialStateSelection.Include)
    {
        var stock = new ExpressionAuthoring().Input<Inventory>("stock");
        return Start().AtEveryCommittedState(Selection, initial)
            .Require(stock, ClaimInputRole.State, s => s.Reserved <= s.OnHand).Build();
    }
    static ClaimDocument Events(bool history = false, long duration = 10)
    {
        var author = new ExpressionAuthoring();
        var trigger = author.Input<OrderEvent>("trigger");
        var witness = author.Input<OrderEvent>("witness");
        return (history ? Start().RequireEarlier(Selection, Witness)
            : Start().RequireWithin(Selection, Witness, TimeSpan.FromTicks(duration)))
            .Require(trigger, ClaimInputRole.Trigger, witness, ClaimInputRole.Witness, (t, w) => t.OrderId == w.OrderId).Build();
    }
    static SemanticObservation Stock(string id, long step, decimal reserved, long? time = null,
        SemanticObservationKind kind = SemanticObservationKind.CommittedState) => new(id, "p1", step, time ?? step,
            Selection, kind, ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
                .Add(ClaimInputRole.State, ObservationValue.FromObject(new Inventory(10m, reserved))));
    static SemanticObservation Event(string id, long step, long time, bool witness = false, string order = "o1", string partition = "p1") =>
        new(id, partition, step, time, witness ? Witness : Selection, SemanticObservationKind.Event,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty, EventPayload: ObservationValue.FromObject(new OrderEvent(order)));
    static ObservationCompleteness Frontier(ClaimReference selector, ObservationClosure closure = ObservationClosure.Open,
        long time = 100, long step = 100, string partition = "p1") => new(selector, partition, step, time, closure, R("frontier-" + partition));
    static TraceObservationBatch Batch(SemanticObservation[] observations, ObservationCompleteness[]? frontiers = null,
        bool includeWitness = false, string[]? witnessPartitions = null)
    {
        List<ObservationRequirement> requirements = [new(Selection, ["p1"])];
        if (includeWitness) requirements.Add(new(Witness, witnessPartitions is null ? ["p1"] : [.. witnessPartitions]));
        return new(new(Contract, R("atomic-scope"), [.. requirements]), R("candidate"), R("epoch"),
            [new(R("alternative"), [.. observations], frontiers is null ? [] : [.. frontiers])], EnumerationCompleteness.Complete, R("enumeration"));
    }
    static TraceEvaluation Evaluate(ClaimDocument claim, TraceObservationBatch batch, TraceEvaluationLimits? limits = null) =>
        TraceClaimEvaluator.Evaluate(claim, batch, limits);
    static ClaimObligationResult Only(TraceEvaluation result) => Assert.Single(Assert.Single(result.Alternatives).Obligations);

    [Fact]
    public void ReservationViolationIsABadPrefixEvenWithoutClosure()
    {
        var result = Evaluate(Invariant(), Batch([Stock("first", 1, 7), Stock("oversell", 2, 12)]));
        Assert.Equal(ClaimVerdict.Violated, result.Verdict);
        Assert.Equal("oversell", result.Alternatives[0].Obligations.Single(x => x.Verdict == ClaimVerdict.Violated).Occurrence);
    }

    [Theory]
    [InlineData(ObservationClosure.Open, ClaimVerdict.Inconclusive)]
    [InlineData(ObservationClosure.Closed, ClaimVerdict.Satisfied)]
    public void NonViolatingInvariantNeedsExplicitScopeClosure(ObservationClosure closure, ClaimVerdict verdict)
    {
        var result = Evaluate(Invariant(), Batch([Stock("stock", 1, 7)], [Frontier(Selection, closure)]));
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, Only(result).Verdict);
    }

    [Theory]
    [InlineData(ObservationClosure.Open, ClaimVerdict.Inconclusive, false)]
    [InlineData(ObservationClosure.Closed, ClaimVerdict.Satisfied, true)]
    public void EmptySelectionReportsVacuityOnlyWhenClosed(ObservationClosure closure, ClaimVerdict verdict, bool vacuous)
    {
        var result = Evaluate(Invariant(), Batch([], [Frontier(Selection, closure)]));
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(vacuous, result.Alternatives[0].Vacuous);
    }

    [Theory]
    [InlineData(InitialStateSelection.Include, ClaimVerdict.Violated)]
    [InlineData(InitialStateSelection.Exclude, ClaimVerdict.Satisfied)]
    public void InitialStateSelectionIsExplicit(InitialStateSelection initial, ClaimVerdict verdict)
    {
        var initialStock = Stock("initial", 0, 12, kind: SemanticObservationKind.InitialState);
        Assert.Equal(verdict, Evaluate(Invariant(initial), Batch([initialStock], [Frontier(Selection, ObservationClosure.Closed)])).Verdict);
    }

    [Fact]
    public void MissingPopulationOrPartitionCannotBecomeAnEmptySuccessfulSelection()
    {
        var batch = Batch([], [Frontier(Selection, ObservationClosure.Closed)]);
        batch = batch with { Contract = batch.Contract with { Requirements = [new(Selection, ["p1", "p2"])] } };
        var result = Evaluate(Invariant(), batch);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.False(result.Alternatives[0].Vacuous);
    }

    [Fact]
    public void TransitionContractEvaluatesTheSameCommitBeforeCommandAndAfter()
    {
        var a = new ExpressionAuthoring();
        var before = a.Input<Inventory>("before"); var command = a.Input<Order>("command"); var after = a.Input<Inventory>("after");
        var claim = Start().ForEachCommitted(Selection).Require(before, command, after,
            (b, c, n) => c.Quantity > 0m && n.Reserved == b.Reserved + c.Quantity).Build();
        var commit = new SemanticObservation("commit", "p1", 1, 1, Selection, SemanticObservationKind.Commit,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
                .Add(ClaimInputRole.Before, ObservationValue.FromObject(new Inventory(10, 2)))
                .Add(ClaimInputRole.Command, ObservationValue.FromObject(new Order(3)))
                .Add(ClaimInputRole.After, ObservationValue.FromObject(new Inventory(10, 4))));
        Assert.Equal(ClaimVerdict.Violated, Evaluate(claim, Batch([commit])).Verdict);
        var abort = commit with { Kind = SemanticObservationKind.AbortedAttempt };
        Assert.Empty(Evaluate(claim, Batch([abort])).Alternatives[0].Obligations);
        var unknown = commit with { Kind = SemanticObservationKind.UnknownOutcome };
        Assert.Equal(ClaimVerdict.Inconclusive, Evaluate(claim, Batch([unknown], [Frontier(Selection, ObservationClosure.Closed)])).Verdict);
    }

    [Fact]
    public void HistoricalWitnessIsStrictlyEarlierAndNonConsuming()
    {
        var result = Evaluate(Events(history: true), Batch([
            Event("capture", 0, 0, witness: true), Event("ship1", 1, 0), Event("ship2", 2, 0)], includeWitness: true));
        Assert.All(result.Alternatives[0].Obligations, x => Assert.Equal("capture", x.WitnessOccurrence));
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict); // Open enclosing stream.
    }

    [Theory]
    [InlineData(false, ClaimVerdict.Inconclusive)]
    [InlineData(true, ClaimVerdict.Violated)]
    public void SameStepHistoryDoesNotCountAndAbsenceRequiresCompleteness(bool complete, ClaimVerdict verdict)
    {
        var result = Evaluate(Events(history: true), Batch([Event("capture", 1, 1, witness: true), Event("ship", 1, 1)],
            complete ? [Frontier(Witness)] : [], includeWitness: true));
        Assert.Equal(verdict, Only(result).Verdict);
    }

    [Theory]
    [InlineData(0, ClaimVerdict.Satisfied)]
    [InlineData(10, ClaimVerdict.Satisfied)]
    [InlineData(11, ClaimVerdict.Violated)]
    public void ResponseAllowsSameStepAndIncludesDeadline(long elapsed, ClaimVerdict verdict)
    {
        var result = Evaluate(Events(), Batch([Event("cancel", 1, 10), Event("release", elapsed == 0 ? 1 : 2, 10 + elapsed, witness: true)],
            [Frontier(Witness, time: 20)], includeWitness: true));
        Assert.Equal(verdict, Only(result).Verdict);
    }

    [Fact]
    public void EarlierOutcomeDoesNotSatisfyResponse()
    {
        var result = Evaluate(Events(), Batch([Event("release", 0, 0, witness: true), Event("cancel", 1, 0)],
            [Frontier(Witness)], includeWitness: true));
        Assert.Equal(ClaimVerdict.Violated, Only(result).Verdict);
    }

    [Fact]
    public void ElapsedTimeWithoutWatermarkDoesNotRefuteResponse()
    {
        var result = Evaluate(Events(), Batch([Event("cancel", 1, 0), Event("unrelated", 2, 1000, witness: true, order: "other")], includeWitness: true));
        Assert.Equal(ClaimVerdict.Inconclusive, Only(result).Verdict);
        Assert.Equal("assurance.response.pending", Only(result).Reason);
    }

    [Theory]
    [InlineData(9, ClaimVerdict.Inconclusive)]
    [InlineData(10, ClaimVerdict.Violated)]
    public void EveryRequiredPartitionMustCloseTheInclusiveDeadline(long secondWatermark, ClaimVerdict verdict)
    {
        var result = Evaluate(Events(), Batch([Event("cancel", 1, 0)],
            [Frontier(Witness, time: 10), Frontier(Witness, time: secondWatermark, partition: "p2")],
            includeWitness: true, witnessPartitions: ["p1", "p2"]));
        Assert.Equal(verdict, Only(result).Verdict);
    }

    [Fact]
    public void ClosedShortScenarioStillCannotCloseALaterDeadline()
    {
        var result = Evaluate(Events(), Batch([Event("cancel", 1, 0)],
            [Frontier(Selection, ObservationClosure.Closed), Frontier(Witness, ObservationClosure.Closed, time: 5)], includeWitness: true));
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void DuplicateDeliveryDoesNotDuplicateObligationsOrConsumeWitnesses()
    {
        var trigger = Event("cancel", 1, 1);
        var result = Evaluate(Events(), Batch([trigger, Event("cancel2", 2, 2), trigger, Event("release", 3, 3, witness: true)],
            [Frontier(Selection, ObservationClosure.Closed)], includeWitness: true));
        Assert.Equal(ClaimVerdict.Satisfied, result.Verdict);
        Assert.Equal(2, result.Alternatives[0].Obligations.Length);
        Assert.Equal(1, result.Alternatives[0].DuplicateDeliveries);
        Assert.All(result.Alternatives[0].Obligations, x => Assert.Equal("release", x.WitnessOccurrence));
    }

    [Fact]
    public void ContradictoryDuplicatesAndOutOfOrderStepsAreInvalid()
    {
        var original = Stock("same", 1, 1);
        var conflicting = Stock("same", 1, 12);
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, Evaluate(Invariant(), Batch([original, conflicting])).Status);
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, Evaluate(Invariant(), Batch([Stock("later", 2, 1), original])).Status);
    }

    [Fact]
    public void MissingInputsAreNotFalseOrNull()
    {
        var missing = Stock("partial", 1, 1) with { Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty };
        var result = Evaluate(Invariant(), Batch([missing], [Frontier(Selection, ObservationClosure.Closed)]));
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.Contains(result.Diagnostics, d => d.Code.Contains("RuntimeInputUnavailable"));
    }

    [Fact]
    public void MissingCorrelationCannotEstablishAbsenceEvenWithCompleteHistory()
    {
        var witness = Event("capture", 0, 0, witness: true) with { EventPayload = null };
        var result = Evaluate(Events(history: true), Batch([witness, Event("ship", 1, 1)], [Frontier(Witness)], includeWitness: true));
        Assert.Equal(ClaimVerdict.Inconclusive, Only(result).Verdict);
    }

    [Theory]
    [InlineData(true, ClaimVerdict.Satisfied)]
    [InlineData(false, ClaimVerdict.Violated)]
    public void JointFiniteCompletionsPreserveRepeatedPredicateCorrelation(bool tautology, ClaimVerdict verdict)
    {
        var input = new ExpressionAuthoring().Input<Flag>("flag");
        var builder = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include);
        var claim = (tautology ? builder.Require(input, ClaimInputRole.State, x => x.Ready || !x.Ready)
            : builder.Require(input, ClaimInputRole.State, x => x.Ready && !x.Ready)).Build();
        var batch = FlagAlternatives();
        Assert.Equal(verdict, Evaluate(claim, batch).Verdict);
        Assert.Equal(ClaimVerdict.Inconclusive, Evaluate(claim, batch with { Enumeration = EnumerationCompleteness.Partial }).Verdict);
    }

    [Fact]
    public void AlternativeMappingsAreNeverChosenForAFavorableVerdict()
    {
        var input = new ExpressionAuthoring().Input<Flag>("flag");
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include).Require(input, ClaimInputRole.State, x => x.Ready).Build();
        var result = Evaluate(claim, FlagAlternatives());
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.Contains(result.Alternatives, x => x.Verdict == ClaimVerdict.Satisfied);
        Assert.Contains(result.Alternatives, x => x.Verdict == ClaimVerdict.Violated);
    }

    static TraceObservationBatch FlagAlternatives()
    {
        var batch = Batch([], [Frontier(Selection, ObservationClosure.Closed)]);
        return batch with { Alternatives = [.. new[] { false, true }.Select(value => new TraceObservationAlternative(R("flag-" + value),
            [new("flag", "p1", 1, 1, Selection, SemanticObservationKind.CommittedState,
                ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.State, ObservationValue.FromObject(new Flag(value))))],
            batch.Alternatives[0].Completeness))] };
    }

    [Fact]
    public void WholeClaimLiftingPreservesCorrelationAcrossObligations()
    {
        var input = new ExpressionAuthoring().Input<Flag>("flag");
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include).Require(input, ClaimInputRole.State, x => x.Ready).Build();
        var batch = FlagAlternatives();
        var alternatives = batch.Alternatives.Select(a => a with { Observations = [a.Observations[0], a.Observations[0] with {
            Occurrence = "opposite", Step = 2, Ticks = 2,
            Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.State,
                ObservationValue.FromObject(new Flag(!a.Observations[0].Values[ClaimInputRole.State].Fields!["Ready"].Bool))) }] }).ToImmutableArray();
        Assert.Equal(ClaimVerdict.Violated, Evaluate(claim, batch with { Alternatives = alternatives }).Verdict);
    }

    [Fact]
    public void EmptyAlternativeDomainIsADiagnosticNotProof()
    {
        var result = Evaluate(Invariant(), Batch([]) with { Alternatives = [] });
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void ExactObservationRevisionMismatchIsInvalid()
    {
        var batch = Batch([]);
        batch = batch with { Contract = batch.Contract with { Reference = R("contract", "2") } };
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, Evaluate(Invariant(), batch).Status);
    }

    [Fact]
    public void MissingSelectorMappingIsUnsupported()
    {
        Assert.Equal(AssuranceAttemptStatus.Unsupported, Evaluate(Events(), Batch([])).Status);
    }

    [Fact]
    public void BudgetExhaustionCannotProduceSuccessOrHideRemainingObligations()
    {
        var result = Evaluate(Invariant(), Batch([Stock("one", 1, 1), Stock("two", 2, 12)], [Frontier(Selection, ObservationClosure.Closed)]),
            new(MaxPredicateEvaluations: 1));
        Assert.Equal(AssuranceAttemptStatus.BudgetExceeded, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.Equal(2, result.Alternatives[0].Obligations.Length);
        var alternatives = Evaluate(Invariant(), FlagAlternatives(), new(MaxAlternatives: 1));
        Assert.Equal(AssuranceAttemptStatus.BudgetExceeded, alternatives.Status);
    }

    [Fact]
    public void IneligibleCandidatesDoNotConsumeComparisonBudget()
    {
        var result = Evaluate(Events(), Batch([Event("early1", 0, 0, witness: true), Event("early2", 1, 1, witness: true), Event("cancel", 2, 2)],
            includeWitness: true), new(MaxEventCandidateVisits: 1));
        Assert.Equal(AssuranceAttemptStatus.Completed, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void TwoThousandNarrowWindowsNeedOnlyTwoThousandComparisons()
    {
        const int count = 2000;
        var observations = Enumerable.Range(0, count).SelectMany(i => new[]
        {
            Event($"trigger-{i}", i * 2, i * 20, order: $"order-{i}"),
            Event($"witness-{i}", i * 2 + 1, i * 20 + 1, witness: true, order: $"order-{i}")
        }).ToArray();
        var result = Evaluate(Events(duration: 1), Batch(observations,
            [Frontier(Selection, ObservationClosure.Closed, count * 20, count * 2),
             Frontier(Witness, ObservationClosure.Closed, count * 20, count * 2)], includeWitness: true),
            new(MaxEventCandidateVisits: count));
        Assert.Equal(AssuranceAttemptStatus.Completed, result.Status);
        Assert.Equal(ClaimVerdict.Satisfied, result.Verdict);
        Assert.Equal(count, result.Alternatives[0].Obligations.Length);
    }

    [Fact]
    public void EligibleNonmatchingCandidatesStillConsumeBudget()
    {
        var result = Evaluate(Events(), Batch([Event("trigger", 0, 0),
            Event("wrong1", 1, 1, witness: true, order: "other"),
            Event("wrong2", 2, 2, witness: true, order: "other")], includeWitness: true),
            new(MaxEventCandidateVisits: 1));
        Assert.Equal(AssuranceAttemptStatus.BudgetExceeded, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void OneRoleNeutralEventCanSupplyBothSidesOfASameStepClaim()
    {
        var author = new ExpressionAuthoring();
        var trigger = author.Input<OrderEvent>("trigger");
        var witness = author.Input<OrderEvent>("witness");
        var claim = Start().RequireWithin(Selection, Selection, TimeSpan.Zero)
            .Require(trigger, ClaimInputRole.Trigger, witness, ClaimInputRole.Witness, (t, w) => t.OrderId == w.OrderId).Build();
        var result = Evaluate(claim, Batch([Event("both", 1, 1)], [Frontier(Selection, ObservationClosure.Closed)]));
        Assert.Equal(ClaimVerdict.Satisfied, result.Verdict);
        Assert.Equal("both", Only(result).WitnessOccurrence);
        Assert.Equal(result.Serialize(), TraceEvaluation.Reevaluate(result.Serialize()).Serialize());
        Assert.Throws<JsonException>(() => TraceEvaluation.Reevaluate(
            result.Serialize().Replace("cohesive.assurance.batch/v2", "cohesive.assurance.batch/v1")));
    }

    [Fact]
    public void LegacyTriggerRolePayloadIsRejectedRatherThanSilentlyRemapped()
    {
        var legacy = Event("legacy", 1, 1) with
        {
            Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
                .Add(ClaimInputRole.Trigger, ObservationValue.FromObject(new OrderEvent("o1"))),
            EventPayload = null
        };
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations,
            Evaluate(Events(), Batch([legacy], includeWitness: true)).Status);
    }

    [Fact]
    public void UnusedMalformedInputDoesNotCreateAContractGap()
    {
        var input = new ClaimInput(new("state"), ClaimInputRole.State, new(new ScalarTypeRef(ScalarTypeKind.Bool)));
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Exclude)
            .Require(Expr.Or(Expr.Const(true), Expr.BoundValue(input.Id)), [input]).Build();
        var result = Evaluate(claim, Batch([Stock("unused", 1, 0)], [Frontier(Selection, ObservationClosure.Closed)]));
        Assert.Equal(ClaimVerdict.Satisfied, result.Verdict);
        Assert.DoesNotContain(result.Diagnostics, x => x.Code == TraceEvaluationCodes.InputContractGap);
    }

    [Fact]
    public void SharedMalformedWitnessIsValidatedOnceAcrossTriggers()
    {
        var boolean = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool));
        var claim = Start().RequireEarlier(Selection, Witness)
            .Require(Expr.Eq(Expr.BoundValue(new("t")), Expr.BoundValue(new("w"))),
                [new(new("t"), ClaimInputRole.Trigger, boolean), new(new("w"), ClaimInputRole.Witness, boolean)]).Build();
        var malformed = Event("malformed", 0, 0, witness: true) with { EventPayload = ObservationValue.FromString("invalid") };
        var a = Event("a", 1, 1) with { EventPayload = ObservationValue.FromBool(true) };
        var b = Event("b", 2, 2) with { EventPayload = ObservationValue.FromBool(true) };
        var result = Evaluate(claim, Batch([malformed, a, b], [Frontier(Witness)], includeWitness: true));
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
        Assert.Single(result.Diagnostics.Where(x => x.Code == TraceEvaluationCodes.InputContractGap));
    }

    [Fact]
    public void ArithmeticFailureAndDeadlineOverflowAreNotViolations()
    {
        var input = new ExpressionAuthoring().Input<Inventory>("stock");
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include)
            .Require(input, ClaimInputRole.State, x => x.OnHand / x.Reserved > 0m).Build();
        Assert.Equal(AssuranceAttemptStatus.Unsupported, Evaluate(claim, Batch([Stock("zero", 1, 0)])).Status);
        var overflow = Evaluate(Events(duration: 1), Batch([Event("cancel", 1, long.MaxValue)], includeWitness: true));
        Assert.Equal(AssuranceAttemptStatus.Unsupported, overflow.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, overflow.Verdict);
    }

    [Fact]
    public void FiniteCollectionPredicatesReuseCoreSemantics()
    {
        var input = new ExpressionAuthoring().Input<Numbers>("numbers");
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include)
            .Require(input, ClaimInputRole.State, x => x.Values.All(v => v >= 0m) && x.Values.Sum() == 0m).Build();
        var observation = Stock("empty", 1, 0) with { Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
            .Add(ClaimInputRole.State, ObservationValue.FromObject(new Numbers([]))) };
        Assert.Equal(ClaimVerdict.Satisfied, Evaluate(claim, Batch([observation], [Frontier(Selection, ObservationClosure.Closed)])).Verdict);
    }

    [Fact]
    public void EvaluationEvidenceIsDeterministicAndRetainsExactInputs()
    {
        var claim = Invariant(); var batch = Batch([Stock("stock", 1, 12)]);
        var result = Evaluate(claim, batch);
        Assert.Equal(result.Serialize(), Evaluate(claim, batch).Serialize());
        Assert.Equal(result.Serialize(), TraceEvaluation.Reevaluate(result.Serialize()).Serialize());
        Assert.Equal(result.Fingerprint(), Evaluate(claim, batch).Fingerprint());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(result.Serialize()))), result.Fingerprint());
        using var json = JsonDocument.Parse(result.Serialize());
        Assert.Contains(claim.Fingerprint, result.Serialize());
        Assert.Contains("test://candidate", result.Serialize());
        Assert.Contains("test://enumeration", result.Serialize());
    }

    [Fact]
    public void MissingNullAndExplicitAbsenceRemainDistinctThroughPersistence()
    {
        var input = new ClaimInput(new("row"), ClaimInputRole.State, new(new JsonTypeRef(JsonTypeKind.Object)));
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include)
            .Require(Expr.Eq(Expr.Field(input.Id, "value"), Expr.Const(ObservationValue.Null)), [input]).Build();
        var complete = new[] { Frontier(Selection, ObservationClosure.Closed) };
        foreach (var kind in new[] { "missing", "null", "absent" })
        {
            var fields = new Dictionary<string, ObservationValue>();
            if (kind != "missing") fields.Add("value", kind == "null" ? ObservationValue.Null : ObservationValue.Undefined);
            var observation = Stock("row", 1, 0) with { Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty
                .Add(ClaimInputRole.State, ObservationValue.FromObject(fields)) };
            var result = Evaluate(claim, Batch([observation], complete));
            Assert.Equal(kind == "missing" ? ClaimVerdict.Inconclusive : kind == "null" ? ClaimVerdict.Satisfied : ClaimVerdict.Violated, result.Verdict);
            Assert.Equal(result.Serialize(), TraceEvaluation.Reevaluate(result.Serialize()).Serialize());
        }
    }

    [Fact]
    public void TamperedVerdictFailsIndependentReevaluation()
    {
        var result = Evaluate(Invariant(), Batch([Stock("bad", 1, 12)]));
        var forged = System.Text.Json.Nodes.JsonNode.Parse(result.Serialize())!;
        forged["verdict"] = "Satisfied";
        Assert.Throws<JsonException>(() => TraceEvaluation.Reevaluate(forged.ToJsonString()));
    }

    [Fact]
    public void LateEvidenceRequiresANewBatchAndPreservesThePriorResult()
    {
        var batch = Batch([Event("cancel", 1, 0)], [Frontier(Witness)], includeWitness: true);
        var original = Evaluate(Events(), batch);
        var corrected = batch with { Epoch = R("correction-epoch"), Alternatives = [batch.Alternatives[0] with {
            Reference = R("corrected-alternative"), Observations = [.. batch.Alternatives[0].Observations, Event("release", 2, 5, witness: true)] }] };
        Assert.Equal(ClaimVerdict.Violated, Only(original).Verdict);
        Assert.Equal(ClaimVerdict.Satisfied, Only(Evaluate(Events(), corrected)).Verdict);
        Assert.Equal(ClaimVerdict.Violated, Only(original).Verdict);
        Assert.NotEqual(original.Fingerprint(), Evaluate(Events(), corrected).Fingerprint());
    }

    [Fact]
    public void MissingCollectionMemberIsEvidenceGapNotDomainAbsence()
    {
        var input = new ClaimInput(new("items"), ClaimInputRole.State, new(new ArrayTypeRef(new JsonTypeRef(JsonTypeKind.Object))));
        var predicate = Expr.Call(ExprFunctionNames.All, Expr.BoundValue(input.Id),
            Expr.Eq(Expr.Field(Cohesive.Model.Expressions.ExprFieldRoots.CurrentItem + ".value"), Expr.Const(ObservationValue.Null)));
        var claim = Start().AtEveryCommittedState(Selection, InitialStateSelection.Include).Require(predicate, [input]).Build();
        var values = ObservationValue.FromArray([ObservationValue.FromObject(new Dictionary<string, ObservationValue>())]);
        var observation = Stock("items", 1, 0) with { Values = ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.State, values) };
        Assert.Equal(ClaimVerdict.Inconclusive, Evaluate(claim, Batch([observation], [Frontier(Selection, ObservationClosure.Closed)])).Verdict);
    }

    [Fact]
    public void DefaultObservationArrayProducesStructuredInvalidInput()
    {
        var batch = Batch([]);
        batch = batch with { Alternatives = [batch.Alternatives[0] with { Observations = default }] };
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, Evaluate(Invariant(), batch).Status);
    }

    [Fact]
    public void WrongObservationKindCannotBecomeVacuousSuccess()
    {
        var result = Evaluate(Invariant(), Batch([Event("wrong-kind", 1, 1)], [Frontier(Selection, ObservationClosure.Closed)]));
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, result.Status);
        Assert.Equal(ClaimVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void CancellationDoesNotReturnPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TraceClaimEvaluator.Evaluate(Invariant(), Batch([]), cancellationToken: cancellation.Token));
    }
}

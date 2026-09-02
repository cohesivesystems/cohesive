using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Tests;

public sealed class PropertyCaseTests
{
    [Fact]
    public void TypedProperty_FindsStableMinimalCounterexampleAndReplaysFromCompactToken()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();

        var result = generator.CheckProperty(
            seed: 42,
            property: static sample => sample.Number < 50);

        Assert.Equal(PropertyCaseRunStatus.CounterexampleFound, result.Status);
        Assert.True(result.IsConclusive);
        var counterexample = Assert.IsType<PropertyCase>(result.BestCounterexample);
        var minimal = generator.Materializer.Materialize(counterexample.Observation);
        Assert.Equal(50, minimal.Number);
        Assert.False(minimal.Enabled);
        Assert.Equal("first", minimal.Tier);
        Assert.Equal("fixed", minimal.Fixed);
        Assert.NotEmpty(counterexample.Replay.ShrinkChoices);
        var token = counterexample.Replay.ToToken();
        var tokenFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Assert.Equal("dabd45cc5824671f3f09004221f4b123311da78206881345f6da5446b9d60e4a", tokenFingerprint);
        Assert.StartsWith("csimpc1.", token, StringComparison.Ordinal);
        Assert.Equal(counterexample.Replay, PropertyCaseReplayEvidence.ParseToken(token));
        Assert.Equal(minimal, generator.ReplayPropertyCase(token));
        Assert.Equal(counterexample.Observation, ReferencePropertyCaseInterpreter.Replay(generator.Plan, token));
    }

    [Fact]
    public void MemberDeclarationOrder_DoesNotChangeCounterexampleOrReplayToken()
    {
        var first = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();
        var reordered = SampleDefinition(["Tier", "Fixed", "Enabled", "Number"]).Compile();

        var firstResult = first.CheckProperty(seed: 1729, property: static sample => sample.Number < 50);
        var reorderedResult = reordered.CheckProperty(seed: 1729, property: static sample => sample.Number < 50);

        Assert.Equal(first.Plan.Fingerprint, reordered.Plan.Fingerprint);
        Assert.Equal(firstResult.Status, reorderedResult.Status);
        Assert.Equal(
            firstResult.BestCounterexample!.Observation,
            reorderedResult.BestCounterexample!.Observation);
        Assert.Equal(
            firstResult.BestCounterexample.Replay.ToToken(),
            reorderedResult.BestCounterexample.Replay.ToToken());
        Assert.Equal(firstResult.BestCounterexample, reorderedResult.BestCounterexample);
        Assert.Equal(firstResult.Evidence, reorderedResult.Evidence);
    }

    [Fact]
    public void ClassifiedRun_RetainsNormalizedCoverageAndBoundedEvidence()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();

        var result = generator.CheckProperty(
            seed: 17,
            evaluate: static sample => sample.Number % 2 == 0
                ? PropertyCaseEvaluation.Pass("number", "even", "number")
                : PropertyCaseEvaluation.Pass("odd", "number"),
            options: new(requiredPassedCases: 25));

        Assert.Equal(PropertyCaseRunStatus.Passed, result.Status);
        Assert.True(result.IsConclusive);
        Assert.Null(result.BestCounterexample);
        Assert.Equal(25, result.Evidence.GeneratedCaseCount);
        Assert.Equal(25, result.Evidence.PassedCaseCount);
        Assert.Equal(0, result.Evidence.DiscardedCaseCount);
        Assert.Equal(25, result.Evidence.Coverage.Single(item => item.Classification.Value == "number").Count);
        Assert.Equal(
            25,
            result.Evidence.Coverage.Sum(static item =>
                item.Classification.Value is "even" or "odd" ? item.Count : 0));
        Assert.Equal(
            PropertyCaseEvaluation.Pass("even", "number"),
            PropertyCaseEvaluation.Pass("number", "even", "number"));
    }

    [Fact]
    public void DiscardBound_ExhaustsWithStableDiagnostic()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();

        var result = generator.CheckProperty(
            seed: 1,
            evaluate: static _ => PropertyCaseEvaluation.Discard("precondition-not-met"),
            options: new(requiredPassedCases: 1, maximumDiscardedCases: 2));

        Assert.Equal(PropertyCaseRunStatus.Exhausted, result.Status);
        Assert.False(result.IsConclusive);
        Assert.Null(result.BestCounterexample);
        Assert.Equal(3, result.Evidence.GeneratedCaseCount);
        Assert.Equal(3, result.Evidence.DiscardedCaseCount);
        var discardCoverage = Assert.Single(result.Evidence.Coverage);
        Assert.Equal("precondition-not-met", discardCoverage.Classification.Value);
        Assert.Equal(3, discardCoverage.Count);
        var diagnostic = Assert.Single(result.Validation.Diagnostics);
        Assert.Equal("simulation.propertyCase.discardLimitExceeded", diagnostic.Code);
        Assert.Equal("/run/discardedCases", diagnostic.Location);
    }

    [Fact]
    public void ShrinkBound_ExhaustsWithoutClaimingStableMinimum()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();
        var seed = Enumerable.Range(0, 100)
            .First(candidate => generator.Generate(candidate).Value.Number > 0);

        var result = generator.CheckProperty(
            seed,
            property: static _ => false,
            options: new(requiredPassedCases: 1, maximumShrinkCandidates: 0));

        Assert.Equal(PropertyCaseRunStatus.Exhausted, result.Status);
        Assert.False(result.IsConclusive);
        Assert.NotNull(result.BestCounterexample);
        Assert.Empty(result.BestCounterexample.Replay.ShrinkChoices);
        Assert.Equal(0, result.Evidence.ShrinkCandidateCount);
        Assert.Equal(
            "simulation.propertyCase.shrinkLimitExceeded",
            Assert.Single(result.Validation.Diagnostics).Code);
    }

    [Fact]
    public void InvalidOptions_ReturnAllStableStructuredDiagnosticsWithoutEvaluation()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();
        var evaluationCount = 0;

        var result = generator.CheckProperty(
            seed: 1,
            property: _ =>
            {
                evaluationCount++;
                return true;
            },
            options: new(
                requiredPassedCases: 0,
                maximumDiscardedCases: -1,
                maximumShrinkCandidates: -1));

        Assert.Equal(PropertyCaseRunStatus.Invalid, result.Status);
        Assert.False(result.IsConclusive);
        Assert.Equal(0, evaluationCount);
        Assert.Equal(
            [
                "simulation.propertyCase.options.maximumDiscardedCasesInvalid",
                "simulation.propertyCase.options.maximumShrinkCandidatesInvalid",
                "simulation.propertyCase.options.requiredPassedCasesInvalid"
            ],
            result.Validation.Diagnostics.Select(static diagnostic => diagnostic.Code));
    }

    [Fact]
    public void RawObservationSurface_UsesSameCaseAndShrinkEvidence()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();
        var typed = generator.CheckProperty(seed: 99, property: static sample => sample.Number < 50);

        var raw = ReferencePropertyCaseInterpreter.Check(
            generator.Plan,
            seed: 99,
            property: static observation => observation.GetField("Number").Int64 < 50);

        Assert.Equal(typed.Status, raw.Status);
        Assert.Equal(typed.BestCounterexample!.Observation, raw.BestCounterexample!.Observation);
        Assert.Equal(typed.BestCounterexample.Replay.ToToken(), raw.BestCounterexample.Replay.ToToken());
        Assert.Equal(typed.Evidence.GeneratedCaseCount, raw.Evidence.GeneratedCaseCount);
        Assert.Equal(typed.Evidence.ShrinkCandidateCount, raw.Evidence.ShrinkCandidateCount);
    }

    [Fact]
    public void ReplayToken_FailsClosedForAnotherDefinitionOrNoncanonicalEncoding()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();
        var result = generator.CheckProperty(seed: 42, property: static sample => sample.Number < 50);
        var token = result.BestCounterexample!.Replay.ToToken();
        var another = Simulation.Define<AnotherSample>(sample => sample.Member(
                value => value.Number,
                Gen.Int32(minimum: 0, maximum: 100)))
            .Compile();

        Assert.Throws<ArgumentException>(() => another.ReplayPropertyCase(token));
        Assert.Throws<FormatException>(() => PropertyCaseReplayEvidence.ParseToken(token + "="));
    }

    [Theory]
    [InlineData(10, 100, 50, true)]
    [InlineData(-100, -10, -50, false)]
    public void Int32Shrinking_ConvergesToTheFailingBoundaryWithinOneSidedRanges(
        int minimum,
        int maximum,
        int failingBoundary,
        bool failsAtOrAboveBoundary)
    {
        var generator = Simulation.Define<RangeSample>(sample => sample.Member(
                value => value.Number,
                Gen.Int32(minimum, maximum)))
            .Compile();
        var seed = Enumerable.Range(0, 1_000)
            .First(candidate =>
            {
                var number = generator.Generate(candidate).Value.Number;
                return failsAtOrAboveBoundary
                    ? number >= failingBoundary
                    : number <= failingBoundary;
            });

        var result = generator.CheckProperty(
            seed,
            property: sample => failsAtOrAboveBoundary
                ? sample.Number < failingBoundary
                : sample.Number > failingBoundary);

        Assert.Equal(PropertyCaseRunStatus.CounterexampleFound, result.Status);
        var counterexample = Assert.IsType<PropertyCase>(result.BestCounterexample);
        Assert.Equal(failingBoundary, generator.Materializer.Materialize(counterexample.Observation).Number);
        var shape = generator.Plan.OutputShape.Graph.GetShape(generator.Plan.OutputShape.ShapeId);
        Assert.True(
            ObservationValidator.TryValidateAgainstShape(
                counterexample.Observation.Fields,
                shape,
                out var validationError,
                generator.Plan.OutputShape.Graph),
            validationError);
    }

    [Fact]
    public void FailureWithNoShrinkCandidates_IsConclusiveAtAZeroShrinkBound()
    {
        var generator = Simulation.Define<ConstantSample>(sample => sample.Member(
                value => value.Value,
                Gen.Constant("fixed")))
            .Compile();

        var result = generator.CheckProperty(
            seed: 42,
            property: static _ => false,
            options: new(requiredPassedCases: 1, maximumShrinkCandidates: 0));

        Assert.Equal(PropertyCaseRunStatus.CounterexampleFound, result.Status);
        Assert.True(result.IsConclusive);
        Assert.Equal(0, result.Evidence.ShrinkCandidateCount);
        Assert.Equal("fixed", generator.ReplayPropertyCase(result.BestCounterexample!.Replay.ToToken()).Value);
    }

    [Fact]
    public void Replay_FailsClosedForAnUnsupportedShrinkerVersion()
    {
        var generator = SampleDefinition(["Number", "Enabled", "Tier", "Fixed"]).Compile();
        var generated = generator.Generate(seed: 42);
        PropertyCaseReplayEvidence evidence = new(
            generated.Replay,
            shrinker: "unsupported-property-shrinker/v1",
            shrinkChoices: []);

        var exception = Assert.Throws<NotSupportedException>(() =>
            ReferencePropertyCaseInterpreter.Replay(generator.Plan, evidence));

        Assert.Contains("unsupported-property-shrinker/v1", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ReferencePropertyCaseInterpreter.ShrinkerIdentity, exception.Message, StringComparison.Ordinal);
    }

    static PocoGenerationDefinition<PropertySample> SampleDefinition(IReadOnlyList<string> order)
    {
        return Simulation.Define<PropertySample>(sample =>
        {
            foreach (var member in order)
            {
                switch (member)
                {
                    case "Number":
                        sample.Member(value => value.Number, Gen.Int32(minimum: 0, maximum: 100));
                        break;
                    case "Enabled":
                        sample.Member(value => value.Enabled, Gen.Bernoulli(probability: 0.5));
                        break;
                    case "Tier":
                        sample.Member(value => value.Tier, Gen.Categorical(
                            Gen.Weighted("first", weight: 1d),
                            Gen.Weighted("second", weight: 1d),
                            Gen.Weighted("third", weight: 1d)));
                        break;
                    case "Fixed":
                        sample.Member(value => value.Fixed, Gen.Constant("fixed"));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown member '{member}'.");
                }
            }
        });
    }

    public sealed record PropertySample(int Number, bool Enabled, string Tier, string Fixed);

    public sealed record AnotherSample(int Number);

    public sealed record RangeSample(int Number);

    public sealed record ConstantSample(string Value);
}

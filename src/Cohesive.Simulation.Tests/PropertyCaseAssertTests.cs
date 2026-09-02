using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Xunit;
using Xunit.Sdk;

namespace Cohesive.Simulation.Tests;

public sealed class PropertyCaseAssertTests
{
    [Fact]
    public void PassedResult_ReturnsNormally()
    {
        var generator = NumberGenerator().Compile();
        var result = generator.CheckProperty(
            seed: 42,
            property: static _ => true,
            options: new(requiredPassedCases: 3));

        PropertyCaseAssert.Passed(result);
    }

    [Fact]
    public void Counterexample_ReportsDeterministicEvidenceAndExactReplayMaterial()
    {
        var generator = NumberGenerator().Compile();
        var result = generator.CheckProperty(
            seed: 42,
            evaluate: static _ => PropertyCaseEvaluation.Fail("z-last", "a-first"));
        var counterexample = Assert.IsType<PropertyCase>(result.BestCounterexample);

        var exception = Assert.Throws<XunitException>(() => PropertyCaseAssert.Passed(result));

        Assert.Contains("Status: CounterexampleFound", exception.Message, StringComparison.Ordinal);
        Assert.Contains("  generatedCases: 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("  \"a-first\": 1\n  \"z-last\": 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "    \"a-first\"\n    \"z-last\"",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"  ReplayToken: {counterexample.Replay.ToToken()}",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"  Observation: {counterexample.Observation.ToCanonicalJson()}",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            exception.Message,
            Assert.Throws<XunitException>(() => PropertyCaseAssert.Passed(result)).Message);
    }

    [Fact]
    public void InvalidResult_ReportsEveryStructuredDiagnosticInStableOrder()
    {
        var result = NumberGenerator().Compile().CheckProperty(
            seed: 42,
            property: static _ => true,
            options: new(
                requiredPassedCases: 0,
                maximumDiscardedCases: -1,
                maximumShrinkCandidates: -1));

        var exception = Assert.Throws<XunitException>(() => PropertyCaseAssert.Passed(result));

        Assert.Contains("Status: Invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Counterexample:\n  <none>", exception.Message, StringComparison.Ordinal);
        Assert.True(
            exception.Message.IndexOf("maximumDiscardedCasesInvalid", StringComparison.Ordinal)
            < exception.Message.IndexOf("maximumShrinkCandidatesInvalid", StringComparison.Ordinal));
        Assert.True(
            exception.Message.IndexOf("maximumShrinkCandidatesInvalid", StringComparison.Ordinal)
            < exception.Message.IndexOf("requiredPassedCasesInvalid", StringComparison.Ordinal));
    }

    [Fact]
    public void ExhaustedResult_ReportsTheBoundAndNoCounterexampleWhenEveryCaseIsDiscarded()
    {
        var result = NumberGenerator().Compile().CheckProperty(
            seed: 42,
            evaluate: static _ => PropertyCaseEvaluation.Discard("precondition"),
            options: new(requiredPassedCases: 1, maximumDiscardedCases: 0));

        var exception = Assert.Throws<XunitException>(() => PropertyCaseAssert.Passed(result));

        Assert.Contains("Status: Exhausted", exception.Message, StringComparison.Ordinal);
        Assert.Contains("  discardedCases: 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("simulation.propertyCase.discardLimitExceeded", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Counterexample:\n  <none>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeObservation_IsExplicitlyTruncatedWithoutTruncatingReplayToken()
    {
        var generator = Simulation.Define<TextSample>(sample => sample.Member(
                value => value.Value,
                Gen.Constant(new string('x', 8_000))))
            .Compile();
        var result = generator.CheckProperty(seed: 42, property: static _ => false);
        var counterexample = Assert.IsType<PropertyCase>(result.BestCounterexample);

        var exception = Assert.Throws<XunitException>(() => PropertyCaseAssert.Passed(result));

        Assert.Contains(
            "[observation truncated after 4096 of ",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(" characters; replay token is exact]", exception.Message, StringComparison.Ordinal);
        Assert.Contains(counterexample.Replay.ToToken(), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(counterexample.Observation.ToCanonicalJson(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullResult_IsRejectedAtTheAdapterBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => PropertyCaseAssert.Passed(null!));
    }

    static PocoGenerationDefinition<NumberSample> NumberGenerator() =>
        Simulation.Define<NumberSample>(sample => sample.Member(
            value => value.Number,
            Gen.Int32(minimum: 0, maximum: 100)));

    public sealed record NumberSample(int Number);

    public sealed record TextSample(string Value);
}

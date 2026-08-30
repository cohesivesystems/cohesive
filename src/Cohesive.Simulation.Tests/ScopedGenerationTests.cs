using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Tests;

public sealed class ScopedGenerationTests
{
    [Fact]
    public void ExplicitScopes_IsolateStableStreamsUsingTheSameDefinitionAndSeed()
    {
        var generator = SampleGenerator();
        GenerationScope customers = new("world/demo/customers");
        GenerationScope operators = new("world/demo/operators");

        var customerStream = generator.GenerateSequence(seed: 1729, scope: customers, count: 8);
        var repeatedCustomerStream = generator.GenerateSequence(seed: 1729, scope: customers, count: 8);
        var operatorStream = generator.GenerateSequence(seed: 1729, scope: operators, count: 8);

        Assert.Equal(
            customerStream.Select(static item => item.Value),
            repeatedCustomerStream.Select(static item => item.Value));
        Assert.Equal(
            customerStream.Select(static item => item.Observation),
            repeatedCustomerStream.Select(static item => item.Observation));
        Assert.Equal(
            customerStream.Select(static item => item.Replay),
            repeatedCustomerStream.Select(static item => item.Replay));
        Assert.Contains(customerStream.Zip(operatorStream), static pair => pair.First.Value != pair.Second.Value);
        Assert.All(customerStream, item => Assert.Equal(customers, item.Replay.Scope));
        Assert.All(operatorStream, item => Assert.Equal(operators, item.Replay.Scope));
    }

    [Fact]
    public void DefaultScope_PreservesTheUnscopedConvenienceSurface()
    {
        var generator = SampleGenerator();

        var implicitScope = generator.Generate(seed: 42, sequenceIndex: 3);
        var explicitScope = generator.Generate(seed: 42, scope: GenerationScope.Default, sequenceIndex: 3);

        Assert.Equal(implicitScope, explicitScope);
        Assert.Equal(GenerationScope.Default, implicitScope.Replay.Scope);
    }

    [Fact]
    public void ReplayToken_RetainsTheExactGenerationScope()
    {
        var generator = SampleGenerator();
        GenerationScope scope = new("scenario/checkout/customer");
        var generated = generator.Generate(seed: 91, scope: scope, sequenceIndex: 5);

        var evidence = GenerationReplayEvidence.ParseToken(generated.Replay.ToToken());
        var replayed = ReferenceGenerationInterpreter.Replay(generator.Plan, evidence);

        Assert.Equal(scope, evidence.Scope);
        Assert.Equal(generated.Observation, replayed.Observation);
        Assert.Equal(generated.Replay, replayed.Replay);
    }

    [Fact]
    public void LazySequences_DoNotMaterializeTheDeclaredBound()
    {
        var generator = SampleGenerator();
        GenerationScope scope = new("script/preview");

        var preview = generator
            .EnumerateSequence(seed: 100, scope: scope, count: int.MaxValue)
            .Take(2)
            .ToArray();

        Assert.Equal(2, preview.Length);
        Assert.Equal(0, preview[0].Replay.SequenceIndex);
        Assert.Equal(1, preview[1].Replay.SequenceIndex);
        Assert.Equal(generator.Generate(seed: 100, scope: scope, sequenceIndex: 0), preview[0]);
        Assert.Equal(generator.Generate(seed: 100, scope: scope, sequenceIndex: 1), preview[1]);
    }

    [Fact]
    public void EagerAndLazySequences_HaveEquivalentScopedSemantics()
    {
        var generator = SampleGenerator();
        GenerationScope scope = new("fixture/assurance");

        var eager = generator.GenerateSequence(seed: 700, scope: scope, count: 12);
        var lazy = generator.EnumerateSequence(seed: 700, scope: scope, count: 12).ToArray();

        Assert.Equal(eager.Select(static item => item.Value), lazy.Select(static item => item.Value));
        Assert.Equal(eager.Select(static item => item.Observation), lazy.Select(static item => item.Observation));
        Assert.Equal(eager.Select(static item => item.Replay), lazy.Select(static item => item.Replay));
    }

    [Fact]
    public void RawObservationEnumeration_UsesTheSameScopedAddressesAsEagerGeneration()
    {
        var plan = SampleGenerator().Plan;
        GenerationScope scope = new("playwright/checkout/seed");

        var eager = ReferenceGenerationInterpreter.GenerateSequence(plan, seed: 301, scope: scope, count: 5);
        var lazy = ReferenceGenerationInterpreter
            .EnumerateSequence(plan, seed: 301, scope: scope, count: 5)
            .ToArray();

        Assert.Equal(eager.Select(static item => item.Observation), lazy.Select(static item => item.Observation));
        Assert.Equal(eager.Select(static item => item.Replay), lazy.Select(static item => item.Replay));
    }

    [Fact]
    public void LazySequence_ValidatesItsBoundaryBeforeEnumeration()
    {
        var generator = SampleGenerator();

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.EnumerateSequence(seed: 1, count: -1));
        Assert.Throws<ArgumentException>(() =>
            generator.EnumerateSequence(seed: 1, scope: default, count: 1));
    }

    static CompiledPocoGenerator<ScopedSample> SampleGenerator() =>
        Simulation.Define<ScopedSample>(sample => sample
                .Member(value => value.First, Gen.Int32(int.MinValue, int.MaxValue))
                .Member(value => value.Second, Gen.Int32(int.MinValue, int.MaxValue)))
            .Compile();

    public sealed record ScopedSample(int First, int Second);
}

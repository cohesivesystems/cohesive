using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TransitionSpecializedKernelTests
{
    [Theory]
    [InlineData("Booked", "Assigned", TransitionOutcomeDisposition.Applied, TransitionDecisionKind.Applied)]
    [InlineData("Assigned", "Assigned", TransitionOutcomeDisposition.Applied, TransitionDecisionKind.NoChange)]
    [InlineData("Delivered", "Assigned", TransitionOutcomeDisposition.Applied, TransitionDecisionKind.AdmissionRejected)]
    [InlineData("Booked", "invalid", TransitionOutcomeDisposition.Applied, TransitionDecisionKind.InvalidDefinition)]
    [InlineData("Booked", "Assigned", TransitionOutcomeDisposition.DomainRejected, TransitionDecisionKind.DomainRejected)]
    public void Ordinal_writes_preserve_decision_and_publish_only_retained_changes(
        string before, string after, TransitionOutcomeDisposition disposition, TransitionDecisionKind expectedKind)
    {
        var entity = ObjectEntityDefinition.For<State>(new("kernel-state"));
        var authored = TransitionAuthoring.Create<State, Input, bool>(entity.Shape,
            new(new("kernel"), new("r1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("tests/kernel"), DocumentOrigin.Generated)),
            transition => transition
                .Invariant(new("valid"), state => state.Status != "invalid")
                .Requires(new("guard"), (state, _) => state.Status != "Delivered", (_, _) => false)
                .Set(new("status"), state => state.Status, (_, input) => input.Status)
                .Set(new("copy-original"), state => state.Payload, (state, _) => state.Status)
                .Return(new("done"), disposition, true));
        var plan = Assert.IsType<CompiledTransitionPlan>(authored.Compile().Plan);
        var shape = entity.StateShape;
        var original = ObservationValue.FromObject(new State(before, before));
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Input(after)));
        var fresh = PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(new State("fresh", before)));
        var canonical = ObservationLayout.Create(shape);
        var reverse = ObservationLayout.Create(shape, canonical.FieldIdentities.Reverse());

        foreach (var layout in new[] { canonical, reverse })
        {
            var observation = Observation.Create(shape, layout,
                layout.FieldIdentities.Select(original.GetProperty).ToArray().AsSpan());
            var state = PortableValue.Concrete(plan.Definition.Observation, observation.Value);

            foreach (var commitState in new[] { null, fresh })
            {
                var expected = TransitionReferenceInterpreter.DecideFullState(plan, new("activation"), input, state, commitState);
                if (commitState is null) Assert.Equal(expectedKind, expected.Kind);

                var actual = TransitionReferenceInterpreter.DecideForEntity(plan, new("activation"), input, observation, commitState);
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual.Decision));
                if (expected.Patch.IsEmpty)
                {
                    Assert.Null(actual.Projected);
                    continue;
                }

                Assert.NotNull(actual.Projected);
                Assert.Equal(TransitionStateProjector.Apply(observation.Value, expected), actual.Projected.Value);
                Assert.Equal(ObservationValue.FromObject(new State(after, before)), actual.Projected.Value);
                Assert.Same(layout, Assert.IsAssignableFrom<IOrdinalObservationStorage>(actual.Projected.Value.Fields).Layout);
                Assert.Equal(original, observation.Value);
            }
        }
    }

    [Fact]
    public void Ordinal_writes_preserve_explicit_null()
    {
        var entity = ObjectEntityDefinition.For<NullableState>(new("fused-null"));
        var authored = TransitionAuthoring.Create<NullableState, int, bool>(entity.Shape,
            new(new("fused-null"), new("r1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("tests/fused-null"), DocumentOrigin.Generated)),
            transition => transition
                .Set(new("clear"), state => state.Note, (string?)null)
                .Return(new("done"), TransitionOutcomeDisposition.Applied, true));
        var plan = Assert.IsType<CompiledTransitionPlan>(authored.Compile().Plan);
        var shape = entity.StateShape;
        var layout = ObservationLayout.Create(shape);
        var observation = Observation.Create(shape, layout, new[] { ObservationValue.FromString("original") }.AsSpan());
        var input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromInt64(0));
        var state = PortableValue.Concrete(plan.Definition.Observation, observation.Value);
        var expected = TransitionReferenceInterpreter.DecideFullState(plan, new("clear-note"), input, state);
        var actual = TransitionReferenceInterpreter.DecideForEntity(plan, new("clear-note"), input, observation);

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual.Decision));
        Assert.Equal(PortableValueState.Null, Assert.Single(actual.Decision.Patch).After.State);
        Assert.NotNull(actual.Projected);
        Assert.Equal(ObservationValue.Null, actual.Projected.Value.GetProperty(nameof(NullableState.Note)));
        Assert.Equal(ObservationValue.FromString("original"), observation.Value.GetProperty(nameof(NullableState.Note)));
    }

    sealed record State(string Status, string Payload);
    sealed record Input(string Status);
    sealed record NullableState(string? Note);
}

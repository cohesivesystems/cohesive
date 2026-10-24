using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

// Differential coverage for batched projection: patches are verified in order against pending assignments and
// applied once, which must equal the sequential WithField/WithoutField reference for overlapping paths.
public sealed class TransitionStateProjectionTests
{
    static readonly ValueContract AnyObject = new(new ObjectTypeRef([]));
    static readonly ValueContract AnyInt = new(new ScalarTypeRef(ScalarTypeKind.Int64));
    static readonly ValueContract NullableInt = new(new ScalarTypeRef(ScalarTypeKind.Int64), nullability: FieldNullability.Nullable);

    [Fact]
    public void Ancestor_then_descendant_patches_match_sequential_projection()
    {
        var state = Parse("""{"a":{"x":1,"y":2},"b":1}""");
        var decision = Decision(
            Set(Path("a"), Concrete(AnyObject, """{"a":{"x":1,"y":2}}""", "a"), Concrete(AnyObject, """{"x":10}""")),
            Set(Path("a", "x"), Concrete(AnyInt, "10"), Concrete(AnyInt, "11")),
            Set(Path("a", "z"), PortableValue.Absent(AnyInt), Concrete(AnyInt, "5")),
            Remove(Path("b"), Concrete(AnyInt, "1")));

        AssertMatchesSequential(state, decision, """{"a":{"x":11,"z":5}}""");
    }

    [Fact]
    public void Ancestor_read_after_descendant_patch_observes_the_composed_candidate()
    {
        var state = Parse("""{"a":{"x":1,"y":2}}""");
        var decision = Decision(
            Set(Path("a", "x"), Concrete(AnyInt, "1"), Concrete(AnyInt, "7")),
            Set(Path("a"), Concrete(AnyObject, """{"x":7,"y":2}"""), Concrete(AnyObject, """{"q":1}""")),
            Remove(Path("a", "q"), Concrete(AnyInt, "1")));

        // Removing the last nested field prunes its now-empty ancestor, exactly as sequential WithoutField does.
        AssertMatchesSequential(state, decision, "{}");
    }

    [Fact]
    public void Before_evidence_is_checked_against_earlier_pending_patches()
    {
        var state = Parse("""{"a":{"x":1}}""");
        var stale = Decision(
            Set(Path("a", "x"), Concrete(AnyInt, "1"), Concrete(AnyInt, "2")),
            Set(Path("a", "x"), Concrete(AnyInt, "1"), Concrete(AnyInt, "3")));
        var exception = Assert.Throws<TransitionStatePreparationException>(() => TransitionStateProjector.Apply(state, stale));
        Assert.Equal("transition.state.beforeMismatch", exception.Code);
        Assert.Equal("/decision/patch/1/before", exception.Location);

        var nullThenSet = Decision(
            Set(Path("a", "x"), Concrete(AnyInt, "1"), PortableValue.Null(NullableInt)),
            Set(Path("a", "x"), PortableValue.Null(NullableInt), Concrete(AnyInt, "4")));
        AssertMatchesSequential(state, nullThenSet, """{"a":{"x":4}}""");
    }

    [Fact]
    public void Scalar_traversal_fails_at_its_own_patch_before_later_evidence_checks()
    {
        var state = Parse("""{"a":1}""");
        var decision = Decision(
            Set(Path("a", "x"), PortableValue.Absent(AnyInt), Concrete(AnyInt, "2")),
            Set(Path("b"), Concrete(AnyInt, "99"), Concrete(AnyInt, "3")));
        var sequential = Assert.Throws<InvalidOperationException>(() => state.WithField(Path("a", "x"), ObservationValue.FromInt64(2)));
        var batched = Assert.Throws<InvalidOperationException>(() => TransitionStateProjector.Apply(state, decision));
        Assert.Equal(sequential.Message, batched.Message);
    }

    static void AssertMatchesSequential(ObservationValue state, TransitionDecision decision, string expectedJson)
    {
        var expected = state;
        foreach (var patch in decision.Patch)
        {
            expected = patch.After.State switch
            {
                PortableValueState.Concrete => expected.WithField(patch.Path, patch.After.Value!.Value),
                PortableValueState.Null => expected.WithField(patch.Path, ObservationValue.Null),
                _ => expected.WithoutField(patch.Path)
            };
        }

        var actual = TransitionStateProjector.Apply(state, decision);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetRawText(), actual.GetRawText());
        Assert.Equal(Parse(expectedJson), actual);
    }

    static TransitionDecision Decision(params TransitionExecutedPatch[] patches)
    {
        var template = TemplateDecision.Value;
        return new(TransitionDecisionKind.Applied, template.Outcome, [.. patches], [], [], template.GuaranteeDemands,
            [], [], template.Evidence);
    }

    static TransitionExecutedPatch Set(FieldPath path, PortableValue before, PortableValue after) =>
        new(new($"set/{path}"), path, new EvaluatedSetTransitionPatch(after), before, after);

    static TransitionExecutedPatch Remove(FieldPath path, PortableValue before) =>
        new(new($"remove/{path}"), path, new EvaluatedRemoveTransitionPatch(), before, PortableValue.Absent(before.Contract));

    static PortableValue Concrete(ValueContract contract, string json, string? property = null)
    {
        var value = Parse(json);
        return PortableValue.Concrete(contract, property is null ? value : value.GetProperty(property));
    }

    static ObservationValue Parse(string json) => ObservationValue.FromJsonElement(System.Text.Json.JsonDocument.Parse(json).RootElement);

    static FieldPath Path(params string[] names) => new([.. names.Select(FieldPathSegment.ForField)]);

    // Only the guarantee demands and evidence envelope are reused; projection reads the crafted patches alone.
    static readonly Lazy<TransitionDecision> TemplateDecision = new(() =>
    {
        var entity = ObjectEntityDefinition.For<Run>(new("projection-run"));
        var authored = TransitionAuthoring.Create<Run, Go, string>(entity.Shape,
            new(new("transition/projection"), new("revision/1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("tests/transition-state-projection"), DocumentOrigin.Generated)),
            transition => transition
                .Set(new("set-status"), state => state.Status, "done")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "done"));
        var compilation = authored.Compile();
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("activation/projection"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Go(true))),
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(new Run("pending"))));
        Assert.Equal(TransitionDecisionKind.Applied, decision.Kind);
        return decision;
    });

    sealed record Run(string Status);
    sealed record Go(bool Flag);
}

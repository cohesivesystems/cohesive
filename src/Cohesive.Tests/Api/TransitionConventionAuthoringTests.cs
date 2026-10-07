using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Api;

public sealed class TransitionConventionAuthoringTests
{
    sealed record State(string Status);

    [Fact]
    public void Convention_identities_are_deterministic_and_independent_of_source_location()
    {
        var first = Declare("/one/file.cs", 10);
        var second = Declare("/moved/file.cs", 400);
        Assert.True(first.Compile().IsSuccessful);
        Assert.True(second.Compile().IsSuccessful);
        Assert.Equal(first.Reference, second.Reference);
    }

    static Transition<State, bool, string> Declare(string file, int line) =>
        TransitionAuthoring.Create<State, bool, string>(ObjectEntityDefinition.For<State>().Shape,
            id: new("test/conventions"), revision: new("1"),
            transition => transition
                .Requires((state, _) => state.Status == "Draft", (_, _) => "Rejected",
                    sourceFile: file, sourceLine: line)
                .Set(state => state.Status, "Submitted", sourceFile: file, sourceLine: line)
                .Return("Submitted", sourceFile: file, sourceLine: line),
            sourceFile: file, sourceLine: line);
}

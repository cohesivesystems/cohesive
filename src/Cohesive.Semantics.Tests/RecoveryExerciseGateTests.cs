using System.Xml.Linq;

namespace Cohesive.Semantics.Tests;

public sealed class RecoveryExerciseGateTests
{
    [Theory]
    [InlineData("Breaking", false)]
    [InlineData("Compatible", true)]
    [InlineData("Unexpected", false)]
    public void RejectsUnexpectedCompatibility(string verdict, bool expectedAdditionBreak) =>
        Assert.Throws<InvalidOperationException>(() => ExerciseAssertions.RequireCompatibility(verdict, expectedAdditionBreak, "API"));

    [Theory]
    [InlineData("Compatible", false)]
    [InlineData("Unknown", false)]
    [InlineData("Breaking", true)]
    public void AcceptsOnlyDeclaredCompatibilityOutcomes(string verdict, bool expectedAdditionBreak) =>
        ExerciseAssertions.RequireCompatibility(verdict, expectedAdditionBreak, "API");

    [Theory]
    [InlineData("renamed", "Passed")]
    [InlineData("required-case", "NotExecuted")]
    [InlineData("required-case", "Failed")]
    public void RejectsMissingRenamedOrSkippedExpectedCases(string name, string outcome)
    {
        var document = Results(("other", "Passed"), (name, outcome));
        Assert.Throws<InvalidOperationException>(() => ExerciseAssertions.RequireTests(document, ["required-case"], "policy"));
        Assert.Throws<InvalidOperationException>(() => ExerciseAssertions.RequireTests(Results(("other", "Passed")), ["required-case"], "policy"));
    }

    [Fact]
    public void RequiresEveryTheoryCaseWhilePermittingAdditionalPassingTests()
    {
        string[] expected = ["test(case: False)", "test(case: True)"];
        Assert.Throws<InvalidOperationException>(() => ExerciseAssertions.RequireTests(Results((expected[0], "Passed")), expected, "policy"));
        ExerciseAssertions.RequireTests(Results((expected[0], "Passed"), (expected[1], "Passed"), ("new", "Passed")), expected, "policy");
    }

    static XDocument Results(params (string Name, string Outcome)[] results)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return new(new XElement(ns + "TestRun", new XElement(ns + "Results", results.Select(result =>
            new XElement(ns + "UnitTestResult", new XAttribute("testName", result.Name), new XAttribute("outcome", result.Outcome))))));
    }
}

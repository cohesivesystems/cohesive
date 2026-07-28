using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionSourceMapTests
{
    [Fact]
    public void ResolveReferences_RemovesDefinitionEnvelopeAndSelectsDeepestReferencesDeterministically()
    {
        ExecutionSourceMap sourceMap = new(
        [
            new("source/shallow", new(["body", "steps"])),
            new("source/z", new(["body", "steps", "0"])),
            new("source/a", new(["body", "steps", "0"])),
            new("source/a", new(["body", "steps", "0"]), "Second construct at the same source")
        ]);

        var references = sourceMap.ResolveReferences(
            "/definition/body/steps/0/operation/value",
            "source/fallback");

        Assert.Equal(["source/a", "source/z"], references.ToArray());
        Assert.Equal(
            ["source/a", "source/z"],
            sourceMap.ResolveReferences("/body/steps/0/operation/value", "source/fallback").ToArray());
        Assert.Equal(
            ["source/fallback"],
            sourceMap.ResolveReferences("/definition/invariants/0", "source/fallback").ToArray());
        Assert.Equal(
            ["source/fallback"],
            sourceMap.ResolveReferences(location: null, "source/fallback").ToArray());
    }

    [Fact]
    public void ResolveReferences_DecodesJsonPointerEscapesBeforeStructuralPrefixMatching()
    {
        ExecutionSourceMap sourceMap = new(
        [
            new("source/escaped", new(["body", "a/b", "~case"])),
            new("source/separate-segments", new(["body", "a", "b", "~case"]))
        ]);

        var references = sourceMap.ResolveReferences(
            "/definition/body/a~1b/~0case/predicate",
            "source/fallback");

        Assert.Equal(["source/escaped"], references.ToArray());
        Assert.Equal(
            ["source/fallback"],
            sourceMap.ResolveReferences("/definition/body/a~2b/~0case", "source/fallback").ToArray());
    }

    [Fact]
    public void ResolveReferences_RequiresUsableFallbackReference()
    {
        Assert.Equal(
            "fallbackReference",
            Assert.Throws<ArgumentNullException>(() =>
                ExecutionSourceMap.Empty.ResolveReferences("/definition", null!)).ParamName);
        Assert.Equal(
            "fallbackReference",
            Assert.Throws<ArgumentException>(() =>
                ExecutionSourceMap.Empty.ResolveReferences("/definition", " ")).ParamName);
    }
}
